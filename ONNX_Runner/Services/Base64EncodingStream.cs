using System.Buffers;
using System.Buffers.Text;

namespace ONNX_Runner.Services;

/// <summary>
/// Forward-only stream that Base64-encodes written audio bytes without allocating temporary
/// arrays on every Write call. A single pooled output buffer is reused for the lifetime of
/// the response and only 0-2 raw carry bytes are retained between calls.
/// </summary>
public sealed class Base64EncodingStream : Stream
{
    private const int EncodedBufferSize = 16 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private readonly byte[] _carry = new byte[2];

    private byte[]? _encodedBuffer;
    private int _carryCount;
    private bool _finalized;
    private bool _disposed;

    public Base64EncodingStream(Stream inner, bool leaveInnerOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leaveInnerOpen = leaveInnerOpen;
        _encodedBuffer = ArrayPool<byte>.Shared.Rent(EncodedBufferSize);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_finalized;
    public override long Length => throw new NotSupportedException("Base64EncodingStream is write-only and forward-only.");

    public override long Position
    {
        get => throw new NotSupportedException("Base64EncodingStream is write-only and forward-only.");
        set => throw new NotSupportedException("Base64EncodingStream is forward-only.");
    }

    /// <summary>
    /// Base64-encodes the supplied bytes and forwards encoded output to the inner stream.
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
    /// Base64-encodes the supplied bytes and forwards encoded output to the inner stream.
    /// </summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WriteCore(buffer);
    }

    // Encodes complete Base64 groups while preserving at most two carry bytes between writes.
    private void WriteCore(ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
        {
            throw new InvalidOperationException("Cannot write after FinalizeEncoding() has been called.");
        }

        if (source.IsEmpty)
        {
            return;
        }

        byte[] encodedBuffer = _encodedBuffer
            ?? throw new ObjectDisposedException(nameof(Base64EncodingStream));

        // Complete a 3-byte group left over from the previous call first.
        if (_carryCount > 0)
        {
            int needed = 3 - _carryCount;

            if (source.Length < needed)
            {
                source.CopyTo(_carry.AsSpan(_carryCount));
                _carryCount += source.Length;
                return;
            }

            Span<byte> rawBlock = stackalloc byte[3];
            _carry.AsSpan(0, _carryCount).CopyTo(rawBlock);
            source[..needed].CopyTo(rawBlock[_carryCount..]);

            Span<byte> encodedBlock = stackalloc byte[4];
            OperationStatus blockStatus = Base64.EncodeToUtf8(
                rawBlock,
                encodedBlock,
                out int blockConsumed,
                out int blockWritten,
                isFinalBlock: false);

            if (blockStatus != OperationStatus.Done || blockConsumed != 3 || blockWritten != 4)
            {
                throw new InvalidOperationException("Unexpected Base64 block encoding failure.");
            }

            _inner.Write(encodedBlock);
            source = source[needed..];
            _carryCount = 0;
        }

        // Encode complete 3-byte groups directly from the caller's buffer. The output side uses
        // one pooled buffer for the whole response, avoiding combined/input copies and per-write GC.
        int wholeBytes = source.Length - (source.Length % 3);
        int maxRawBytesPerPass = (encodedBuffer.Length / 4) * 3;

        while (wholeBytes > 0)
        {
            int rawBytesThisPass = Math.Min(wholeBytes, maxRawBytesPerPass);
            ReadOnlySpan<byte> rawChunk = source[..rawBytesThisPass];

            OperationStatus status = Base64.EncodeToUtf8(
                rawChunk,
                encodedBuffer,
                out int consumed,
                out int written,
                isFinalBlock: false);

            if (status != OperationStatus.Done || consumed != rawBytesThisPass)
            {
                throw new InvalidOperationException("Unexpected Base64 streaming encoding failure.");
            }

            _inner.Write(encodedBuffer, 0, written);

            source = source[consumed..];
            wholeBytes -= consumed;
        }

        // Preserve only the final 1-2 bytes for the next call. No slice allocation is required.
        if (!source.IsEmpty)
        {
            source.CopyTo(_carry);
            _carryCount = source.Length;
        }
    }

    /// <summary>
    /// Flushes only the transport. Base64 padding is emitted exclusively by FinalizeEncoding().
    /// </summary>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Flush();
    }

    /// <summary>
    /// Writes the final padded Base64 group, if any, and flushes the underlying transport.
    /// </summary>
    public void FinalizeEncoding()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
        {
            return;
        }

        _finalized = true;

        if (_carryCount > 0)
        {
            Span<byte> finalBlock = stackalloc byte[4];
            OperationStatus status = Base64.EncodeToUtf8(
                _carry.AsSpan(0, _carryCount),
                finalBlock,
                out int consumed,
                out int written,
                isFinalBlock: true);

            if (status != OperationStatus.Done || consumed != _carryCount)
            {
                throw new InvalidOperationException("Unexpected Base64 finalization failure.");
            }

            _inner.Write(finalBlock[..written]);
            _carryCount = 0;
        }

        _inner.Flush();
    }

    /// <summary>
    /// Finalizes Base64 output, returns the pooled buffer, and optionally disposes the inner stream.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        if (disposing)
        {
            try
            {
                FinalizeEncoding();
            }
            finally
            {
                _disposed = true;

                if (_encodedBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_encodedBuffer);
                    _encodedBuffer = null;
                }

                if (!_leaveInnerOpen)
                {
                    _inner.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Read operations are not supported by this forward-only stream.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Base64EncodingStream is write-only.");

    /// <summary>
    /// Seeking is not supported by this forward-only stream.
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Base64EncodingStream is forward-only.");

    /// <summary>
    /// Changing length is not supported by this forward-only stream.
    /// </summary>
    public override void SetLength(long value) =>
        throw new NotSupportedException("Base64EncodingStream is forward-only.");
}
