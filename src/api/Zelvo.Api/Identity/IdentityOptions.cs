namespace Zelvo.Api.Identity;

/// <summary>
/// Identity settings (section <c>Identity</c>). Each tenant has its own Keycloak realm named
/// <c>{RealmPrefix}{tenant slug}</c>; tokens are accepted only from the realm of the request's tenant.
/// </summary>
internal sealed class IdentityOptions
{
    public const string Section = "Identity";

    /// <summary>Keycloak's address as browsers see it; token issuers start with it.</summary>
    public string PublicUrl { get; set; } = "http://localhost:8180";

    /// <summary>Keycloak's address from the server (keys, admin API). Defaults to <see cref="PublicUrl"/>.</summary>
    public string? InternalUrl { get; set; }

    /// <summary>Prefix of tenant realm names.</summary>
    public string RealmPrefix { get; set; } = "zelvo-";

    /// <summary>Audience the API requires in access tokens.</summary>
    public string Audience { get; set; } = "zelvo-api";

    /// <summary>Token <c>acr</c> values that mean the user signed in with a second factor.</summary>
    public IList<string> MfaAcrValues { get; init; } = ["mfa"];

    /// <summary>Allowed clock difference when checking token times.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Client id of the Zelvo service account in Keycloak's master realm.</summary>
    public string ProvisionerClientId { get; set; } = "zelvo-provisioner";

    /// <summary>Secret of <see cref="ProvisionerClientId"/>. Empty: the API cannot change Keycloak settings.</summary>
    public string? ProvisionerClientSecret { get; set; }

    public string BackchannelUrl => (InternalUrl is { Length: > 0 } url ? url : PublicUrl).TrimEnd('/');

    public string RealmFor(string tenantSlug) => RealmPrefix + tenantSlug;

    public string IssuerFor(string tenantSlug) => $"{PublicUrl.TrimEnd('/')}/realms/{RealmFor(tenantSlug)}";
}
