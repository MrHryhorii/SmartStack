using System.Threading.Channels;

namespace ONNX_Runner.Services;

/// <summary>
/// Універсальний буферизований потік-шлюз.
/// Накопичує дані до вказаного розміру або відправляє їх негайно при виклику Flush() чи Dispose().
/// </summary>
public class BridgingStream(ChannelWriter<byte[]> writer, int minChunkSizeBytes = 8192) : Stream
{
    private readonly ChannelWriter<byte[]> _writer = writer;
    private readonly MemoryStream _buffer = new();
    private readonly int _minChunkSizeBytes = minChunkSizeBytes;

    private long _totalBytesWritten = 0;

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        _buffer.Write(buffer, offset, count);
        _totalBytesWritten += count;

        // Якщо буферизація увімкнена і ми набрали потрібний об'єм - відправляємо блок
        if (_minChunkSizeBytes > 0 && _buffer.Length >= _minChunkSizeBytes)
        {
            PushToChannel();
        }
    }

    // Будь-хто ззовні може скомандувати "Відправляй що є!"
    public override void Flush()
    {
        PushToChannel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Виштовхуємо залишки при знищенні потоку (остання "буква")
            PushToChannel();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void PushToChannel()
    {
        if (_buffer.Length == 0) return;

        _writer.TryWrite(_buffer.ToArray());
        _buffer.SetLength(0); // Очищаємо буфер
    }

    // ==========================================
    // СТАНДАРТНІ ЗАГЛУШКИ
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