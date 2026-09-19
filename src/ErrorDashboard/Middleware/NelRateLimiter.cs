using System.Collections.Concurrent;

namespace Our.Umbraco.ErrorDashboard.Middleware;

/// <summary>
///     Fixed-window per-client rate limiter for the public collector.
/// </summary>
/// <remarks>
///     Deliberately crude. The collector is unauthenticated, so it needs *some* ceiling, but the data
///     is telemetry rather than anything transactional - shedding a little of it under abuse costs
///     nothing, and a precise limiter would cost more than the thing it protects.
/// </remarks>
public sealed class NelRateLimiter
{
    /// <summary>Beyond this many tracked clients the whole window is dropped, bounding memory under a spray attack.</summary>
    private const int MaxTrackedClients = 50_000;

    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, int> _counts = new();
    private readonly Lock _windowLock = new();

    private DateTime _windowStartUtc = DateTime.UtcNow;

    /// <summary>Records a request and reports whether the client is still within its allowance.</summary>
    public bool IsAllowed(string clientKey, int limitPerMinute, DateTime utcNow)
    {
        if (limitPerMinute <= 0)
        {
            return true;
        }

        RollWindowIfElapsed(utcNow);

        if (_counts.Count >= MaxTrackedClients)
        {
            _counts.Clear();
        }

        int count = _counts.AddOrUpdate(clientKey, 1, static (_, existing) => existing + 1);
        return count <= limitPerMinute;
    }

    private void RollWindowIfElapsed(DateTime utcNow)
    {
        if (utcNow - _windowStartUtc < WindowLength)
        {
            return;
        }

        lock (_windowLock)
        {
            if (utcNow - _windowStartUtc < WindowLength)
            {
                return;
            }

            _counts.Clear();
            _windowStartUtc = utcNow;
        }
    }
}
