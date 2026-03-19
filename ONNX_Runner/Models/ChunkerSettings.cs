namespace ONNX_Runner.Models;

public class ChunkerSettings
{
    public int MaxChunkLength { get; set; } = 250;
    // Базова пауза між реченнями в секундах (для нормальної швидкості 1.0)
    public float SentencePauseSeconds { get; set; } = 0.4f;
}