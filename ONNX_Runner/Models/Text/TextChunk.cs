namespace ONNX_Runner.Models.Text;

/// <summary>
/// Represents an isolated segment of text along with its predicted language and metadata.
/// </summary>
public record TextChunk
{
    public required string Text { get; init; }
    public required string DetectedLanguage { get; init; }
    public double Probability { get; init; }
    public bool IsReliable { get; init; }
    public bool IsPunctuationOrSpace { get; init; }
    public string Script { get; init; } = "None";
    public IReadOnlyList<string> RawTop5 { get; init; } = Array.Empty<string>();

    // True if the text is a pre-formatted raw phoneme block. Explicit [[...]] blocks always
    // qualify; ambiguous [...] and /.../ forms are admitted only after IPA validation upstream.
    // Skips language detection and eSpeak, going straight to fallback validation.
    public bool IsRawPhonemes { get; init; }

    // Structural syntax is preserved until native character-name pronunciation.
    public bool IsTechnical { get; init; }
}
