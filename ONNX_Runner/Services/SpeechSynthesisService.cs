using System.Diagnostics;
using ONNX_Runner.Models;
using ONNX_Runner.Services.Synthesis;

namespace ONNX_Runner.Services;
/// <summary>
/// Shared core synthesis pipeline. Every external API shape (OpenAI, Tsubaki's own
/// extended endpoint, and any future ones) funnels through this single service after its
/// own adapter translates the wire request into a SynthesisRequest — so there is exactly
/// one place where the request concurrency gate is acquired and released, no matter how many
/// different endpoints exist. Endpoints and adapters should never call the generation
/// pipeline or touch the request gate directly; they should only ever call SynthesizeAsync.
/// </summary>
public partial class SpeechSynthesisService(
    SemaphoreSlim requestGate,
    IServiceProvider services,
    NativeAudioDependencies nativeAudioDependencies,
    ILogger<SpeechSynthesisService> logger)
{
    private static long s_requestSequence = -1;
    /// <summary>
    /// Executes one validated synthesis request under the shared concurrency limit.
    /// Assigns a lightweight process-local request ID and records queue/generation timing.
    /// </summary>
    public async Task<IResult> SynthesizeAsync(
        SynthesisRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        long requestId = Interlocked.Increment(ref s_requestSequence);
        long requestStartedTimestamp = Stopwatch.GetTimestamp();
        using var requestLogContext = RequestLogContext.Push(requestId);

        string requestedVoice = string.IsNullOrWhiteSpace(request.Voice)
            ? "piper_base"
            : request.Voice;

        if (logger.IsEnabled(LogLevel.Information))
        {
            LogRequestReceived(
                logger,
                request.Input.Length,
                requestedVoice,
                request.Format,
                request.StreamFormat,
                request.Stream?.ToString() ?? "default");
        }
        // =================================================================
        // REQUEST VALIDATION
        // =================================================================
        // Wire-format validation belongs to the adapter. Runtime capability checks live here
        // because every external endpoint funnels through this service.
        if (!nativeAudioDependencies.IsFormatAvailable(request.Format))
        {
            string formatName = request.Format == AudioFormat.B64Json
                ? "b64_json"
                : request.Format.ToString().ToLowerInvariant();
            logger.LogWarning(
                "Request rejected because LAME is unavailable | format={Format}",
                formatName);
            return Results.Problem(
                title: "Requested audio format is unavailable",
                detail:
                    $"'{formatName}' requires the native LAME MP3 library, " +
                    "which was not found on this system. Install your distribution's " +
                    "libmp3lame runtime package or request wav, flac, opus, aac, or pcm.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
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
            logger.LogWarning(
                "Input truncated to {CharacterCount} characters by MaxTextLength",
                request.Input.Length);
        }
        // Safely verify if the base TTS model was successfully loaded at startup.
        // If not, we return a 500 Internal Server Error without crashing the server.
        var piperConfig = services.GetService<PiperConfig>();
        if (piperConfig == null)
        {
            logger.LogError("Request rejected because the Piper model is not loaded");
            return Results.Problem("Model is not loaded properly.", statusCode: 500);
        }
        var streamConfig = services.GetRequiredService<StreamSettings>();

        ResponsePipeline.State response = default;
        ResponsePipeline.Plan responsePlan = default;
        bool responsePlanResolved = false;
        bool gateAcquired = false;
        bool generationStarted = false;

        long generationStartedTimestamp = 0;
        TimeSpan queueElapsed = TimeSpan.Zero;
        try
        {
            // =================================================================
            // CONCURRENCY CONTROL (SEMAPHORE PATTERN)
            // =================================================================
            // Measure only time actually spent waiting for a concurrency slot. Request parsing,
            // validation, and response setup are deliberately excluded from this queue metric.
            long queueStartedTimestamp = Stopwatch.GetTimestamp();
            await requestGate.WaitAsync(cancellationToken);

            queueElapsed = Stopwatch.GetElapsedTime(queueStartedTimestamp);
            gateAcquired = true;
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
            // Resolve payload format, response framing, and delivery timing independently.
            // The plan is a small value type, so adding SSE does not allocate formatter/factory
            // objects or leak transport conditions into the actual generation pipeline.
            responsePlan = ResponsePipeline.Resolve(request, ctx, streamConfig);
            responsePlanResolved = true;
            ResponsePipeline.CreateTransport(
                ref response,
                responsePlan);

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
                    responsePlan,
                    httpContext,
                    cancellationToken);
            }
            // Wrap the transport only when the selected payload/framing combination requires it.
            // Normal audio writes directly to the raw stream; B64Json uses its existing
            // Base64EncodingStream for normal responses, while SSE owns Base64 event framing.
            ResponsePipeline.OpenPayload(
                ref response,
                responsePlan);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                float effectivePitch = request.Pitch ?? ctx.DspConfig.DefaultPitch;
                string effectiveEffect = request.Effect ?? ctx.EffectsConfig.DefaultEffect;
                string effectiveEnvironment = request.Environment ?? ctx.EffectsConfig.DefaultEnvironment;
                logger.LogDebug(
                    "Generation started | queue={QueueMs:F1} ms | clone={Clone} | stream={Stream} | stream_format={StreamFormat} | speed={SpeechSpeed:F2} | pitch={Pitch:F2} | effect={Effect} | environment={Environment} | sample_rate={SampleRate} Hz",
                    queueElapsed.TotalMilliseconds,
                    ctx.CanClone ? "yes" : "no",
                    responsePlan.UseStreaming ? "yes" : "no",
                    responsePlan.StreamFormat,
                    request.Speed,
                    effectivePitch,
                    effectiveEffect,
                    effectiveEnvironment,
                    ctx.FinalSampleRate);
            }
            generationStarted = true;
            generationStartedTimestamp = Stopwatch.GetTimestamp();
            // =================================================================
            // ASYNCHRONOUS AUDIO GENERATION (PRODUCER-CONSUMER PATTERN)
            // =================================================================
            // Base synthesis and post-processing remain independent producer/consumer stages.
            // Different hardware can therefore work concurrently, while bounded channels apply
            // backpressure when one stage outruns another and propagate failures across the pipe.
            AudioGenerationPipeline.GenerationResult generationResult =
                await AudioGenerationPipeline.GenerateAsync(
                    ctx,
                    response.TargetStream!,
                    responsePlan.FlushAfterEachSentence,
                    cancellationToken);
            TimeSpan generationElapsed =
                Stopwatch.GetElapsedTime(generationStartedTimestamp);
            // =================================================================
            // RESPONSE FINALIZATION
            // =================================================================
            // Finalize only external payload framing. Normal B64Json finalizes its continuous
            // Base64 JSON object here; SSE framing is finalized by its network sender instead.
            ResponsePipeline.ClosePayload(
                ref response,
                responsePlan);
            IResult result = await ResponsePipeline.CompleteAsync(
                response,
                responsePlan,
                request,
                httpContext,
                cancellationToken);

            TimeSpan totalElapsed =
                Stopwatch.GetElapsedTime(requestStartedTimestamp);
            if (logger.IsEnabled(LogLevel.Information))
            {
                double audioSeconds = generationResult.AudioSeconds;
                double generationSeconds = generationElapsed.TotalSeconds;
                double rtf = audioSeconds > 0.0
                    ? generationSeconds / audioSeconds
                    : 0.0;
                double realtimeSpeed = generationSeconds > 0.0
                    ? audioSeconds / generationSeconds
                    : 0.0;
                LogGenerationCompleted(
                    logger,
                    audioSeconds,
                    generationSeconds,
                    rtf,
                    realtimeSpeed,
                    queueElapsed.TotalMilliseconds,
                    totalElapsed.TotalSeconds);
            }
            return result;
        }
        catch (Exception ex) when (
            ex is OperationCanceledException ||
            cancellationToken.IsCancellationRequested ||
            httpContext.RequestAborted.IsCancellationRequested)
        {
            // A client disconnect can surface as a closed output stream rather than an
            // OperationCanceledException. Both paths are a canceled request.
            // Discard unfinished payload framing and release network buffers.
            await ResponsePipeline.AbortAsync(response);

            TimeSpan totalElapsed =
                Stopwatch.GetElapsedTime(requestStartedTimestamp);

            logger.LogWarning(
                "Request canceled | stage={Stage} | total={TotalSeconds:F3} s",
                GetStage(gateAcquired, generationStarted),
                totalElapsed.TotalSeconds);
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

            TimeSpan totalElapsed =
                Stopwatch.GetElapsedTime(requestStartedTimestamp);
            logger.LogError(
                ex,
                "Request failed | stage={Stage} | total={TotalSeconds:F3} s",
                GetStage(gateAcquired, generationStarted),
                totalElapsed.TotalSeconds);
            if (httpContext.Response.HasStarted)
            {
                // If streaming already started, we can't send a 500 status code anymore.
                // The response channel has already been faulted so every pipeline stage can
                // terminate instead of continuing expensive inference for a dead connection.
                return Results.Empty;
            }
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
        finally
        {
            ResponsePipeline.Dispose(ref response);

            // CRITICAL: Only release the request gate if WaitAsync actually acquired it.
            // A cancellation while still waiting must never increment the request gate count.
            if (gateAcquired)
            {
                requestGate.Release();
            }
        }
    }
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Request received | chars={CharacterCount} | voice={Voice} | format={Format} | stream_format={StreamFormat} | stream={Stream}",
        SkipEnabledCheck = true)]
    private static partial void LogRequestReceived(
        ILogger logger,
        int characterCount,
        string voice,
        AudioFormat format,
        SpeechStreamFormat streamFormat,
        string stream);
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Generation completed | audio={AudioSeconds:F3} s | generation={GenerationSeconds:F3} s | RTF={Rtf:F3} | speed={Speed:F2}x | queue={QueueMs:F1} ms | total={TotalSeconds:F3} s",
        SkipEnabledCheck = true)]
    private static partial void LogGenerationCompleted(
        ILogger logger,
        double audioSeconds,
        double generationSeconds,
        double rtf,
        double speed,
        double queueMs,
        double totalSeconds);
    private static string GetStage(bool gateAcquired, bool generationStarted)
    {
        if (!gateAcquired)
        {
            return "queue";
        }

        return generationStarted
            ? "generation"
            : "setup";
    }
}
