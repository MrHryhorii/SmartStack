namespace ONNX_Runner.Models;

public class RateLimitSettings
{
    /// <summary>
    /// Maximum number of requests allowed within the specified time window before rate limiting is applied.
    /// </summary>
    public int PermitLimit { get; set; } = 20;

    /// <summary>
    /// Time window in seconds during which the specified number of requests (PermitLimit) is allowed. After this window expires, the request count resets.
    /// </summary>
    public int WindowSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of excess requests that can be queued when the rate limit is exceeded. If the queue is full, additional requests will be rejected until space is available.
    /// </summary>
    public int QueueLimit { get; set; } = 5;
}