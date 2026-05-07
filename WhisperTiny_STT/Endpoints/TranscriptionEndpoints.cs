using Microsoft.AspNetCore.Mvc;
using WhisperTiny_STT.Services;
using System.Buffers;
using System.Text;
using System.Threading.Channels;

namespace WhisperTiny_STT.Endpoints;

public static class TranscriptionEndpoints
{
    // These constants define the capacity of the channels connecting the pipeline stages.
    // They can be tuned based on expected load and memory constraints.
    private const int AudioChannelCapacity = 500;
    private const int VadEventChannelCapacity = 100;

    public static void MapTranscriptionEndpoints(this IEndpointRouteBuilder app, bool enableTranscription, bool enableTranslation)
    {
        // =====================================================================
        // POST /v1/audio/transcriptions
        // =====================================================================
        if (enableTranscription)
        {
            app.MapPost("/v1/audio/transcriptions", async (
                IFormFile file,
                [FromForm] string? language,            // Kept for API compatibility. Ignored in processing since Sherpa-ONNX handles language at the Recognizer level.
                [FromForm] string? response_format,     // "text", "verbose_json", or default JSON with just the text
                [FromServices] AudioProcessor audioProcessor,
                [FromServices] VadProcessor vadProcessor,
                [FromKeyedServices("transcribe")] Transcriptor transcriptor,
                [FromServices] SemaphoreSlim inferenceSemaphore,
                [FromServices] IConfiguration config,
                CancellationToken ct) =>
            {
                // For backward compatibility, we still accept the 'language' parameter, 
                // but Sherpa-ONNX's Recognizer is initialized with a fixed language or auto-detect based on config.
                // If dynamic language switching per request is needed, 
                // it would require a more complex setup with multiple Recognizer instances or reinitialization, 
                // which is beyond the scope of this example.
                string actualLanguage = config["SttSettings:Language"] ?? "auto";
                // The 'response_format' parameter allows clients to specify how they want the response structured.
                return await HandleRequestAsync(file, response_format, "transcribe", actualLanguage,
                    audioProcessor, vadProcessor, transcriptor, inferenceSemaphore, ct);
            })
            .DisableAntiforgery()
            .WithName("CreateTranscription")
            .WithTags("Audio");
        }

        // =====================================================================
        // POST /v1/audio/translations
        // =====================================================================
        if (enableTranslation)
        {
            app.MapPost("/v1/audio/translations", async (
                IFormFile file,
                [FromForm] string? response_format,     // "text", "verbose_json", or default JSON with just the text
                [FromServices] AudioProcessor audioProcessor,
                [FromServices] VadProcessor vadProcessor,
                [FromKeyedServices("translate")] Transcriptor transcriptor,
                [FromServices] SemaphoreSlim inferenceSemaphore,
                CancellationToken ct) =>
            {
                // This endpoint is designed for translation, so we use the "translate" Transcriptor instance 
                // which is initialized with the target language set to English.
                return await HandleRequestAsync(file, response_format, "translate", "en",
                    audioProcessor, vadProcessor, transcriptor, inferenceSemaphore, ct);
            })
            .DisableAntiforgery()
            .WithName("CreateTranslation")
            .WithTags("Audio");
        }
    }

    // Centralized request handler for both transcription and translation endpoints to avoid code duplication.
    private static async Task<IResult> HandleRequestAsync(
        IFormFile file,
        string? responseFormat,
        string taskName,
        string outputLanguage,
        AudioProcessor audioProcessor,
        VadProcessor vadProcessor,
        Transcriptor transcriptor,
        SemaphoreSlim inferenceSemaphore,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = new { message = "Audio file is required." } });

        if (!await inferenceSemaphore.WaitAsync(TimeSpan.Zero, ct))
            return Results.Json(new { error = new { message = "Server is busy. Please retry shortly." } }, statusCode: 503);

        try
        {
            // Pipeline no longer requires a language hint
            string resultText = await RunPipelineAsync(file, audioProcessor, vadProcessor, transcriptor, ct);

            Console.ForegroundColor = taskName == "translate" ? ConsoleColor.Cyan : ConsoleColor.Green;
            Console.WriteLine($"[{taskName.ToUpper()}] {resultText}");
            Console.ResetColor();

            return FormatResponse(responseFormat, resultText, taskName, outputLanguage);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Pipeline failed: {ex.Message}");
            Console.ResetColor();
            return Results.Problem("An internal error occurred.", statusCode: 500);
        }
        finally
        {
            inferenceSemaphore.Release();
        }
    }

    // This method orchestrates the entire audio processing, VAD, 
    // and transcription pipeline using channels to connect each stage. 
    // It returns the final transcribed or translated text.
    private static async Task<string> RunPipelineAsync(
        IFormFile file,
        AudioProcessor audioProcessor,
        VadProcessor vadProcessor,
        Transcriptor transcriptor,
        CancellationToken ct)
    {
        var channel1 = Channel.CreateBounded<IMemoryOwner<float>>(new BoundedChannelOptions(AudioChannelCapacity)
        { SingleWriter = true, SingleReader = true });

        var channel2 = Channel.CreateBounded<(float[]? Audio, bool IsEndOfTurn)>(new BoundedChannelOptions(VadEventChannelCapacity)
        { SingleWriter = true, SingleReader = true });

        await using var fileStream = file.OpenReadStream();

        var audioTask = audioProcessor.ProcessStreamToChannelAsync(fileStream, channel1.Writer, ct);
        var vadTask = vadProcessor.ProcessVadChannelAsync(channel1.Reader, channel2.Writer, ct);

        var sb = new StringBuilder();
        // Transcriptor method call is now clean
        await foreach (var sentence in transcriptor.ProcessWhisperChannelAsync(channel2.Reader, ct))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }

        await Task.WhenAll(audioTask, vadTask);

        return sb.ToString();
    }

    // Formats the response based on the requested format.
    private static IResult FormatResponse(string? responseFormat, string text, string task, string language)
    {
        return responseFormat?.ToLowerInvariant() switch
        {
            "text" => Results.Text(text, contentType: "text/plain; charset=utf-8"),
            "verbose_json" => Results.Ok(new { task, language, text }),
            _ => Results.Ok(new { text }),
        };
    }
}