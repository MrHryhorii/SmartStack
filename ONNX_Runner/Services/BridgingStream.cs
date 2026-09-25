using System.Buffers;
using System.Threading.Channels;

namespace ONNX_Runner.Services;

/// <summary>
/// A universal buffered gateway stream.
/// It acts as a bridge between synchronous audio writers (like NAudio) and asynchronous network streams.
/// Accumulates incoming bytes in a rented memory pool buffer and pushes them to a Threading.Channel
/// to optimize network packet sizes. The bounded channel provides real backpressure instead of dropping
/// audio when the network temporarily falls behind generation.
/// </summary>
public class BridgingStream : Stream
{
    private readonly ChannelWriter<(byte[] Buffer, int Length)> _writer;
    private readonly int _minChunkSizeBytes;
    private byte[]? _currentBuffer;
    private int _bufferPosition;
    private long _totalBytesWritten;

    public BridgingStream(
        ChannelWriter<(byte[] Buffer, int Length)> writer,
        int minChunkSizeBytes = 8192)
    {
        _writer = writer;
        _minChunkSizeBytes = minChunkSizeBytes > 0 ? minChunkSizeBytes : 1024;
        _currentBuffer = ArrayPool<byte>.Shared.Rent(_minChunkSizeBytes);
    }

    /// <summary>
    /// Intercepts data written by synchronous audio encoders and buffers it into pooled memory.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset and count exceed the source buffer length.");
        }

        WriteCore(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Span overload avoids Stream's compatibility fallback when a caller already has span-based data.
    /// </summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WriteCore(buffer);
    }

    // Buffers incoming bytes and dispatches full chunks to the network channel.
    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_currentBuffer == null, this);

        int sourceOffset = 0;

        while (sourceOffset < buffer.Length)
        {
            int spaceAvailable = _currentBuffer!.Length - _bufferPosition;
            int bytesToCopy = Math.Min(buffer.Length - sourceOffset, spaceAvailable);

            buffer.Slice(sourceOffset, bytesToCopy)
                .CopyTo(_currentBuffer.AsSpan(_bufferPosition, bytesToCopy));

            _bufferPosition += bytesToCopy;
            sourceOffset += bytesToCopy;
            _totalBytesWritten += bytesToCopy;

            if (_bufferPosition >= _minChunkSizeBytes)
            {
                PushToChannel();
            }
        }
    }

    /// <summary>
    /// Forces the stream to dispatch any buffered bytes. If the bounded channel is full,
    /// this synchronously waits for capacity so the audio producer naturally slows down
    /// instead of silently losing data.
    /// </summary>
    public override void Flush()
    {
        PushToChannel();
    }

    /// <summary>
    /// Transfers ownership of the current pooled buffer to the network channel.
    /// TryWrite is the allocation-free common path; when the bounded channel is temporarily full,
    /// WriteAsync supplies the required backpressure. A closed/faulted channel propagates upstream.
    /// </summary>
    private void PushToChannel()
    {
        if (_bufferPosition == 0 || _currentBuffer == null)
        {
            return;
        }

        byte[] bufferToSend = _currentBuffer;
        int lengthToSend = _bufferPosition;
        bool ownershipTransferred = false;

        try
        {
            var item = (Buffer: bufferToSend, Length: lengthToSend);

            if (_writer.TryWrite(item))
            {
                ownershipTransferred = true;
            }
            else
            {
                // FullMode.Wait means TryWrite(false) can simply mean "temporarily full".
                // The audio writers above this Stream are synchronous, so block this producer
                // thread until the async network consumer frees capacity or the channel closes.
                _writer.WriteAsync(item).AsTask().GetAwaiter().GetResult();
                ownershipTransferred = true;
            }
        }
        finally
        {
            if (!ownershipTransferred)
            {
                ArrayPool<byte>.Shared.Return(bufferToSend);
                _currentBuffer = null;
                _bufferPosition = 0;
            }
        }

        // Ownership now belongs to the channel/network sender. Rent fresh storage only after
        // the handoff succeeds, keeping pooled-buffer ownership unambiguous on failure paths.
        _currentBuffer = ArrayPool<byte>.Shared.Rent(_minChunkSizeBytes);
        _bufferPosition = 0;
    }

    /// <summary>
    /// Discards buffered bytes when the network response cannot accept more output.
    /// </summary>
    public void Abort()
    {
        if (_currentBuffer == null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_currentBuffer);
        _currentBuffer = null;
        _bufferPosition = 0;
    }

    /// <summary>
    /// Flushes any buffered bytes and returns locally owned pooled memory.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                PushToChannel();
            }
            finally
            {
                if (_currentBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_currentBuffer);
                    _currentBuffer = null;
                    _bufferPosition = 0;
                }
            }
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _currentBuffer != null;
    public override long Length => _totalBytesWritten;

    public override long Position
    {
        get => _totalBytesWritten;
        set => throw new NotSupportedException("BridgingStream is forward-only.");
    }

    /// <summary>
    /// Read operations are not supported by this write-only stream.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("BridgingStream is write-only.");

    /// <summary>
    /// Seeking is not supported by this forward-only stream.
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("BridgingStream is forward-only.");

    /// <summary>
    /// Changing length is not supported by this forward-only stream.
    /// </summary>
    public override void SetLength(long value) =>
        throw new NotSupportedException("BridgingStream does not support SetLength.");
}
