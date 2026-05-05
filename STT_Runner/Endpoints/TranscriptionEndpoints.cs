using Microsoft.AspNetCore.Mvc;
using STT_Runner.Services;
using System.Buffers;
using System.Text;
using System.Threading.Channels;

namespace STT_Runner.Endpoints;

/// <summary>
/// OpenAI-compatible Speech-to-Text endpoints.
///
/// Implements the same routes, parameters, and response shapes as the OpenAI Audio API
/// so any client using the official OpenAI SDK can point at this server with zero changes.
///
/// Routes
/// ──────
/// POST /v1/audio/transcriptions — transcribes speech in its original language.
/// POST /v1/audio/translations   — transcribes speech and translates the result to English.
///
/// Supported response_format values
/// ─────────────────────────────────
/// "json"         (default) → { "text": "..." }
/// "text"                   → plain text body
/// "verbose_json"           → { "task": ..., "language": ..., "text": "..." }
/// </summary>
public static class TranscriptionEndpoints
{
    // ── Channel capacity constants ────────────────────────────────────────────
    // Channel 1 (AudioProcessor → VAD): larger buffer because 512-sample chunks arrive fast.
    private const int AudioChannelCapacity = 500;

    // Channel 2 (VAD → Whisper): small — each item is a full sentence (potentially megabytes).
    private const int SentenceChannelCapacity = 20;

    public static void MapTranscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        // =====================================================================
        // POST /v1/audio/transcriptions
        // =====================================================================
        // OpenAI reference: https://platform.openai.com/docs/api-reference/audio/createTranscription
        //
        // Accepted form fields:
        //   file            (required) — audio file
        //   model           (ignored)  — kept for API compatibility; model path comes from config
        //   language        (optional) — BCP-47 source language hint, e.g. "uk", "en"; defaults to "auto"
        //   response_format (optional) — "json" | "text" | "verbose_json"; defaults to "json"
        // =====================================================================
        app.MapPost("/v1/audio/transcriptions", async (
            HttpContext context,
            IFormFile file,
            [FromForm] string? model,
            [FromForm] string? language,
            [FromForm] string? response_format,
            [FromServices] AudioProcessor audioProcessor,
            [FromServices] VadProcessor vadProcessor,
            [FromServices] Transcriptor transcriptor,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = new { message = "Audio file is required.", type = "invalid_request_error" } });

            try
            {
                string transcript = await RunPipelineAsync(
                    file, audioProcessor, vadProcessor, transcriptor,
                    languageHint: language,
                    translate: false,
                    ct);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[TRANSCRIPTION] {transcript}");
                Console.ResetColor();

                return FormatResponse(response_format, transcript, task: "transcribe", language: language ?? "auto");
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499); // Client Closed Request
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Transcription failed: {ex.Message}");
                Console.ResetColor();
                return Results.Problem("An internal error occurred during transcription.", statusCode: 500);
            }
        })
        .DisableAntiforgery()
        .WithName("CreateTranscription")
        .WithSummary("Transcribes audio into the input language.")
        .WithTags("Audio");

        // =====================================================================
        // POST /v1/audio/translations
        // =====================================================================
        // OpenAI reference: https://platform.openai.com/docs/api-reference/audio/createTranslation
        //
        // Always outputs English regardless of the source language.
        // Source language is auto-detected by Whisper — no language parameter accepted,
        // matching the OpenAI API contract exactly.
        //
        // Accepted form fields:
        //   file            (required) — audio file
        //   model           (ignored)  — kept for API compatibility
        //   response_format (optional) — "json" | "text" | "verbose_json"; defaults to "json"
        // =====================================================================
        app.MapPost("/v1/audio/translations", async (
            HttpContext context,
            IFormFile file,
            [FromForm] string? model,
            [FromForm] string? response_format,
            [FromServices] AudioProcessor audioProcessor,
            [FromServices] VadProcessor vadProcessor,
            [FromServices] Transcriptor transcriptor,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = new { message = "Audio file is required.", type = "invalid_request_error" } });

            try
            {
                string translated = await RunPipelineAsync(
                    file, audioProcessor, vadProcessor, transcriptor,
                    languageHint: null,  // auto-detect source; Whisper handles this internally
                    translate: true,
                    ct);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[TRANSLATION] {translated}");
                Console.ResetColor();

                return FormatResponse(response_format, translated, task: "translate", language: "en");
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Translation failed: {ex.Message}");
                Console.ResetColor();
                return Results.Problem("An internal error occurred during translation.", statusCode: 500);
            }
        })
        .DisableAntiforgery()
        .WithName("CreateTranslation")
        .WithSummary("Translates audio into English.")
        .WithTags("Audio");
    }

    // ── Shared pipeline ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full three-stage pipeline (AudioProcessor → VAD → Whisper) for a single
    /// audio file and returns the concatenated transcript as a single string.
    ///
    /// The two background tasks (audio chunking and VAD segmentation) are awaited after
    /// the Whisper enumeration finishes, so any exception from either stage propagates
    /// to the caller rather than being silently swallowed.
    /// </summary>
    private static async Task<string> RunPipelineAsync(
        IFormFile file,
        AudioProcessor audioProcessor,
        VadProcessor vadProcessor,
        Transcriptor transcriptor,
        string? languageHint,
        bool translate,
        CancellationToken ct)
    {
        // Bounded channels apply back-pressure so a slow Whisper cannot cause
        // the audio or VAD stages to buffer unbounded data in memory.
        var channel1 = Channel.CreateBounded<IMemoryOwner<float>>(
            new BoundedChannelOptions(AudioChannelCapacity)
            {
                SingleWriter = true,
                SingleReader = true,
            });

        var channel2 = Channel.CreateBounded<(IMemoryOwner<float>, int)>(
            new BoundedChannelOptions(SentenceChannelCapacity)
            {
                SingleWriter = true,
                SingleReader = true,
            });

        await using var fileStream = file.OpenReadStream();

        // Stage 1 and 2 run concurrently in the background.
        // Do not await them yet — we need to drain the channel first.
        var audioTask = audioProcessor.ProcessStreamToChannelAsync(fileStream, channel1.Writer, ct);
        var vadTask = vadProcessor.ProcessVadChannelAsync(channel1.Reader, channel2.Writer, ct);

        // Stage 3 runs inline, streaming sentences out of channel2 as they arrive.
        var sb = new StringBuilder();
        await foreach (var sentence in transcriptor.ProcessWhisperChannelAsync(
                           channel2.Reader, languageHint, translate, ct))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }

        // Await background tasks to surface any exceptions from stages 1 and 2.
        // Both channels are already completed at this point (TryComplete was called
        // in their respective finally blocks), so this should resolve immediately.
        await Task.WhenAll(audioTask, vadTask);

        return sb.ToString();
    }

    // ── Response formatting ───────────────────────────────────────────────────

    /// <summary>
    /// Formats the transcript according to the requested <paramref name="responseFormat"/>.
    /// Unrecognised format values fall back to "json" to match OpenAI's behaviour.
    /// </summary>
    private static IResult FormatResponse(
        string? responseFormat,
        string text,
        string task,
        string language)
    {
        return responseFormat?.ToLowerInvariant() switch
        {
            "text" => Results.Text(text, contentType: "text/plain; charset=utf-8"),

            "verbose_json" => Results.Ok(new
            {
                task,
                language,
                text,
            }),

            // "json" is the default, matches exactly what OpenAI returns.
            _ => Results.Ok(new { text }),
        };
    }
}