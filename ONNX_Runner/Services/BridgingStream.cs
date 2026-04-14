using System.Threading.Channels;

namespace ONNX_Runner.Services;

/// <summary>
/// A universal buffered gateway stream. 
/// It acts as a bridge between synchronous audio writers (like NAudio) and asynchronous network streams.
/// It accumulates incoming bytes in a memory buffer and pushes them to a Threading.Channel 
/// only when a specific chunk size is reached, optimizing network packet sizes.
/// </summary>
public class BridgingStream(ChannelWriter<byte[]> writer, int minChunkSizeBytes = 8192) : Stream
{
    // The asynchronous channel writer that pushes data to the HTTP Response body
    private readonly ChannelWriter<byte[]> _writer = writer;

    // Internal buffer to accumulate small, rapid audio writes into larger, efficient network chunks
    private readonly MemoryStream _buffer = new();
    private readonly int _minChunkSizeBytes = minChunkSizeBytes;

    private long _totalBytesWritten = 0;

    /// <summary>
    /// Intercepts data written by the audio encoder (e.g., LameMP3FileWriter) and buffers it.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        _buffer.Write(buffer, offset, count);
        _totalBytesWritten += count;

        // If buffering is enabled and we've reached the required chunk size, 
        // dispatch the chunk to the network channel immediately.
        if (_minChunkSizeBytes > 0 && _buffer.Length >= _minChunkSizeBytes)
        {
            PushToChannel();
        }
    }

    /// <summary>
    /// Forces the stream to immediately dispatch any remaining data in the buffer to the network.
    /// Useful for pushing data immediately after a complete sentence is spoken.
    /// </summary>
    public override void Flush()
    {
        PushToChannel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Flush any remaining "leftover" bytes when the stream is being closed/destroyed
            PushToChannel();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Extracts the accumulated bytes from the internal MemoryStream, writes them to the 
    /// asynchronous Channel, and resets the buffer for the next chunk.
    /// </summary>
    private void PushToChannel()
    {
        if (_buffer.Length == 0) return;

        _writer.TryWrite(_buffer.ToArray());
        _buffer.SetLength(0); // Clear the buffer efficiently without reallocating memory
    }

    // ==========================================
    // STANDARD STREAM OVERRIDES (STUBS)
    // ==========================================
    // These are required to implement the abstract Stream class, but since this is a 
    // write-only forward-moving stream, seeking and reading are disabled.

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