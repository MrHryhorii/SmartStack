using System.Security.Cryptography;

namespace ONNX_Runner.Services;

/// <summary>
/// Wraps an output <see cref="Stream"/> to encode written bytes into base64 text on the fly.
/// Acts as a transparent drop-in replacement for the underlying stream (e.g., <c>BridgingStream</c> 
/// for <c>text/plain</c> streaming, or <c>MemoryStream</c> for buffering), requiring zero changes 
/// to the existing audio pipeline.
/// 
/// USAGE: Wrap the inner stream and write bytes normally. You MUST call <c>FinalizeEncoding()</c> 
/// exactly once at the end of the response to append the final '=' padding. Do NOT call it from 
/// <c>Flush()</c>, as premature padding will corrupt subsequent data.
/// 
/// WHY A CUSTOM TRANSFORM: Base64 encodes in fixed 3-byte blocks. Audio chunks arrive in arbitrary 
/// sizes. This stream uses <see cref="ToBase64Transform"/> to safely carry over 0-2 leftover bytes 
/// across <c>Write</c> calls, preventing chunk-boundary corruption.
/// 
/// LIMITATION: Streaming pure base64 reduces Time-To-First-Byte, but clients cannot decode or play 
/// the audio until the entire sequence is completed and finalized.
/// </summary>
public sealed class Base64EncodingStream(Stream inner, bool leaveInnerOpen = false) : Stream
{
    private readonly ICryptoTransform _transform = new ToBase64Transform();

    // 0–2 raw bytes from the previous Write() that didn't complete a 3-byte group yet.
    private byte[] _carry = [];
    private bool _finalized;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException("Base64EncodingStream is write-only and forward-only.");
    public override long Position
    {
        get => throw new NotSupportedException("Base64EncodingStream is write-only and forward-only.");
        set => throw new NotSupportedException("Base64EncodingStream is write-only and forward-only.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_finalized) throw new InvalidOperationException("Cannot write after FinalizeEncoding() has been called.");
        if (count == 0) return;

        // Combine any leftover bytes from the previous call with the newly written data —
        // this is what lets chunk boundaries fall anywhere without corrupting a 3-byte group.
        byte[] combined;
        if (_carry.Length > 0)
        {
            combined = new byte[_carry.Length + count];
            Buffer.BlockCopy(_carry, 0, combined, 0, _carry.Length);
            Buffer.BlockCopy(buffer, offset, combined, _carry.Length, count);
        }
        else
        {
            combined = new byte[count];
            Buffer.BlockCopy(buffer, offset, combined, 0, count);
        }

        int wholeGroups = combined.Length / 3;
        int wholeBytes = wholeGroups * 3;

        if (wholeBytes > 0)
        {
            // ToBase64Transform.CanTransformMultipleBlocks is false: it must be called once
            // per exact 3-byte group (its InputBlockSize), never with a larger count.
            var outBuf = new byte[wholeGroups * 4]; // OutputBlockSize is 4 per group.
            int written = 0;
            for (int i = 0; i < wholeBytes; i += 3)
            {
                written += _transform.TransformBlock(combined, i, 3, outBuf, written);
            }
            inner.Write(outBuf, 0, written);
        }

        int leftover = combined.Length - wholeBytes;
        _carry = leftover > 0 ? combined[wholeBytes..] : [];
    }

    /// <summary>
    /// Passes through to the inner stream so buffered bytes actually reach the network for a
    /// streaming response. Deliberately does NOT finalize the base64 sequence — see the class
    /// remarks on why that must stay separate from per-sentence Flush() calls.
    /// </summary>
    public override void Flush() => inner.Flush();

    /// <summary>
    /// Writes the final base64 group — including '=' padding if the total raw byte count
    /// wasn't a multiple of 3 — and flushes the inner stream. Call exactly once, after the
    /// last Write(), at true end-of-response. The base64 sequence is not valid/decodable
    /// until this has run.
    /// </summary>
    public void FinalizeEncoding()
    {
        if (_finalized) return;
        _finalized = true;

        byte[] finalBlock = _transform.TransformFinalBlock(_carry, 0, _carry.Length);
        if (finalBlock.Length > 0) inner.Write(finalBlock, 0, finalBlock.Length);
        inner.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Safety net: if the caller forgot to call FinalizeEncoding() explicitly (e.g. an
            // exception cut generation short), still emit a valid, decodable base64 sequence
            // for whatever was actually written, rather than silently dropping the tail.
            FinalizeEncoding();
            _transform.Dispose();
            if (!leaveInnerOpen) inner.Dispose();
        }
        base.Dispose(disposing);
    }

    // Not meaningful for a write-only, forward-only stream.
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Base64EncodingStream is write-only.");
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Base64EncodingStream is forward-only.");
    public override void SetLength(long value) =>
        throw new NotSupportedException("Base64EncodingStream is forward-only.");
}
