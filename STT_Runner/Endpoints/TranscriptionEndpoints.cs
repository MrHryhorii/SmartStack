using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using STT_Runner.Services;
using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace STT_Runner.Endpoints;

public static class TranscriptionEndpoints
{
    // Documentation-only shape; the endpoint reads multipart sections directly.
    public sealed class UploadForm
    {
        public string? Language { get; set; }
        [Required]
        public IFormFile File { get; set; } = default!;
        public string? Model { get; set; }
        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; }
    }

    private const int AudioChannelCapacity = 32;
    private const int SentenceChannelCapacity = 3;
    private const int MaxBoundaryLength = 128;
    private const int MaxFieldBytes = 8192;

    public static void MapTranscriptionEndpoints(this IEndpointRouteBuilder app, bool rateLimitingEnabled)
    {
        MapEndpoint(app, "/v1/audio/transcriptions", translate: false, rateLimitingEnabled);
        MapEndpoint(app, "/v1/audio/translations", translate: true, rateLimitingEnabled);
    }

    private static void MapEndpoint(IEndpointRouteBuilder app, string route, bool translate, bool rateLimitingEnabled)
    {
        var endpoint = app.MapPost(route, (HttpContext context, AudioProcessor audio,
            VadProcessor vad, Transcriptor whisper, RequestSlots slots, IConfiguration config) =>
            HandleAsync(context, audio, vad, whisper, slots, config, translate))
            .DisableAntiforgery()
            .WithTags("Audio")
            .WithName(translate ? "CreateTranslation" : "CreateTranscription");

        if (rateLimitingEnabled)
            endpoint.RequireRateLimiting("SttRateLimit");
    }

    private static async Task<IResult> HandleAsync(HttpContext context, AudioProcessor audio,
        VadProcessor vad, Transcriptor whisper, RequestSlots slots, IConfiguration config, bool translate)
    {
        CancellationToken ct = context.RequestAborted;
        try
        {
            using var requestLease = await slots.AcquireAsync(ct);
            if (!requestLease.IsAcquired)
                return Error("Server queue is full. Please retry shortly.", 503);

            string? format = context.Request.Query["response_format"];
            string? language = context.Request.Query["language"];
            bool streamResponse = config.GetValue<bool>("SttSettings:StreamResponse", false);
            bool wroteSentence = false;
            Func<string, CancellationToken, Task>? sendSentence = null;
            if (streamResponse)
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                sendSentence = async (sentence, token) =>
                {
                    await context.Response.WriteAsync(wroteSentence ? " " + sentence : sentence, token);
                    await context.Response.Body.FlushAsync(token);
                    wroteSentence = true;
                };
            }
            string transcript;

            if (context.Request.HasFormContentType)
            {
                (transcript, format, language) = await ProcessMultipartAsync(context.Request, audio, vad,
                    whisper, language, format, translate, sendSentence, ct);
            }
            else if (IsRawAudio(context.Request.ContentType))
            {
                transcript = await RunPipelineAsync(context.Request.Body, audio, vad, whisper,
                    translate ? null : language, translate, sendSentence, ct);
            }
            else
            {
                return Error("Send multipart/form-data or raw audio with an audio/* or application/octet-stream content type.", 415);
            }

            Console.WriteLine($"[{(translate ? "TRANSLATION" : "TRANSCRIPTION")}] {transcript}");
            if (streamResponse) return Results.Empty;
            return FormatResponse(format, transcript, translate, language);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (context.Response.HasStarted) return Results.Empty;
            return Results.StatusCode(499);
        }
        catch (OperationCanceledException)
        {
            return Error("Server queue wait timed out. Please retry shortly.", 503);
        }
        catch (InvalidDataException ex)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return Results.Empty;
            }
            return Error(ex.Message, 400);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Transcription failed: {ex}");
            if (context.Response.HasStarted)
            {
                context.Abort();
                return Results.Empty;
            }
            return Results.Problem("An internal error occurred during transcription.", statusCode: 500);
        }
    }

    private static async Task<(string Transcript, string? Format, string? Language)> ProcessMultipartAsync(
        HttpRequest request, AudioProcessor audio, VadProcessor vad, Transcriptor whisper,
        string? language, string? format, bool translate,
        Func<string, CancellationToken, Task>? sendSentence, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
            throw new InvalidDataException("Invalid multipart content type.");

        string boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value ?? "";
        if (boundary.Length is 0 or > MaxBoundaryLength)
            throw new InvalidDataException("Missing or oversized multipart boundary.");

        var reader = new MultipartReader(boundary, request.Body)
        {
            BodyLengthLimit = 512L * 1024 * 1024,
            HeadersLengthLimit = 16 * 1024
        };
        string? transcript = null;
        bool fileSeen = false;
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.DispositionType.Equals("form-data"))
                throw new InvalidDataException("Invalid multipart section.");

            string fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
            bool isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
            if (isFile)
            {
                if (fieldName != "file" || fileSeen)
                    throw new InvalidDataException("Exactly one audio file field named 'file' is required.");

                fileSeen = true;
                transcript = await RunPipelineAsync(section.Body, audio, vad, whisper,
                    translate ? null : language, translate, sendSentence, ct);
                continue;
            }

            string value = await ReadFieldAsync(section.Body, ct);
            switch (fieldName)
            {
                case "language" when !translate:
                    if (fileSeen && !string.Equals(language ?? "auto", value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The language field must appear before the file field for streaming transcription.");
                    language = value;
                    break;
                case "response_format":
                    format = value;
                    break;
                case "model":
                    break;
            }
        }

        if (!fileSeen) throw new InvalidDataException("Audio file is required.");
        return (transcript ?? "", format, language);
    }

    private static async Task<string> ReadFieldAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxFieldBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) return Encoding.UTF8.GetString(buffer, 0, total);
            total += read;
        }
        throw new InvalidDataException("Multipart field exceeds 8192 bytes.");
    }

    private static bool IsRawAudio(string? contentType) =>
        contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
        contentType?.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string> RunPipelineAsync(Stream input, AudioProcessor audio,
        VadProcessor vad, Transcriptor whisper, string? language, bool translate,
        Func<string, CancellationToken, Task>? sendSentence, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = linked.Token;
        var pcm = Channel.CreateBounded<IMemoryOwner<float>>(new BoundedChannelOptions(AudioChannelCapacity)
        {
            SingleWriter = true,
            SingleReader = true
        });
        var sentences = Channel.CreateBounded<(IMemoryOwner<float> Owner, int Length)>(
            new BoundedChannelOptions(SentenceChannelCapacity)
            {
                SingleWriter = true,
                SingleReader = true
            });

        Task producer = audio.ProcessStreamToChannelAsync(input, pcm.Writer, token);
        Task segmenter = vad.ProcessVadChannelAsync(pcm.Reader, sentences.Writer, token);
        var text = new StringBuilder();
        try
        {
            await foreach (string sentence in whisper.ProcessWhisperChannelAsync(sentences.Reader,
                language, translate, token))
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(sentence);
                if (sendSentence is not null)
                    await sendSentence(sentence, token);
            }
            await Task.WhenAll(producer, segmenter);
            return text.ToString();
        }
        catch
        {
            linked.Cancel();
            try { await Task.WhenAll(producer, segmenter); } catch (Exception) { }
            throw;
        }
        finally
        {
            while (pcm.Reader.TryRead(out var owner)) owner.Dispose();
            while (sentences.Reader.TryRead(out var sentence)) sentence.Owner.Dispose();
        }
    }

    private static IResult FormatResponse(string? format, string text, bool translate, string? language) =>
        format?.ToLowerInvariant() switch
        {
            "text" => Results.Text(text, "text/plain; charset=utf-8"),
            "verbose_json" => Results.Ok(new
            {
                task = translate ? "translate" : "transcribe",
                language = translate ? "en" : language ?? "auto",
                text
            }),
            _ => Results.Ok(new { text })
        };

    private static IResult Error(string message, int statusCode) => Results.Json(
        new { error = new { message, type = "invalid_request_error" } }, statusCode: statusCode);
}
