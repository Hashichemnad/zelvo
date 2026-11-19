namespace Zelvo.Api.Security.RateLimiting;

/// <summary>Counts requests per key in fixed windows.</summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Counts one request for <paramref name="key"/>; returns the count in the current window and
    /// the time until it ends, or null when the store is unavailable (requests are then allowed).
    /// </summary>
    Task<RateLimitCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>Requests counted in the current window and the time left in it.</summary>
public readonly record struct RateLimitCount(long Count, TimeSpan ResetsIn);
