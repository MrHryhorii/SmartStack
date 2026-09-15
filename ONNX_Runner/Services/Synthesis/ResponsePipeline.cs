using System.Buffers;
using System.Text;
using System.Threading.Channels;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services.Synthesis;

/// <summary>
/// Static response pipeline that prepares, wraps, streams, buffers, and finalizes synthesis output.
/// It deliberately keeps response-format decisions outside the audio generation pipeline while
/// preserving a linear data path that can be followed directly from SpeechSynthesisService.
/// </summary>
internal static class ResponsePipeline
{
    private const int NetworkChannelCapacity = 50;
    private const int BufferedResponseInitialCapacity = 1024 * 1024;

    // Cached once for the entire process. The original monolith allocated these arrays for
    // every B64Json request even though the prefix and suffix never change.
    private static readonly byte[] Base64JsonPrefix = Encoding.UTF8.GetBytes("{\n  \"audioContent\": \"");
    private static readonly byte[] Base64JsonSuffix = Encoding.UTF8.GetBytes("\"\n}");

    internal enum PayloadKind : byte
    {
        Audio,
        Base64Json
    }

    /// <summary>
    /// Small allocation-free value describing how the current response must be delivered.
    /// New response formats should normally extend this policy instead of leaking conditions
    /// into SpeechSynthesisService or AudioGenerationPipeline.
    /// </summary>
    internal readonly struct Plan(
        PayloadKind payload,
        bool useStreaming,
        bool flushAfterEachSentence)
    {
        public readonly PayloadKind Payload = payload;
        public readonly bool UseStreaming = useStreaming;
        public readonly bool FlushAfterEachSentence = flushAfterEachSentence;
    }

    /// <summary>
    /// Runtime references owned by one response. This is a value type on purpose: the state
    /// container itself does not allocate, while the streams/channel/tasks inside it are only
    /// created when the selected transport actually requires them.
    /// </summary>
    internal struct State
    {
        public Channel<(byte[] Buffer, int Length, bool ContainsAudio)>? NetworkChannel;
        public Task? NetworkSenderTask;
        public Stream? RawStream;
        public Stream? TargetStream;
        public Base64EncodingStream? Base64Stream;
        public bool PayloadOpened;
        public bool PayloadClosed;
    }

    public static Plan Resolve(
        SynthesisRequest request,
        SynthesisContext ctx,
        StreamSettings streamConfig)
    {
        bool shouldStream = request.Stream ?? streamConfig.EnableStreaming;

        // WAV requires the total file size to be written into its header upfront.
        // Therefore, true chunked streaming is conceptually impossible for WAV and
        // transparently falls back to buffered delivery even when stream=true was requested.
        bool useStreaming = shouldStream && ctx.AudioFormat != AudioFormat.Wav;

        PayloadKind payload = request.Format == AudioFormat.B64Json
            ? PayloadKind.Base64Json
            : PayloadKind.Audio;

        return new Plan(
            payload,
            useStreaming,
            useStreaming && streamConfig.FlushAfterEachSentence);
    }

    public static void CreateTransport(
        ref State state,
        Plan plan,
        StreamSettings streamConfig)
    {
        if (!plan.UseStreaming)
        {
            // Pre-allocate 1 MB exactly like the original monolith for non-streaming requests.
            state.RawStream = new MemoryStream(BufferedResponseInitialCapacity);
            return;
        }

        var channelOptions = new BoundedChannelOptions(NetworkChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };

        state.NetworkChannel = Channel.CreateBounded<(byte[] Buffer, int Length, bool ContainsAudio)>(channelOptions);

        int chunkSize = streamConfig.MinChunkSizeKb * 1024;
        state.RawStream = new BridgingStream(state.NetworkChannel.Writer, chunkSize);
    }

