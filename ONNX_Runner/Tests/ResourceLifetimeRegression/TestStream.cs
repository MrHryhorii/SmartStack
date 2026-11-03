/// <summary>Injects write failures while keeping response ownership observable.</summary>
sealed class TestStream(int allowedWrites = int.MaxValue) : Stream
{
    private readonly MemoryStream _inner = new();
    private int _writes;
    public bool Closed { get; private set; }
    public override bool CanRead => false;
    public override bool CanWrite => !Closed;
    public override bool CanSeek => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long length) => _inner.SetLength(length);
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_writes++ >= allowedWrites) throw new IOException("Injected response write failure.");
        _inner.Write(buffer);
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    protected override void Dispose(bool disposing)
    {
        Closed = true;
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
