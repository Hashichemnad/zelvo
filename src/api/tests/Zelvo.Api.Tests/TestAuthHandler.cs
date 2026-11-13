using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zelvo.Api.Security;

namespace Zelvo.Api.Tests;

/// <summary>
/// Test-only sign-in from headers: <c>X-Test-User</c>, <c>X-Test-Roles</c> (comma-separated) and
/// <c>X-Test-Tenant</c> (tenant id claim). Without <c>X-Test-User</c> the request is anonymous.
/// </summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user),
            new("name", $"Test user {user}"),
        };
        claims.AddRange(Request.Headers["X-Test-Roles"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(role => new Claim(ClaimTypes.Role, role)));
        if (Request.Headers["X-Test-Tenant"].ToString() is { Length: > 0 } tenant)
        {
            claims.Add(new Claim(ZelvoClaims.TenantId, tenant));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