    public static void ConfigureHeaders(
        Plan plan,
        SynthesisRequest request,
        SynthesisContext ctx,
        HttpContext httpContext)
    {
        httpContext.Response.Headers.Append(
            "X-Audio-Sample-Rate",
            ctx.DisplaySampleRate.ToString());

        httpContext.Response.Headers.Append(
            "X-Audio-Codec",
            GetCodecName(ctx.AudioFormat));

        if (!plan.UseStreaming)
        {
            return;
        }

        // Streaming responses are content streams, not download attachments.
        // B64Json keeps its dedicated application/json MIME type here as well.
        httpContext.Response.ContentType = AudioStreamManager.GetMimeType(request.Format);
    }

    public static void StartNetworkSender(
        ref State state,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (state.NetworkChannel == null)
        {
            throw new InvalidOperationException("Streaming response channel has not been created.");
        }

        // No Task.Run here: this path performs asynchronous network I/O and does not need
        // an extra ThreadPool hop. Calling the async method starts it immediately until the
        // first incomplete await while the synthesis pipeline can continue independently.
        state.NetworkSenderTask = SendToNetworkAsync(
            state.NetworkChannel,
            httpContext.Response.Body,
            RequestLogContext.Current,
            cancellationToken);
    }

    public static void OpenPayload(
        ref State state,
        Plan plan)
    {
        Stream rawStream = state.RawStream
            ?? throw new InvalidOperationException("Response transport has not been created.");

        switch (plan.Payload)
        {
            case PayloadKind.Audio:
                state.TargetStream = rawStream;
                state.PayloadOpened = true;
                return;

            case PayloadKind.Base64Json:
                // Write the JSON prefix directly to the underlying stream BEFORE the audio
                // encoder starts. Streaming responses can therefore deliver bytes immediately.
                rawStream.Write(Base64JsonPrefix, 0, Base64JsonPrefix.Length);

                // leaveInnerOpen: true ensures we can still append the closing JSON suffix
                // after Base64EncodingStream finalizes its 1-2 byte remainder and padding.
                state.Base64Stream = new Base64EncodingStream(rawStream, leaveInnerOpen: true);
                state.TargetStream = state.Base64Stream;
                state.PayloadOpened = true;
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(plan), plan.Payload, "Unsupported response payload.");
        }
    }

    public static void ClosePayload(
        ref State state,
        Plan plan)
    {
        if (!state.PayloadOpened || state.PayloadClosed)
        {
            return;
        }

        switch (plan.Payload)
        {
            case PayloadKind.Audio:
                state.PayloadClosed = true;
                return;

            case PayloadKind.Base64Json:
                // Dispose finalizes any pending Base64 remainder and writes correct padding.
                state.Base64Stream?.Dispose();
                state.Base64Stream = null;

                // The JSON suffix must be written only after Base64 finalization so no encoded
                // audio bytes can appear outside the audioContent string.
                state.RawStream!.Write(Base64JsonSuffix, 0, Base64JsonSuffix.Length);
                state.PayloadClosed = true;
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(plan), plan.Payload, "Unsupported response payload.");
        }
    }

    public static async Task<IResult> CompleteAsync(
        State state,
        Plan plan,
        SynthesisRequest request)
    {
        if (plan.UseStreaming)
        {
            // Push the final partially-filled BridgingStream chunk before completing the channel.
            try
            {
                state.RawStream?.Flush();
            }
            catch (ObjectDisposedException)
            {
            }

            state.NetworkChannel?.Writer.TryComplete();

            if (state.NetworkSenderTask != null)
            {
                await state.NetworkSenderTask;
            }

            return Results.Empty;
        }

        var memoryStream = state.RawStream as MemoryStream
            ?? throw new InvalidOperationException("Buffered response does not own a MemoryStream.");

        // Preserve the original behavior: materialize one exact-sized array only when the
        // caller explicitly requested a non-streaming response or the format requires it.
        byte[] finalBytes = memoryStream.ToArray();

        // Buffered audio responses are complete files, so preserve a download filename.
        // B64Json is a JSON payload rather than an audio file and must never be an attachment.
        string? fileName = plan.Payload == PayloadKind.Base64Json
            ? null
            : AudioStreamManager.GetFileName(request.Format);

        return Results.File(
            finalBytes,
            AudioStreamManager.GetMimeType(request.Format),
            fileName);
    }

    public static async Task AbortAsync(
        State state,
        Exception? error = null)
    {
        if (state.NetworkChannel == null)
        {
            return;
        }

        // Flush any bytes successfully produced during best-effort payload finalization before
        // terminating the channel. If the client is already gone this simply fails harmlessly.
        try
        {
            state.RawStream?.Flush();
        }
        catch
        {
            // Best-effort cleanup only.
        }

        state.NetworkChannel.Writer.TryComplete(error);

        if (state.NetworkSenderTask != null)
        {
            try
            {
                await state.NetworkSenderTask;
            }
            catch
            {
                // Expected when the client disconnected or the channel completed with an error.
            }

            return;
        }

        // StartAsync can fail before the network sender is launched. Return any pooled chunks
        // that might already have been queued by response framing or cleanup logic.
        DrainQueuedBuffers(state.NetworkChannel.Reader);
    }

    public static void Dispose(ref State state)
    {
        // Normally Base64Stream is already disposed by ClosePayload. This fallback only covers
        // failures that occurred before normal payload finalization.
        try
        {
            state.Base64Stream?.Dispose();
        }
        catch
        {
            // Best-effort cleanup only.
        }

        state.Base64Stream = null;
        state.TargetStream = null;

        state.RawStream?.Dispose();
        state.RawStream = null;
    }

    public static void TryClosePayload(
        ref State state,
        Plan plan)
    {
        if (!state.PayloadOpened || state.PayloadClosed)
        {
            return;
        }

        try
        {
            ClosePayload(ref state, plan);
        }
        catch
        {
            // Best-effort cleanup only. The original pipeline exception remains authoritative.
        }
    }

    private static async Task SendToNetworkAsync(
        Channel<(byte[] Buffer, int Length, bool ContainsAudio)> channel,
        Stream responseBody,
        RequestDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        bool httpAudioTimingMarked = false;

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Write only the valid data length from the rented array.
                    await responseBody.WriteAsync(
                        chunk.Buffer.AsMemory(0, chunk.Length),
                        cancellationToken);

                    await responseBody.FlushAsync(cancellationToken);

                    if (chunk.ContainsAudio && !httpAudioTimingMarked)
                    {
                        diagnostics?.MarkHttpAudioFlushed();
                        httpAudioTimingMarked = true;
                    }
                }
                finally
                {
                    // CRITICAL ZERO-ALLOCATION REQUIREMENT:
                    // Always return the network chunk array to the shared pool after it has been sent.
                    ArrayPool<byte>.Shared.Return(chunk.Buffer);
                }
            }
        }
        catch (Exception ex)
        {
            // Propagate network failure back into BridgingStream. Once the writer observes the
            // completed channel it faults the audio consumer, which in turn cancels the producer.
            // This keeps the whole request pipeline fail-fast instead of allowing a dead network
            // reader to leave synthesis blocked forever on a full bounded channel.
            channel.Writer.TryComplete(ex);
            throw;
        }
        finally
        {
            // Cancellation or network failure can leave already-rented chunks queued after the
            // reader stops. Explicitly drain them so ArrayPool ownership is never leaked.
            DrainQueuedBuffers(channel.Reader);
        }
    }

    private static void DrainQueuedBuffers(
        ChannelReader<(byte[] Buffer, int Length, bool ContainsAudio)> reader)
    {
        while (reader.TryRead(out var chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
        }
    }

    private static string GetCodecName(AudioFormat format)
    {
        // Avoid ToString().ToLowerInvariant() allocations for the hot/common formats.
        // Unknown future codecs still fall back safely until they receive an explicit mapping.
        return format switch
        {
            AudioFormat.Mp3 => "mp3",
            AudioFormat.Wav => "wav",
            AudioFormat.Opus => "opus",
            _ => format.ToString().ToLowerInvariant()
        };
    }
}
