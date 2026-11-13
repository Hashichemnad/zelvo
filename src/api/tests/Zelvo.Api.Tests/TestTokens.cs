using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zelvo.Api.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>
/// Issues access tokens shaped like the ones Keycloak issues for tenant realms, signed with a
/// test key that the test API trusts for every realm. The API validates them with its real checks.
/// </summary>
internal static class TestTokens
{
    public const string PublicUrl = "http://keycloak.test";

    private static readonly RSA Rsa = RSA.Create(2048);

    public static RsaSecurityKey Key { get; } = new(Rsa) { KeyId = "test-key" };

    public static string IssuerFor(Tenant tenant) => $"{PublicUrl}/realms/zelvo-{tenant.Slug}";

    public static string Issue(
        Tenant tenant,
        string subject,
        bool mfa = true,
        string? issuer = null,
        string audience = "zelvo-api",
        Guid? tenantClaim = null,
        DateTime? expires = null,
        string? email = null,
        string? name = null,
        SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = subject,
            [ZelvoClaimTypes.TenantId] = (tenantClaim ?? tenant.Id).ToString(),
            ["acr"] = mfa ? "mfa" : "pwd",
        };
        if (email is not null)
        {
            claims["email"] = email;
        }

        if (name is not null)
        {
            claims["name"] = name;
        }

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? IssuerFor(tenant),
            Audience = audience,
            Claims = claims,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }
}

/// <summary>Trusts <see cref="TestTokens.Key"/> for every realm.</summary>
internal sealed class TestRealmSigningKeys : IRealmSigningKeys
{
    public Task<IReadOnlyCollection<SecurityKey>> GetAsync(string realm, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<SecurityKey>>([TestTokens.Key]);
}

/// <summary>Records what the API would change in Keycloak; can be told to fail.</summary>
internal sealed class FakeKeycloakAdmin : IKeycloakAdmin
{
    public List<(string Realm, Zelvo.Data.Identity.EmailCodeSignIn Mode, IReadOnlyList<string> Domains)> Calls { get; } = [];

    public bool Fail { get; set; }

    public Task SetEmailCodeSignInAsync(string realm, Zelvo.Data.Identity.EmailCodeSignIn mode, IReadOnlyList<string> allowedDomains, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        Calls.Add((realm, mode, allowedDomains));
        return Task.CompletedTask;
    }
}
