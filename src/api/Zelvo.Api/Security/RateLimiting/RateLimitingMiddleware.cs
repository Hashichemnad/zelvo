using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Security.RateLimiting;

/// <summary>
/// Applies <see cref="RateLimitOptions"/> to <c>/api</c> requests: integrations (API keys and
/// OAuth clients) are counted per integration with the limit their administrator set (module 14),
/// signed-in users per tenant and user, visitors per client address (behind trusted proxies, the forwarded address).
/// Keys are hashed, so the cache holds no user ids or addresses. Every response carries
/// <c>RateLimit-Limit</c>, <c>RateLimit-Remaining</c> and <c>RateLimit-Reset</c>; refused requests
/// get 429 <c>rate-limited</c> with <c>Retry-After</c>.
/// </summary>
internal sealed partial class RateLimitingMiddleware(RequestDelegate next, IOptions<RateLimitOptions> options, ILogger<RateLimitingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IRateLimitStore store, ITenantContext tenant, Integrations.ApiClientDirectory clients)
    {
        var limits = options.Value;
        if (!limits.Enabled || !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var (kind, permits, partition) = await PartitionAsync(context, tenant, limits, clients).ConfigureAwait(false);
        // Integration limits are set per minute by administrators, whatever the installation's window.
        var window = kind == "client" ? TimeSpan.FromMinutes(1) : limits.Window;
        var count = await store.HitAsync(Key(kind, partition), window, context.RequestAborted).ConfigureAwait(false);
        if (count is not { } current)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var reset = Math.Max(1, (int)Math.Ceiling(current.ResetsIn.TotalSeconds));
        var headers = context.Response.Headers;
        headers["RateLimit-Limit"] = permits.ToString(CultureInfo.InvariantCulture);
        headers["RateLimit-Remaining"] = Math.Max(0, permits - current.Count).ToString(CultureInfo.InvariantCulture);
        headers["RateLimit-Reset"] = reset.ToString(CultureInfo.InvariantCulture);

        if (current.Count > permits)
        {
            RateLimitMetrics.Rejected.Add(1, new KeyValuePair<string, object?>("caller", kind));
            LogRejected(logger, kind, tenant.Slug);
            headers.RetryAfter = reset.ToString(CultureInfo.InvariantCulture);
            await ApiProblem.WriteAsync(context, StatusCodes.Status429TooManyRequests, "rate-limited",
                "Too many requests. Try again after the time in Retry-After.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static async Task<(string Kind, int Permits, string Partition)> PartitionAsync(
        HttpContext context, ITenantContext tenant, RateLimitOptions limits, Integrations.ApiClientDirectory clients)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true
            && await clients.ForPrincipalAsync(user, context.RequestAborted).ConfigureAwait(false) is { } client)
        {
            return ("client", client.RateLimitPerMinute, $"{tenant.TenantId:N}|{client.Id:N}");
        }

        var subject = user.Identity?.IsAuthenticated == true
            ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")
            : null;
        return subject is not null
            ? ("user", limits.SignedInPermits, $"{tenant.TenantId:N}|{subject}")
            : ("visitor", limits.VisitorPermits, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static string Key(string kind, string partition) =>
        $"zelvo:rl:{kind}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partition)))[..32]}";

    [LoggerMessage(Level = LogLevel.Information, Message = "Rate limit reached for a {Caller} of tenant {Tenant}.")]
    private static partial void LogRejected(ILogger logger, string caller, string? tenant);
}
