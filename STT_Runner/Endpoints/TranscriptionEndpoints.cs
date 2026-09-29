using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using STT_Runner.Services;
using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whisper.net;

namespace STT_Runner.Endpoints;

public static class TranscriptionEndpoints
{
    // Swagger describes the multipart API; the handler reads sections itself.
    public sealed class UploadForm
    {
        [Required]
        public IFormFile File { get; set; } = default!;
        public string? Model { get; set; }
        public string? Language { get; set; }
        public string? Prompt { get; set; }
        public float? Temperature { get; set; }
        public bool? Stream { get; set; }
        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; }
        [JsonPropertyName("timestamp_granularities[]")]
        public string[]? TimestampGranularities { get; set; }
        [JsonPropertyName("chunking_strategy")]
        public string? ChunkingStrategy { get; set; }
    }

    private sealed class AudioOptions
    {
        public string? Language { get; set; }
        public string? Prompt { get; set; }
        public string ResponseFormat { get; set; } = "json";
        public float Temperature { get; set; }
        public bool Stream { get; set; }
        public bool StreamSpecified { get; set; }
        public bool DefaultStream { get; set; }
        public bool SegmentTimestamps { get; set; }
        public bool WordTimestamps { get; set; }
        public bool ExplicitGranularity { get; set; }
        public bool ChunkingSpecified { get; set; }
        public VadSegmentationOptions? VadOptions { get; set; }
        public Dictionary<string, string>? ChunkingFields { get; set; }
    }

    private const int MaxBoundaryLength = 128;
    private const int MaxFieldBytes = 32 * 1024;
    private static readonly Lazy<HashSet<string>> SupportedLanguages = new(() =>
        WhisperFactory.GetSupportedLanguages().ToHashSet(StringComparer.OrdinalIgnoreCase));

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

            var options = new AudioOptions
            {
                Language = context.Request.Query["language"],
                Prompt = context.Request.Query["prompt"],
                ResponseFormat = context.Request.Query["response_format"].ToString() is { Length: > 0 } format ? format : "json",
                DefaultStream = config.GetValue<bool>("SttSettings:StreamResponse", false)
            };
            ApplyQueryOptions(context.Request.Query, options, config);

            TranscriptResult result;
            if (context.Request.HasFormContentType)
            {
                // Spooling permits any multipart field order, including language after file.
                await using var upload = await ReadMultipartAsync(context.Request, options, config, ct);
                ValidateOptions(options, translate);
                upload.Position = 0;
                result = await RunPipelineAsync(upload, audio, vad, whisper, options, translate,
                    context, ct);
            }
            else if (IsRawAudio(context.Request.ContentType))
            {
                ValidateOptions(options, translate);
                result = await RunPipelineAsync(context.Request.Body, audio, vad, whisper,
                    options, translate, context, ct);
            }
            else
            {
                return Error("Send multipart/form-data or raw audio with an audio/* or application/octet-stream content type.", 415);
            }

            Console.WriteLine($"[{(translate ? "TRANSLATION" : "TRANSCRIPTION")}] {result.Duration:F1}s, {result.Text.Length} characters.");
            if (options.Stream)
            {
                await WriteEventAsync(context, new { type = "transcript.text.done", text = result.Text }, ct);
                return Results.Empty;
            }
            return FormatResponse(options, result, translate);
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
                await WriteStreamErrorAsync(context, ex.Message);
                return Results.Empty;
            }
            return Error(ex.Message, 400);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Transcription failed: {ex}");
            if (context.Response.HasStarted)
            {
                await WriteStreamErrorAsync(context, "An internal error occurred during transcription.");
                return Results.Empty;
            }
            return Results.Problem("An internal error occurred during transcription.", statusCode: 500);
        }
    }

    private static void ApplyQueryOptions(IQueryCollection query, AudioOptions options, IConfiguration config)
    {
        if (query.TryGetValue("stream", out var stream))
        {
            options.Stream = ParseBool(stream.ToString(), "stream");
            options.StreamSpecified = true;
        }
        if (query.TryGetValue("temperature", out var temperature))
            options.Temperature = ParseTemperature(temperature.ToString());
        if (query.TryGetValue("timestamp_granularities[]", out var granularities))
            foreach (string? value in granularities) AddGranularity(options, value ?? "");
        if (query.TryGetValue("timestamp_granularities", out var plainGranularities))
            foreach (string? value in plainGranularities) AddGranularity(options, value ?? "");
        if (query.TryGetValue("chunking_strategy", out var chunking))
            SetChunkingStrategy(options, chunking.ToString(), config);
        foreach (string field in query.Keys)
        {
            if (TryGetChunkingField(field, out string name))
            {
                (options.ChunkingFields ??= new(StringComparer.Ordinal))[name] = query[field].ToString();
                continue;
            }
            if (field is not ("model" or "language" or "prompt" or "response_format" or
                "stream" or "temperature" or "timestamp_granularities[]" or
                "timestamp_granularities" or "chunking_strategy"))
                throw new InvalidDataException($"Unsupported parameter: {field}.");
        }
        CompleteChunkingFields(options, config);
    }

    private static async Task<FileStream> ReadMultipartAsync(HttpRequest request, AudioOptions options,
        IConfiguration config, CancellationToken ct)
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
        FileStream? upload = null;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data"))
                    throw new InvalidDataException("Invalid multipart section.");

                string field = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                bool isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                if (isFile)
                {
                    if (field != "file" || upload is not null)
                        throw new InvalidDataException("Exactly one audio file field named 'file' is required.");
                    upload = new FileStream(Path.Combine(Path.GetTempPath(), $"stt-{Guid.NewGuid():N}.tmp"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                        FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    await section.Body.CopyToAsync(upload, ct);
                    continue;
                }

                string value = await ReadFieldAsync(section.Body, ct);
                switch (field)
                {
                    case "language": options.Language = value; break;
                    case "prompt": options.Prompt = value; break;
                    case "response_format": options.ResponseFormat = value; break;
                    case "temperature": options.Temperature = ParseTemperature(value); break;
                    case "stream":
                        options.Stream = ParseBool(value, "stream");
                        options.StreamSpecified = true;
                        break;
                    case "timestamp_granularities[]" or "timestamp_granularities":
                        AddGranularity(options, value);
                        break;
                    case "chunking_strategy": SetChunkingStrategy(options, value, config); break;
                    case "model": break; // API aliases use the same loaded GGML weights.
                    case var name when TryGetChunkingField(name, out string key):
                        (options.ChunkingFields ??= new(StringComparer.Ordinal))[key] = value;
                        break;
                    default: throw new InvalidDataException($"Unsupported parameter: {field}.");
                }
            }
            CompleteChunkingFields(options, config);
            return upload ?? throw new InvalidDataException("Audio file is required.");
        }
        catch
        {
            if (upload is not null) await upload.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> ReadFieldAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        while (buffer.WrittenCount <= MaxFieldBytes)
        {
            int remaining = MaxFieldBytes + 1 - buffer.WrittenCount;
            int read = await stream.ReadAsync(buffer.GetMemory(Math.Min(4096, remaining)), ct);
            if (read == 0) return Encoding.UTF8.GetString(buffer.WrittenSpan);
            buffer.Advance(read);
        }
        throw new InvalidDataException("Multipart field exceeds 32768 bytes.");
    }

    private static bool ParseBool(string value, string name) => value.ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" or "null" => false,
        _ => throw new InvalidDataException($"{name} must be a boolean.")
    };

    private static float ParseTemperature(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float temperature)
            || !float.IsFinite(temperature) || temperature is < 0 or > 1)
            throw new InvalidDataException("temperature must be between 0 and 1.");
        return temperature;
    }

    private static void AddGranularity(AudioOptions options, string value)
    {
        if (value.StartsWith('['))
        {
            try
            {
                using var array = JsonDocument.Parse(value);
                if (array.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("timestamp_granularities must be an array.");
                foreach (JsonElement entry in array.RootElement.EnumerateArray())
                    AddGranularity(options, entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? "" : "");
                return;
            }
            catch (JsonException)
            {
                throw new InvalidDataException("timestamp_granularities must be an array.");
            }
        }
        options.ExplicitGranularity = true;
        switch (value)
        {
            case "word": options.WordTimestamps = true; break;
            case "segment": options.SegmentTimestamps = true; break;
            default: throw new InvalidDataException("timestamp_granularities[] must be 'word' or 'segment'.");
        }
    }

    private static bool TryGetChunkingField(string field, out string name)
    {
        const string bracket = "chunking_strategy[";
        const string dotted = "chunking_strategy.";
        if (field.StartsWith(bracket, StringComparison.Ordinal) && field.EndsWith(']'))
        {
            name = field[bracket.Length..^1];
            return name.Length > 0;
        }
        if (field.StartsWith(dotted, StringComparison.Ordinal))
        {
            name = field[dotted.Length..];
            return name.Length > 0;
        }
        name = "";
        return false;
    }

    private static void CompleteChunkingFields(AudioOptions options, IConfiguration config)
    {
        if (options.ChunkingFields is not { Count: > 0 } chunkingFields) return;
        if (options.ChunkingSpecified)
            throw new InvalidDataException("Specify chunking_strategy once, either as JSON or as individual fields.");
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in chunkingFields)
        {
            fields[name] = name switch
            {
                "silence_duration_ms" or "prefix_padding_ms" when
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer) => integer,
                "threshold" when
                    float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float probability) => probability,
                _ => value
            };
        }
        SetChunkingStrategy(options, JsonSerializer.Serialize(fields), config);
        options.ChunkingFields = null;
    }

    private static void SetChunkingStrategy(AudioOptions options, string value, IConfiguration config)
    {
        options.ChunkingSpecified = true;
        if (value == "auto" || value == "\"auto\"")
        {
            options.VadOptions = null;
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            JsonElement strategy = document.RootElement;
            if (strategy.ValueKind != JsonValueKind.Object ||
                !strategy.TryGetProperty("type", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "server_vad")
                throw new InvalidDataException("chunking_strategy must be 'auto' or a server_vad object.");

            int pauseMs = config.GetValue<int>("SttSettings:VadPauseMs", 800);
            int paddingMs = 384;
            float threshold = 0.5f;
            foreach (JsonProperty property in strategy.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "type": break;
                    case "silence_duration_ms" when property.Value.ValueKind == JsonValueKind.Number &&
                                                    property.Value.TryGetInt32(out int pause):
                        pauseMs = pause;
                        break;
                    case "prefix_padding_ms" when property.Value.ValueKind == JsonValueKind.Number &&
                                                  property.Value.TryGetInt32(out int padding):
                        paddingMs = padding;
                        break;
                    case "threshold" when property.Value.ValueKind == JsonValueKind.Number &&
                                          property.Value.TryGetSingle(out float probability):
                        threshold = probability;
                        break;
                    default:
                        throw new InvalidDataException($"Invalid chunking_strategy field: {property.Name}.");
                }
            }
            if (pauseMs is < 32 or > 5000 || paddingMs is < 0 or > 5000 ||
                !float.IsFinite(threshold) || threshold is < 0 or > 1)
                throw new InvalidDataException("server_vad requires silence_duration_ms=32..5000, prefix_padding_ms=0..5000, and threshold=0..1.");

            options.VadOptions = new VadSegmentationOptions(pauseMs, paddingMs, threshold);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("chunking_strategy must be 'auto' or a JSON server_vad object.");
        }
    }

    private static void ValidateOptions(AudioOptions options, bool translate)
    {
        if (options.ResponseFormat is not ("json" or "text" or "srt" or "vtt" or "verbose_json"))
            throw new InvalidDataException("Unsupported response_format.");
        if (options.ExplicitGranularity && (translate || options.ResponseFormat != "verbose_json"))
            throw new InvalidDataException("timestamp_granularities[] requires transcription with response_format=verbose_json.");
        if (translate && options.ChunkingSpecified)
            throw new InvalidDataException("chunking_strategy is only supported for transcription.");
        if (translate && !string.IsNullOrEmpty(options.Language))
            throw new InvalidDataException("language is not supported for translation.");
        if (!translate && !string.IsNullOrWhiteSpace(options.Language) &&
            !options.Language.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !SupportedLanguages.Value.Contains(options.Language))
            throw new InvalidDataException("language must be a language code supported by Whisper.");
        if (!options.StreamSpecified && !translate && options.ResponseFormat == "json")
            options.Stream = options.DefaultStream;
        if (options.ResponseFormat == "verbose_json" && !options.ExplicitGranularity)
            options.SegmentTimestamps = true;
        if (options.Stream && (translate || options.ResponseFormat != "json"))
            throw new InvalidDataException("stream=true requires transcription with response_format=json.");
    }

    private static bool IsRawAudio(string? contentType) =>
        contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
        contentType?.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true;

    private static Task<TranscriptResult> RunPipelineAsync(Stream input, AudioProcessor audio,
        VadProcessor vad, Transcriptor whisper, AudioOptions options, bool translate,
        HttpContext context, CancellationToken ct)
    {
        bool includeSegments = options.SegmentTimestamps || options.ResponseFormat is "srt" or "vtt";
        var request = new PipelineRequest(options.Language, options.Prompt, options.Temperature,
            translate, options.VadOptions, options.WordTimestamps, includeSegments, options.SegmentTimestamps);
        Func<string, RecognizedChunk, CancellationToken, Task>? onDelta = options.Stream
            ? (delta, _, token) => WriteEventAsync(context, new { type = "transcript.text.delta", delta }, token)
            : null;
        return TranscriptPipeline.RunAsync(input, audio, vad, whisper, request, onDelta, ct);
    }

    private static IResult FormatResponse(AudioOptions options, TranscriptResult result, bool translate)
    {
        if (options.ResponseFormat == "text") return Results.Text(result.Text, "text/plain; charset=utf-8");
        if (options.ResponseFormat is "srt" or "vtt")
            return Results.Text(FormatSubtitles(result.Segments, options.ResponseFormat == "vtt"),
                options.ResponseFormat == "vtt" ? "text/vtt; charset=utf-8" : "application/x-subrip; charset=utf-8");
        if (options.ResponseFormat == "verbose_json")
        {
            return Results.Json(new
            {
                task = translate ? "translate" : "transcribe",
                language = result.Language,
                duration = result.Duration,
                text = result.Text,
                segments = options.SegmentTimestamps ? result.Segments.Select(segment => new
                {
                    id = segment.Id, seek = (int)Math.Round(segment.Start * 100),
                    start = segment.Start, end = segment.End, text = segment.Text,
                    tokens = segment.Tokens, temperature = segment.Temperature,
                    avg_logprob = segment.AvgLogprob, compression_ratio = segment.CompressionRatio,
                    no_speech_prob = segment.NoSpeechProb
                }).ToArray() : null,
                words = options.WordTimestamps ? result.Words.Select(word => new
                {
                    word = word.Word, start = word.Start, end = word.End
                }).ToArray() : null
            }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }
        return Results.Ok(new { text = result.Text });
    }

    private static string FormatSubtitles(IReadOnlyList<TranscriptSegment> segments, bool vtt)
    {
        var output = new StringBuilder();
        if (vtt) output.Append("WEBVTT\n\n");
        for (int i = 0; i < segments.Count; i++)
        {
            TranscriptSegment segment = segments[i];
            if (!vtt) output.Append(i + 1).Append('\n');
            output.Append(SubtitleTime(segment.Start, vtt)).Append(" --> ")
                .Append(SubtitleTime(segment.End, vtt)).Append('\n')
                .Append(segment.Text).Append("\n\n");
        }
        return output.ToString();
    }

    private static string SubtitleTime(double seconds, bool vtt)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        long hours = (long)time.TotalHours;
        char separator = vtt ? '.' : ',';
        return $"{hours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}";
    }

    private static async Task WriteEventAsync(HttpContext context, object value, CancellationToken ct)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
        }
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(value) + "\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static async Task WriteStreamErrorAsync(HttpContext context, string message)
    {
        try
        {
            await context.Response.WriteAsync("event: error\ndata: " + JsonSerializer.Serialize(new
            {
                error = new { message, type = "invalid_request_error" }
            }) + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch (Exception) { context.Abort(); }
    }

    private static IResult Error(string message, int statusCode) => Results.Json(
        new { error = new { message, type = "invalid_request_error" } }, statusCode: statusCode);
}
