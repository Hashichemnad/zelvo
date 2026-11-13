using System.Diagnostics;
using System.Security.Claims;
using Zelvo.Data.Auditing;

namespace Zelvo.Api.Auditing;

/// <summary>Records who is calling and from where, for every audit entry of the request.</summary>
internal sealed class AuditContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, AuditContext auditContext)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            auditContext.SetActor(
                AuditActorType.User,
                user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"),
                user.FindFirstValue("name") ?? user.Identity.Name);
        }
        else
        {
            auditContext.SetActor(AuditActorType.Anonymous, null, null);
        }

        auditContext.SetMetadata("correlationId", Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);
        auditContext.SetMetadata("clientAddress", context.Connection.RemoteIpAddress?.ToString());
        auditContext.SetMetadata("userAgent", context.Request.Headers.UserAgent.ToString());
        return next(context);
    }
}
