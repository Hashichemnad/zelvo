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
        SecurityKey? key = null,
        IReadOnlyDictionary<string, object>? extraClaims = null)
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

        foreach (var (type, value) in extraClaims ?? new Dictionary<string, object>())
        {
            claims[type] = value;
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
    public List<(string Realm, RealmBranding Branding)> BrandingCalls { get; } = [];

    /// <summary>Users removed by erasure (task F5).</summary>
    public List<(string Realm, string Subject)> DeletedUsers { get; } = [];

    public Task DeleteUserAsync(string realm, string subject, CancellationToken cancellationToken)
    {
        lock (DeletedUsers)
        {
            DeletedUsers.Add((realm, subject));
        }

        return Task.CompletedTask;
    }

    /// <summary>Users turned on or off (task F7).</summary>
    public List<(string Realm, string Subject, bool Enabled)> EnabledCalls { get; } = [];

    /// <summary>Sign-outs: a user's subject, or null for everyone in the realm (task F7).</summary>
    public List<(string Realm, string? Subject)> SignOuts { get; } = [];

    public Task SetUserEnabledAsync(string realm, string subject, bool enabled, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (EnabledCalls)
        {
            EnabledCalls.Add((realm, subject, enabled));
        }

        return Task.CompletedTask;
    }

    public Task SignOutAsync(string realm, string? subject, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (SignOuts)
        {
            SignOuts.Add((realm, subject));
        }

        return Task.CompletedTask;
    }

    public Task ApplyBrandingAsync(string realm, RealmBranding branding, CancellationToken cancellationToken)
    {
        BrandingCalls.Add((realm, branding));
        return Task.CompletedTask;
    }

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

    public List<(string Realm, DirectorySignInKind Kind, DirectorySignIn? SignIn)> DirectorySignInCalls { get; } = [];

    public Task ApplyDirectorySignInAsync(string realm, DirectorySignInKind kind, DirectorySignIn? signIn, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        DirectorySignInCalls.Add((realm, kind, signIn));
        return Task.CompletedTask;
    }

    public List<(string Realm, UaePassProvider Provider)> UaePassCalls { get; } = [];

    public Task ApplyUaePassAsync(string realm, UaePassProvider provider, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        UaePassCalls.Add((realm, provider));
        return Task.CompletedTask;
    }

    public List<(string Realm, string Subject, string? EmiratesId)> EmiratesIdCalls { get; } = [];

    public Task SetEmiratesIdAsync(string realm, string subject, string? emiratesId, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        EmiratesIdCalls.Add((realm, subject, emiratesId));
        return Task.CompletedTask;
    }

    /// <summary>Demo users by realm and username (email), with their Keycloak id.</summary>
    public Dictionary<(string Realm, string Username), string> DemoUsers { get; } = [];

    public List<(string Realm, IReadOnlySet<string> Keep)> DeleteCalls { get; } = [];

    /// <summary>Keycloak keeps a user's id, so the fake gives the same id for the same realm and username.</summary>
    public static string IdFor(string realm, string username) =>
        new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{realm}|{username}"))[..16]).ToString();

    public Task<string> EnsureDemoUserAsync(string realm, string email, string firstName, string lastName, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (DemoUsers)
        {
            var id = IdFor(realm, email);
            DemoUsers[(realm, email)] = id;
            return Task.FromResult(id);
        }
    }

    public Task<int> DeleteUsersExceptAsync(string realm, IReadOnlySet<string> keep, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (DemoUsers)
        {
            DeleteCalls.Add((realm, keep));
            return Task.FromResult(0);
        }
    }
    /// <summary>OAuth clients created for integrations, by realm and client id.</summary>
    public Dictionary<(string Realm, string ClientId), (IntegrationClientSpec Spec, bool Enabled, string Secret)> IntegrationClients { get; } = [];

    public Task<IntegrationClientCredentials> CreateIntegrationClientAsync(string realm, IntegrationClientSpec spec, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (IntegrationClients)
        {
            var secret = $"secret-{Guid.NewGuid():N}";
            IntegrationClients[(realm, spec.ClientId)] = (spec, true, secret);
            return Task.FromResult(new IntegrationClientCredentials($"internal-{spec.ClientId}", secret, ServiceAccountFor(spec.ClientId)));
        }
    }

    /// <summary>The service account subject the fake gives a client.</summary>
    public static string ServiceAccountFor(string clientId) =>
        new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"sa|{clientId}"))[..16]).ToString();

    public Task SetClientEnabledAsync(string realm, string internalId, bool enabled, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (IntegrationClients)
        {
            var key = (realm, internalId["internal-".Length..]);
            var current = IntegrationClients[key];
            IntegrationClients[key] = (current.Spec, enabled, current.Secret);
        }

        return Task.CompletedTask;
    }

    public Task<string> RegenerateClientSecretAsync(string realm, string internalId, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (IntegrationClients)
        {
            var key = (realm, internalId["internal-".Length..]);
            var current = IntegrationClients[key];
            var secret = $"secret-{Guid.NewGuid():N}";
            IntegrationClients[key] = (current.Spec, current.Enabled, secret);
            return Task.FromResult(secret);
        }
    }

    /// <summary>Users created by imports, by realm and email.</summary>
    public Dictionary<(string Realm, string Email), (string Id, string FirstName, string LastName)> ImportedUsers { get; } = [];

    public Task<string> FindOrCreateUserAsync(string realm, string email, string firstName, string lastName, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KeycloakAdminException("Keycloak is down (test).");
        }

        lock (ImportedUsers)
        {
            var id = IdFor(realm, email);
            ImportedUsers[(realm, email)] = (id, firstName, lastName);
            return Task.FromResult(id);
        }
    }
}
