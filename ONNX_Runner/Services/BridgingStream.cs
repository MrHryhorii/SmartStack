using System.Buffers;
using System.Threading.Channels;

namespace ONNX_Runner.Services;

/// <summary>
/// A universal buffered gateway stream. 
/// It acts as a bridge between synchronous audio writers (like NAudio) and asynchronous network streams.
/// Accumulates incoming bytes in a rented memory pool buffer and pushes them to a Threading.Channel 
/// to optimize network packet sizes. Zero-allocation design prevents GC spikes during streaming.
/// </summary>
public class BridgingStream : Stream
{
    // The asynchronous channel writer that pushes data to the HTTP Response body.
    // Now passes both the rented buffer and its actual used length.
    private readonly ChannelWriter<(byte[] Buffer, int Length)> _writer;
    private readonly int _minChunkSizeBytes;

    // Zero-allocation buffer state
    private byte[]? _currentBuffer;
    private int _bufferPosition = 0;
    private long _totalBytesWritten = 0;

    public BridgingStream(ChannelWriter<(byte[] Buffer, int Length)> writer, int minChunkSizeBytes = 8192)
    {
        _writer = writer;
        // Enforce a minimum chunk size of 1KB to prevent network spam
        _minChunkSizeBytes = minChunkSizeBytes > 0 ? minChunkSizeBytes : 1024;

        // Rent the initial buffer from the shared pool
        _currentBuffer = ArrayPool<byte>.Shared.Rent(_minChunkSizeBytes);
    }

    /// <summary>
    /// Intercepts data written by the audio encoder and buffers it into a rented array.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0 || _currentBuffer == null) return;

        int bytesRemaining = count;
        int currentOffset = offset;

        // Loop handles cases where the incoming count is larger than our chunk size limit
        while (bytesRemaining > 0)
        {
            int spaceAvailable = _currentBuffer.Length - _bufferPosition;
            int bytesToCopy = Math.Min(bytesRemaining, spaceAvailable);

            Array.Copy(buffer, currentOffset, _currentBuffer, _bufferPosition, bytesToCopy);

            _bufferPosition += bytesToCopy;
            currentOffset += bytesToCopy;
            bytesRemaining -= bytesToCopy;
            _totalBytesWritten += bytesToCopy;

            // If buffering is enabled and we've reached the required chunk size, dispatch immediately.
            if (_bufferPosition >= _minChunkSizeBytes)
            {
                PushToChannel();
            }
        }
    }

    /// <summary>
    /// Forces the stream to immediately dispatch any remaining data in the buffer to the network.
    /// </summary>
    public override void Flush()
    {
        PushToChannel();
    }

    /// <summary>
    /// Dispatches the current rented buffer to the channel and prepares a fresh one.
    /// </summary>
    private void PushToChannel()
    {
        if (_bufferPosition == 0 || _currentBuffer == null) return;

        // TryWrite returns false only if the channel is already completed (request cancelled/done).
        // In that case, dropping the chunk is correct behaviour — the connection is gone.
        if (!_writer.TryWrite((_currentBuffer, _bufferPosition)))
        {
            Console.WriteLine($"[DEBUG] BridgingStream: channel already closed, chunk of {_bufferPosition} bytes dropped.");
            // Because the channel is closed, it will never be read, so we must return the memory here.
            ArrayPool<byte>.Shared.Return(_currentBuffer);
        }

        // Rent a new clean buffer for the next incoming data chunk
        _currentBuffer = ArrayPool<byte>.Shared.Rent(_minChunkSizeBytes);
        _bufferPosition = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Flush any remaining "leftover" bytes when the stream is being closed/destroyed
            PushToChannel();

            // Clean up the final unused rented buffer to prevent memory leaks
            if (_currentBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_currentBuffer);
                _currentBuffer = null;
            }
        }
        base.Dispose(disposing);
    }

    // ==========================================
    // STANDARD STREAM OVERRIDES (STUBS)
    // ==========================================
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _totalBytesWritten;

    public override long Position
    {
        get => _totalBytesWritten;
        set { }
    }

    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => _totalBytesWritten;
    public override void SetLength(long value) { }
}