namespace Zelvo.Api.Security.RateLimiting;

/// <summary>
/// Baseline rate limits of the API (configuration section <c>RateLimiting</c>), per signed-in user
/// and per client address for visitors, counted in fixed windows shared by every API instance
/// (Valkey). These protect the installation as a whole; limits per API key that administrators set
/// come with API keys (module 14).
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "RateLimiting";

    /// <summary>Whether limits apply.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Length of a counting window.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Requests a signed-in user may make per window, per tenant.</summary>
    public int SignedInPermits { get; set; } = 600;

    /// <summary>Requests a visitor (not signed in) may make per window, per client address.</summary>
    public int VisitorPermits { get; set; } = 120;
}
