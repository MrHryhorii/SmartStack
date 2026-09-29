using STT_Runner.Services;
using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Whisper.net;

namespace STT_Runner.Endpoints;

/// <summary>
/// A browser-facing full-duplex route. Binary messages form one continuous
/// encoded audio stream; the text "stop" ends input and flushes the transcript.
/// </summary>
public static class LiveTranscriptionEndpoints
{
    private static readonly HashSet<string> Languages =
        WhisperFactory.GetSupportedLanguages().ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void MapLiveTranscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var languages = new[] { new { code = "auto", name = "Auto detect" } }
            .Concat(Languages.Where(code => code != "auto")
                .Select(code => new { code, name = LanguageName(code) })
                .OrderBy(item => item.name))
            .ToArray();
        app.MapGet("/v1/languages", () => Results.Ok(new { data = languages }))
            .WithTags("Models");
        app.MapGet("/live", HandleAsync).WithTags("Live transcription");
    }

    private static async Task HandleAsync(HttpContext context, AudioProcessor audio,
        VadProcessor vad, Transcriptor whisper, RequestSlots slots)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }
        string language = context.Request.Query["language"].ToString();
        if (language.Length == 0) language = "auto";
        if (language != "auto" && !Languages.Contains(language))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string translation = context.Request.Query["translate"].ToString();
        if (translation is not ("true" or "false" or ""))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        bool translate = translation == "true";

        System.Threading.RateLimiting.RateLimitLease acquired;
        try { acquired = await slots.AcquireAsync(context.RequestAborted); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        using var lease = acquired;
        if (!lease.IsAcquired)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        CancellationToken ct = linked.Token;
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 256 * 1024,
            resumeWriterThreshold: 128 * 1024, useSynchronizationContext: false));
        Task receiver = Task.CompletedTask;
        try
        {
            await SendAsync(socket, new { type = "session.ready" }, ct);
            receiver = ReceiveAudioAsync(socket, pipe.Writer, linked);
            await using Stream input = pipe.Reader.AsStream();
            long index = 0;
            var request = new PipelineRequest(language, null, 0, translate, null,
                WordTimestamps: false, IncludeSegments: false, VerboseSegmentMetadata: false);
            TranscriptResult result = await TranscriptPipeline.RunAsync(input, audio, vad, whisper,
                request, (delta, chunk, token) => SendAsync(socket, new
                {
                    type = "transcript.text.delta",
                    delta,
                    index = index++,
                    start = Math.Max(0, chunk.Start),
                    end = chunk.End
                }, token), ct);
            await receiver;
            await SendAsync(socket, new
            {
                type = "transcript.text.done",
                text = result.Text,
                duration = result.Duration,
                language = result.Language
            }, ct);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Complete", ct);
        }
        catch (Exception ex)
        {
            bool disconnected = context.RequestAborted.IsCancellationRequested ||
                ex is OperationCanceledException or WebSocketException or IOException;
            linked.Cancel();
            if (disconnected)
            {
                socket.Abort();
                return;
            }

            Console.Error.WriteLine($"[ERROR] Live transcription failed: {ex}");
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    // A broken peer must not hold the request slot while an error is sent.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendAsync(socket, new
                    {
                        type = "error",
                        message = ex is InvalidDataException ? ex.Message : "Live transcription stopped unexpectedly."
                    }, timeout.Token);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError,
                        "Transcription failed", timeout.Token);
                }
                catch (Exception) { socket.Abort(); }
            }
        }
        finally
        {
            linked.Cancel();
            try { await receiver; } catch (Exception) { }
        }
    }

    private static async Task ReceiveAudioAsync(WebSocket socket, PipeWriter writer,
        CancellationTokenSource cancellation)
    {
        CancellationToken ct = cancellation.Token;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var command = new StringBuilder();
        Exception? failure = null;
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (part.MessageType == WebSocketMessageType.Close)
                    throw new IOException("The client disconnected before stopping the recording.");
                if (part.MessageType == WebSocketMessageType.Binary)
                {
                    if (command.Length != 0)
                        throw new InvalidDataException("Audio interrupted a control message.");
                    if (part.Count > 0)
                    {
                        FlushResult flushed = await writer.WriteAsync(buffer.AsMemory(0, part.Count), ct);
                        if (flushed.IsCompleted) throw new IOException("The audio pipeline closed early.");
                    }
                    continue;
                }

                command.Append(Encoding.UTF8.GetString(buffer, 0, part.Count));
                if (command.Length > 128)
                    throw new InvalidDataException("Control message is too long.");
                if (!part.EndOfMessage) continue;
                if (command.ToString() != "stop")
                    throw new InvalidDataException("Expected the stop control message.");
                return;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            // A dropped connection should stop VAD and Whisper even if FFmpeg is
            // currently waiting for more input or the audio pipe is backpressured.
            if (ex is IOException or WebSocketException or OperationCanceledException)
                cancellation.Cancel();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await writer.CompleteAsync(failure);
        }
    }

    private static Task SendAsync(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(),
            WebSocketMessageType.Text, true, ct).AsTask();

    private static string LanguageName(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (CultureNotFoundException) { return code; }
    }
}
