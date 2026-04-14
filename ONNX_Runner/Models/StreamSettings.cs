namespace ONNX_Runner.Models;

/// <summary>
/// Configuration for network audio streaming (Chunked Transfer Encoding).
/// </summary>
public class StreamSettings
{
    /// <summary>
    /// Globally enables or disables HTTP chunked streaming.
    /// </summary>
    public bool EnableStreaming { get; set; } = true;

    /// <summary>
    /// If true, the server flushes audio to the network immediately after generating a full sentence, 
    /// ensuring minimal time-to-first-audio (TTFA) for the client.
    /// </summary>
    public bool FlushAfterEachSentence { get; set; } = true;

    /// <summary>
    /// The minimum buffer size before forcing a network chunk dispatch 
    /// (if waiting for a full sentence takes too long).
    /// </summary>
    public int MinChunkSizeKb { get; set; } = 8;
}