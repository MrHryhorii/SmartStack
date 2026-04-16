namespace ONNX_Runner.Models;

/// <summary>
/// General configuration settings for the API endpoints.
/// </summary>
public class ApiSettings
{
    /// <summary>
    /// The maximum allowed number of characters for incoming text requests.
    /// A value of 0 means unlimited (ideal for local/personal use).
    /// If the limit is greater than 0, any text exceeding this length will be automatically truncated 
    /// to prevent Out-Of-Memory (OOM) errors and excessive server load.
    /// </summary>
    public int MaxTextLength { get; set; } = 0;
}