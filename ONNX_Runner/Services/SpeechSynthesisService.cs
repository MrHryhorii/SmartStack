using ONNX_Runner.Models;
using ONNX_Runner.Services.Synthesis;

namespace ONNX_Runner.Services;

/// <summary>
/// Shared core synthesis pipeline. Every external API shape (OpenAI, Tsubaki's own
/// extended endpoint, and any future ones) funnels through this single service after its
/// own adapter translates the wire request into a SynthesisRequest — so there is exactly
/// one place where the GPU/CPU semaphore is acquired and released, no matter how many
/// different endpoints exist. Endpoints and adapters should never call the generation
/// pipeline or touch the semaphore directly; they should only ever call SynthesizeAsync.
/// </summary>
public class SpeechSynthesisService(SemaphoreSlim gpuSemaphore, IServiceProvider services)
{
    public async Task<IResult> SynthesizeAsync(
        SynthesisRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // =================================================================
        // REQUEST VALIDATION
        // =================================================================
        // Wire-format-specific validation (empty input, invalid response_format string) is
        // the adapter's responsibility, done before this method is ever called — by the time
        // a SynthesisRequest reaches here, Input is non-empty and Format is already resolved.

        // =================================================================
        // TEXT LENGTH LIMITATION (OOM PROTECTION)
        // =================================================================
        // Protects the server from Out-Of-Memory errors and GPU timeout limits.
        // If the client sends a massive block of text (like a whole book in one request),
        // we smoothly truncate it to the allowed limit rather than rejecting the entire request.
        var apiSettings = services.GetRequiredService<ApiSettings>();
        if (apiSettings.MaxTextLength > 0 && request.Input.Length > apiSettings.MaxTextLength)
        {
            request.Input = request.Input[..apiSettings.MaxTextLength];
        }

        // Safely verify if the base TTS model was successfully loaded at startup.
        // If not, we return a 500 Internal Server Error without crashing the server.
        var piperConfig = services.GetService<PiperConfig>();
        if (piperConfig == null)
        {
            return Results.Problem("Model is not loaded properly.", statusCode: 500);
        }

        var streamConfig = services.GetRequiredService<StreamSettings>();

        ResponsePipeline.State response = default;
        ResponsePipeline.Plan responsePlan = default;
        bool responsePlanResolved = false;
        bool semaphoreAcquired = false;

        try
        {
            // =================================================================
            // CONCURRENCY CONTROL (SEMAPHORE PATTERN)
            // =================================================================
            // Wait for an available slot in the execution queue. This strictly limits
            // concurrent ONNX inferences to prevent GPU VRAM Out-Of-Memory (OOM) errors
            // or CPU thread starvation.
            await gpuSemaphore.WaitAsync(cancellationToken);
            semaphoreAcquired = true;

            // =================================================================
            // REQUEST CONTEXT
            // =================================================================
            // Resolve all synthesis-only state once. Response formatting and HTTP transport
            // remain outside this context so future response formats cannot contaminate the
            // actual audio generation pipeline.
            var ctx = SynthesisContextBuilder.Build(request, services, piperConfig);

            // =================================================================
            // RESPONSE PREPARATION
            // =================================================================
            // Resolve how the response must be represented and delivered. The plan is a small
            // value type, so selecting raw audio vs Base64 JSON and buffered vs streaming output
            // does not require allocating formatter/transport/factory objects per request.
            responsePlan = ResponsePipeline.Resolve(request, ctx, streamConfig);
            responsePlanResolved = true;

            ResponsePipeline.CreateTransport(
                ref response,
                responsePlan,
                streamConfig);

            ResponsePipeline.ConfigureHeaders(
                responsePlan,
                request,
                ctx,
                httpContext);

            if (responsePlan.UseStreaming)
            {
                await httpContext.Response.StartAsync(cancellationToken);

                ResponsePipeline.StartNetworkSender(
                    ref response,
                    httpContext,
                    cancellationToken);
            }

            // Wrap the transport only when the external response format requires it.
            // Normal audio writes directly to the raw stream; B64Json inserts a lightweight
            // Base64EncodingStream while the generation pipeline remains completely unaware.
            ResponsePipeline.OpenPayload(
                ref response,
                responsePlan);

            // =================================================================
            // ASYNCHRONOUS AUDIO GENERATION (PRODUCER-CONSUMER PATTERN)
            // =================================================================
            // Base synthesis and post-processing remain independent producer/consumer stages.
            // Different hardware can therefore work concurrently, while bounded channels apply
            // backpressure when one stage outruns another and propagate failures across the pipe.
            await AudioGenerationPipeline.GenerateAsync(
                ctx,
                response.TargetStream!,
                responsePlan.FlushAfterEachSentence,
                cancellationToken);

            // =================================================================
            // RESPONSE FINALIZATION
            // =================================================================
            // Finalize only the external payload framing. For B64Json this flushes Base64
            // padding and appends the closing JSON suffix; ordinary audio is a no-op here.
            ResponsePipeline.ClosePayload(
                ref response,
                responsePlan);

            return await ResponsePipeline.CompleteAsync(
                response,
                responsePlan,
                request);
        }
        catch (OperationCanceledException)
        {
            // Triggered if the client disconnects/cancels the request midway through generation.
            // Best-effort payload finalization preserves buffered B64Json correctness and releases
            // wrapper state; network cleanup then returns all queued ArrayPool buffers.
            if (responsePlanResolved)
            {
                ResponsePipeline.TryClosePayload(
                    ref response,
                    responsePlan);
            }

            await ResponsePipeline.AbortAsync(response);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] Client disconnected. Generation stopped to save resources.");
            Console.ResetColor();

            return Results.Empty;
        }
        catch (Exception ex)
        {
            if (responsePlanResolved)
            {
                ResponsePipeline.TryClosePayload(
                    ref response,
                    responsePlan);
            }

            await ResponsePipeline.AbortAsync(response, ex);

            if (httpContext.Response.HasStarted)
            {
                // If streaming already started, we can't send a 500 status code anymore.
                // The response channel has already been faulted so every pipeline stage can
                // terminate instead of continuing expensive inference for a dead connection.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] Stream aborted unexpectedly: {ex.Message}");
                Console.ResetColor();

                return Results.Empty;
            }

            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
        finally
        {
            ResponsePipeline.Dispose(ref response);

            // CRITICAL: Only release the semaphore if WaitAsync actually acquired it.
            // A cancellation while still waiting must never increment the semaphore count.
            if (semaphoreAcquired)
            {
                gpuSemaphore.Release();
            }
        }
    }
}
