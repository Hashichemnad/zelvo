using System.Diagnostics.Metrics;

namespace Zelvo.Api.Security.RateLimiting;

/// <summary>Metrics of rate limiting (meter <c>Zelvo.Api</c>).</summary>
internal static class RateLimitMetrics
{
    private static readonly Meter Meter = new(Zelvo.Observability.ZelvoTelemetry.ApiMeter);

    /// <summary>Requests refused with 429, by kind of caller (<c>user</c> or <c>visitor</c>).</summary>
    public static readonly Counter<long> Rejected = Meter.CreateCounter<long>("zelvo.rate_limit.rejected", description: "Requests refused because a rate limit was reached.");

    /// <summary>Requests let through because the counters could not be reached.</summary>
    public static readonly Counter<long> StoreErrors = Meter.CreateCounter<long>("zelvo.rate_limit.store_errors", description: "Requests allowed without a rate limit check because the cache was unavailable.");
}
