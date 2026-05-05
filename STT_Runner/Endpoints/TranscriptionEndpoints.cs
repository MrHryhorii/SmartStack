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
///
/// Concurrency guard
/// ─────────────────
/// A <see cref="SemaphoreSlim"/> injected from DI limits how many requests run the
/// AI pipeline simultaneously. Requests that arrive when all slots are occupied are
/// rejected immediately with 503 rather than queued, keeping memory and latency bounded.
/// The limit is configured via <c>ServerSecurity:MaxConcurrentInference</c>.
/// </summary>
public static class TranscriptionEndpoints
{
    // Channel 1 (AudioProcessor → VAD): larger — 512-sample chunks arrive very fast.
    private const int AudioChannelCapacity = 500;

    // Channel 2 (VAD → Whisper): small — each item is a full sentence (many kilobytes).
    private const int SentenceChannelCapacity = 20;

    public static void MapTranscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        // =====================================================================
        // POST /v1/audio/transcriptions
        // =====================================================================
        // OpenAI reference: https://platform.openai.com/docs/api-reference/audio/createTranscription
        //
        // Accepted multipart/form-data fields:
        //   file            (required) — audio file in any format NAudio supports
        //   model           (optional) — accepted but ignored; model path comes from config
        //   language        (optional) — BCP-47 source language hint, e.g. "uk", "en"
        //   response_format (optional) — "json" | "text" | "verbose_json"  (default: "json")
        // =====================================================================
        app.MapPost("/v1/audio/transcriptions", async (
            IFormFile file,
            [FromForm] string? model,
            [FromForm] string? language,
            [FromForm] string? response_format,
            [FromServices] AudioProcessor audioProcessor,
            [FromServices] VadProcessor vadProcessor,
            [FromServices] Transcriptor transcriptor,
            [FromServices] SemaphoreSlim inferenceSemaphore,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest(new
                {
                    error = new { message = "Audio file is required.", type = "invalid_request_error" }
                });

            // Try to enter the semaphore without waiting.
            // If all inference slots are occupied, return 503 immediately.
            if (!await inferenceSemaphore.WaitAsync(TimeSpan.Zero, ct))
            {
                return Results.Json(
                    new { error = new { message = "Server is busy. Please retry shortly.", type = "server_busy" } },
                    statusCode: 503);
            }

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
                return Results.StatusCode(499); // Nginx convention: Client Closed Request
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Transcription failed: {ex.Message}");
                Console.ResetColor();
                return Results.Problem("An internal error occurred during transcription.", statusCode: 500);
            }
            finally
            {
                // Always release the slot, even if the pipeline threw.
                inferenceSemaphore.Release();
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
        // The `language` parameter is intentionally absent — the OpenAI Translations
        // endpoint does not accept it; Whisper auto-detects the source language
        // internally via the WithTranslate() decoder task.
        //
        // Accepted multipart/form-data fields:
        //   file            (required) — audio file
        //   model           (optional) — accepted but ignored
        //   response_format (optional) — "json" | "text" | "verbose_json"  (default: "json")
        // =====================================================================
        app.MapPost("/v1/audio/translations", async (
            IFormFile file,
            [FromForm] string? model,
            [FromForm] string? response_format,
            [FromServices] AudioProcessor audioProcessor,
            [FromServices] VadProcessor vadProcessor,
            [FromServices] Transcriptor transcriptor,
            [FromServices] SemaphoreSlim inferenceSemaphore,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest(new
                {
                    error = new { message = "Audio file is required.", type = "invalid_request_error" }
                });

            if (!await inferenceSemaphore.WaitAsync(TimeSpan.Zero, ct))
            {
                return Results.Json(
                    new { error = new { message = "Server is busy. Please retry shortly.", type = "server_busy" } },
                    statusCode: 503);
            }

            try
            {
                string translated = await RunPipelineAsync(
                    file, audioProcessor, vadProcessor, transcriptor,
                    languageHint: null, // Source language is auto-detected by Whisper's translation task.
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
            finally
            {
                inferenceSemaphore.Release();
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
    /// audio file and returns the concatenated transcript.
    ///
    /// Ordering matters: stage 3 (Whisper) drains channel2 inline before we await the
    /// background tasks. Awaiting stage 1/2 first would deadlock because VAD blocks
    /// writing to the full channel2 while Whisper hasn't started reading yet.
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
        // Create the two channels that connect the pipeline stages. 
        // Both are bounded to prevent unbounded memory growth when downstream stages are slower than upstream ones.'
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

        // Stages 1 and 2 run as fire-and-forget tasks; exceptions are captured and
        // re-thrown by Task.WhenAll after stage 3 has finished draining the channel.
        var audioTask = audioProcessor.ProcessStreamToChannelAsync(fileStream, channel1.Writer, ct);
        var vadTask = vadProcessor.ProcessVadChannelAsync(channel1.Reader, channel2.Writer, ct);

        // Stage 3 runs inline so we can stream the transcript sentences as they arrive.
        var sb = new StringBuilder();
        await foreach (var sentence in transcriptor.ProcessWhisperChannelAsync(
                           channel2.Reader, languageHint, translate, ct))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }

        // Both channels are completed at this point; WhenAll surfaces any background exceptions.
        await Task.WhenAll(audioTask, vadTask);

        return sb.ToString();
    }

    // ── Response formatting ───────────────────────────────────────────────────

    /// <summary>
    /// Formats the transcript per the OpenAI <c>response_format</c> contract.
    /// Unknown values fall back to <c>json</c>, matching OpenAI's behaviour.
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

            "verbose_json" => Results.Ok(new { task, language, text }),

            // Default: plain { "text": "..." } — identical to OpenAI's response shape.
            _ => Results.Ok(new { text }),
        };
    }
}