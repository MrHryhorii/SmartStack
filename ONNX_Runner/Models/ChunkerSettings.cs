namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for the text chunking and sentence boundary detection module.
/// </summary>
public class ChunkerSettings
{
    /// <summary>
    /// The maximum character length of a single text chunk before forcing an emergency split.
    /// Prevents GPU/CPU timeouts or memory overloads on extremely long, run-on sentences.
    /// </summary>
    public int MaxChunkLength { get; set; } = 200;

    /// <summary>
    /// Allows one conservative punctuation split before the first generated audio chunk
    /// of each request. Normal sentence chunking is used after that first chunk.
    /// </summary>
    public bool EarlySplit { get; set; } = true;

    /// <summary>
    /// The base silence duration (in seconds) inserted between generated sentences 
    /// at a standard 1.0x reading speed. Scales dynamically with the requested speech rate.
    /// </summary>
    public float SentencePauseSeconds { get; set; } = 0.3f;
}