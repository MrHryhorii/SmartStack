namespace ONNX_Runner.Models;

public class StreamSettings
{
    // Чи використовувати HTTP Chunked Transfer Encoding
    public bool EnableStreaming { get; set; } = true;

    // Чи виштовхувати звук у мережу одразу після кожного згенерованого речення
    public bool FlushAfterEachSentence { get; set; } = true;

    // Мінімальний розмір чанка перед відправкою (якщо не чекаємо кінця речення)
    public int MinChunkSizeKb { get; set; } = 8;
}