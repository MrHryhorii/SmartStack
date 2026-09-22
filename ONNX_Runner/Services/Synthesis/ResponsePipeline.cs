using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
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
    private const int BufferedResponseInitialCapacity = 64 * 1024;
    // Cached once for the entire process. The original monolith allocated these arrays for
    // every B64Json request even though the prefix and suffix never change.
    private static readonly byte[] Base64JsonPrefix = Encoding.UTF8.GetBytes("{\n  \"audioContent\": \"");
    private static readonly byte[] Base64JsonSuffix = Encoding.UTF8.GetBytes("\"\n}");
    private static ReadOnlySpan<byte> SseAudioDeltaPrefix =>
        "data: {\"type\":\"speech.audio.delta\",\"audio\":\""u8;
    private static ReadOnlySpan<byte> SseBase64JsonDeltaPrefix =>
        "data: {\"type\":\"speech.audio.delta\",\"audioContent\":\""u8;
    private static ReadOnlySpan<byte> SseDeltaSuffix => "\"}\n\n"u8;
    private static ReadOnlySpan<byte> SseDoneEvent =>
        "data: {\"type\":\"speech.audio.done\",\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}}\n\n"u8;

    internal enum PayloadKind : byte
    {
        Audio,
        Base64Json
    }
    /// <summary>
    /// Small allocation-free value describing how the current response must be delivered.
    /// Payload, wire framing, and delivery timing remain independent so each request parameter
    /// keeps the same meaning across all supported combinations.
    /// </summary>
    internal readonly struct Plan(
        PayloadKind payload,
        SpeechStreamFormat streamFormat,
        bool useStreaming,
        bool flushAfterEachSentence,
        int chunkSize)
    {
        public readonly PayloadKind Payload = payload;
        public readonly SpeechStreamFormat StreamFormat = streamFormat;
        public readonly bool UseStreaming = useStreaming;
        public readonly bool FlushAfterEachSentence = flushAfterEachSentence;
        public readonly int ChunkSize = chunkSize;
    }
    /// <summary>
    /// Runtime references owned by one response. This is a value type on purpose: the state
    /// container itself does not allocate, while the streams/channel/tasks inside it are only
    /// created when the selected transport actually requires them.
    /// </summary>
    internal struct State
    {
        public Channel<(byte[] Buffer, int Length)>? NetworkChannel;
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
        // Therefore, true incremental delivery is conceptually impossible for WAV and
        // transparently falls back to buffered delivery even when stream=true was requested.
        // SSE can still frame the completed WAV afterward; it simply cannot make WAV arrive sooner.
        bool useStreaming = shouldStream && ctx.AudioFormat != AudioFormat.Wav;

        PayloadKind payload = request.Format == AudioFormat.B64Json
            ? PayloadKind.Base64Json
            : PayloadKind.Audio;
        int chunkSize = Math.Max(1, streamConfig.MinChunkSizeKb * 1024);
        return new Plan(
            payload,
            request.StreamFormat,
            useStreaming,
            useStreaming && streamConfig.FlushAfterEachSentence,
            chunkSize);
    }
    public static void CreateTransport(
        ref State state,
        Plan plan)
    {
        if (!plan.UseStreaming)
        {
            // Start below the LOH threshold. MemoryStream grows only when a larger buffered response actually needs it.
            state.RawStream = new MemoryStream(BufferedResponseInitialCapacity);
            return;
        }
        var channelOptions = new BoundedChannelOptions(NetworkChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };

        state.NetworkChannel = Channel.CreateBounded<(byte[] Buffer, int Length)>(channelOptions);
        state.RawStream = new BridgingStream(state.NetworkChannel.Writer, plan.ChunkSize);
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

        if (plan.StreamFormat == SpeechStreamFormat.Sse)
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.Append("Cache-Control", "no-cache");
            return;
        }
        if (!plan.UseStreaming)
        {
            return;
        }
        // Streaming audio responses are content streams, not download attachments.
        // B64Json keeps its dedicated application/json MIME type here as well.
        httpContext.Response.ContentType = AudioStreamManager.GetMimeType(request.Format);
    }
    public static void StartNetworkSender(
        ref State state,
        Plan plan,
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
        state.NetworkSenderTask = plan.StreamFormat == SpeechStreamFormat.Sse
            ? SendSseToNetworkAsync(
                state.NetworkChannel,
                httpContext.Response.BodyWriter,
                plan.Payload,
                cancellationToken)
            : SendToNetworkAsync(
                state.NetworkChannel,
                httpContext.Response.Body,
                cancellationToken);
    }
    public static void OpenPayload(
        ref State state,
        Plan plan)
    {
        Stream rawStream = state.RawStream
            ?? throw new InvalidOperationException("Response transport has not been created.");

        // SSE owns its own event framing and Base64 representation. The audio encoder must
        // therefore always write raw encoded audio bytes into the transport, including when
        // response_format=b64_json. The SSE sender preserves that payload's audioContent field.
        if (plan.StreamFormat == SpeechStreamFormat.Sse)
        {
            state.TargetStream = rawStream;
            state.PayloadOpened = true;
            return;
        }

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

        if (plan.StreamFormat == SpeechStreamFormat.Sse)
        {
            state.PayloadClosed = true;
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
        SynthesisRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
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

        if (plan.StreamFormat == SpeechStreamFormat.Sse)
        {
            // stream=false and formats that inherently require buffering (WAV) still use SSE
            // framing, but only after the complete encoded audio is available. Access the owned
            // MemoryStream buffer directly to avoid materializing another full-size byte array.
            await httpContext.Response.StartAsync(cancellationToken);
            await SendBufferedSseAsync(
                memoryStream,
                httpContext.Response.BodyWriter,
                plan.Payload,
                plan.ChunkSize,
                cancellationToken);
            return Results.Empty;
        }

        // Preserve the original behavior: materialize one exact-sized array only when the
        // caller explicitly requested a non-streaming normal response or the format requires it.
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
        Channel<(byte[] Buffer, int Length)> channel,
        Stream responseBody,
        CancellationToken cancellationToken)
    {
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
    private static async Task SendSseToNetworkAsync(
        Channel<(byte[] Buffer, int Length)> channel,
        PipeWriter responseWriter,
        PayloadKind payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteSseDelta(
                        responseWriter,
                        chunk.Buffer.AsSpan(0, chunk.Length),
                        payload);
                    FlushResult flushResult = await responseWriter.FlushAsync(cancellationToken);
                    ThrowIfSseTransportClosed(flushResult, cancellationToken);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(chunk.Buffer);
                }
            }

            WriteSseDone(responseWriter);
            FlushResult doneResult = await responseWriter.FlushAsync(cancellationToken);
            ThrowIfSseTransportClosed(doneResult, cancellationToken, allowCompleted: true);
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            throw;
        }
        finally
        {
            DrainQueuedBuffers(channel.Reader);
        }
    }
    private static async Task SendBufferedSseAsync(
        MemoryStream memoryStream,
        PipeWriter responseWriter,
        PayloadKind payload,
        int chunkSize,
        CancellationToken cancellationToken)
    {
        if (!memoryStream.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array == null)
        {
            throw new InvalidOperationException("Buffered response memory is not directly accessible.");
        }

        byte[] buffer = segment.Array;
        int offset = segment.Offset;
        int remaining = segment.Count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(chunkSize, remaining);
            WriteSseDelta(
                responseWriter,
                buffer.AsSpan(offset, length),
                payload);

            FlushResult flushResult = await responseWriter.FlushAsync(cancellationToken);
            ThrowIfSseTransportClosed(flushResult, cancellationToken);
            offset += length;
            remaining -= length;
        }

        WriteSseDone(responseWriter);
        FlushResult doneResult = await responseWriter.FlushAsync(cancellationToken);
        ThrowIfSseTransportClosed(doneResult, cancellationToken, allowCompleted: true);
    }
    private static void WriteSseDelta(
        PipeWriter writer,
        ReadOnlySpan<byte> audio,
        PayloadKind payload)
    {
        ReadOnlySpan<byte> prefix = payload == PayloadKind.Base64Json
            ? SseBase64JsonDeltaPrefix
            : SseAudioDeltaPrefix;
        ReadOnlySpan<byte> suffix = SseDeltaSuffix;
        int encodedLength = Base64.GetMaxEncodedToUtf8Length(audio.Length);
        int requiredLength = checked(prefix.Length + encodedLength + suffix.Length);
        Span<byte> destination = writer.GetSpan(requiredLength);

        prefix.CopyTo(destination);
        int offset = prefix.Length;
        OperationStatus status = Base64.EncodeToUtf8(
            audio,
            destination[offset..],
            out int consumed,
            out int written,
            isFinalBlock: true);
        if (status != OperationStatus.Done || consumed != audio.Length)
        {
            throw new InvalidOperationException("Failed to encode an SSE audio chunk as Base64.");
        }

        offset += written;
        suffix.CopyTo(destination[offset..]);
        offset += suffix.Length;
        writer.Advance(offset);
    }
    private static void WriteSseDone(PipeWriter writer)
    {
        ReadOnlySpan<byte> doneEvent = SseDoneEvent;
        Span<byte> destination = writer.GetSpan(doneEvent.Length);
        doneEvent.CopyTo(destination);
        writer.Advance(doneEvent.Length);
    }
    private static void ThrowIfSseTransportClosed(
        FlushResult result,
        CancellationToken cancellationToken,
        bool allowCompleted = false)
    {
        if (result.IsCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (result.IsCompleted && !allowCompleted)
        {
            throw new IOException("The SSE response transport was closed by the client.");
        }
    }
    private static void DrainQueuedBuffers(
        ChannelReader<(byte[] Buffer, int Length)> reader)
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
            AudioFormat.Aac => "aac",
            AudioFormat.Flac => "flac",
            _ => format.ToString().ToLowerInvariant()
        };
    }
}
