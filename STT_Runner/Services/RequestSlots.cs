using System.Threading.RateLimiting;

namespace STT_Runner.Services;

/// <summary>
/// Bounds active request pipelines and waiting requests independently of the
/// fixed-window HTTP rate limit and the Whisper inference semaphore.
/// </summary>
public sealed class RequestSlots : IDisposable
{
    private readonly ConcurrencyLimiter _limiter;
    private readonly TimeSpan _waitTimeout;

    public RequestSlots(int concurrency, int queueLimit, int queueWaitSeconds)
    {
        if (concurrency < 1 || queueLimit < 0 || queueWaitSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrency),
                "Concurrency must be positive, queue size nonnegative, and wait timeout positive.");

        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = concurrency,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        _waitTimeout = TimeSpan.FromSeconds(queueWaitSeconds);
    }

    public async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        // The linked token removes a timed-out or disconnected client from the queue.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_waitTimeout);
        return await _limiter.AcquireAsync(1, timeout.Token);
    }

    public object GetStatus()
    {
        RateLimiterStatistics? statistics = _limiter.GetStatistics();
        return new
        {
            available = statistics?.CurrentAvailablePermits ?? 0,
            waiting = statistics?.CurrentQueuedCount ?? 0
        };
    }

    public void Dispose() => _limiter.Dispose();
}
