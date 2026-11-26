using System.Diagnostics.Metrics;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Security;

/// <summary>
/// Task F7 (ASVS V16.3.2, V16.3.3): refused API calls are security events. Every 401 and 403 on
/// <c>/api</c> is written to the service log as a warning (method, route pattern, status, kind of
/// caller and tenant; no personal data, tokens or query strings) and counted in
/// <c>zelvo.security.refused</c>. Calls refused for a known tenant are also written to the audit
/// log (category Security): <c>access.denied</c> for a caller who may not do something, and
/// <c>access.unauthenticated</c> when a credential was sent but not accepted (expired, revoked,
/// forged or for another tenant). The entry is written in its own scope after the response, so
/// the refused call's own changes are never saved by accident and a logging failure never changes
/// the answer.
/// </summary>
internal sealed partial class SecurityEventMiddleware(RequestDelegate next, IServiceScopeFactory scopes, ILogger<SecurityEventMiddleware> logger)
{
    private static readonly Meter Meter = new(Zelvo.Observability.ZelvoTelemetry.ApiMeter);

    private static readonly Counter<long> Refused =
        Meter.CreateCounter<long>("zelvo.security.refused", description: "API calls refused with 401 or 403, by status and kind of caller.");

    public async Task InvokeAsync(HttpContext context, ITenantContext tenant, AuditContext auditContext)
    {
        await next(context).ConfigureAwait(false);

        var status = context.Response.StatusCode;
        if (status is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            || !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var request = context.Request;
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
        var credentialSent = request.Headers.Authorization.Count > 0 || request.Headers.ContainsKey("X-Api-Key");
        var actor = auditContext.ActorType;
        Refused.Add(1, new KeyValuePair<string, object?>("status", status), new KeyValuePair<string, object?>("caller", actor.ToString()));
        LogRefused(logger, status, request.Method, route, actor, credentialSent, tenant.Slug ?? "(none)");

        var action = status == StatusCodes.Status403Forbidden ? "access.denied"
            : credentialSent ? "access.unauthenticated"
            : null;
        if (action is null || tenant.TenantId is not { } tenantId || tenant.Slug is not { } slug)
        {
            // Visitors without a credential asking for a signed-in page: logged and counted only.
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
            var audit = scope.ServiceProvider.GetRequiredService<AuditContext>();
            audit.SetActor(actor, auditContext.ActorId, auditContext.ActorName);
            foreach (var (key, value) in auditContext.Metadata)
            {
                audit.SetMetadata(key, value);
            }

            await scope.ServiceProvider.GetRequiredService<IAuditLog>().RecordAsync(AuditCategory.Security, action, "api", null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["method"] = request.Method,
                    ["route"] = route,
                    ["status"] = status,
                    ["credential"] = credentialSent,
                }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWriteFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Security event: refused {Status} for {Method} {Route} (caller {Caller}, credential sent {CredentialSent}, tenant {Tenant}).")]
    private static partial void LogRefused(ILogger logger, int status, string method, string route, AuditActorType caller, bool credentialSent, string tenant);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write the audit entry of a refused API call.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);
}
