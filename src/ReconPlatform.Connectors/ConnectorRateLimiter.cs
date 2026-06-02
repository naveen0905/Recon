using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace ReconPlatform.Connectors;

/// <summary>
/// Per-source sliding-window rate limiter for connectors.
/// Configured via <see cref="ReconPlatform.Config.Models.SourceConfig.RateLimitPerMinute"/>.
/// Default: 60 requests per minute per source.
/// Connectors should acquire a lease before each HTTP/SQL call and dispose it when done.
/// </summary>
public sealed class ConnectorRateLimiter : IDisposable
{
    private readonly ConcurrentDictionary<string, SlidingWindowRateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Acquires a rate-limit lease for <paramref name="sourceId"/>.
    /// Uses a per-source sliding window of 60 s with <paramref name="permitLimit"/> permits.
    /// </summary>
    public ValueTask<RateLimitLease> AcquireAsync(
        string sourceId,
        int permitLimit = 60,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var limiter = _limiters.GetOrAdd(sourceId, _ => CreateLimiter(permitLimit));
        return limiter.AcquireAsync(permitCount: 1, cancellationToken: ct);
    }

    private static SlidingWindowRateLimiter CreateLimiter(int permitLimit) =>
        new(new SlidingWindowRateLimiterOptions
        {
            PermitLimit          = permitLimit,
            Window               = TimeSpan.FromMinutes(1),
            SegmentsPerWindow    = 6,   // 10-second buckets
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit           = 0,   // do not queue — fail fast
            AutoReplenishment    = true,
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var limiter in _limiters.Values)
            limiter.Dispose();

        _limiters.Clear();
    }
}
