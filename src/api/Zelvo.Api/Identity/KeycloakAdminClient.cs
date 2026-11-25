using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Identity;

/// <summary>What the API changes in Keycloak at run time.</summary>
internal interface IKeycloakAdmin
{
    /// <summary>Copies the email one-time code sign-in setting into the realm's sign-in flow.</summary>
    Task SetEmailCodeSignInAsync(string realm, EmailCodeSignIn mode, IReadOnlyList<string> allowedDomains, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or updates the realm's UAE PASS identity provider (GOV-1), its profile mapper and the
    /// first sign-in settings (automatic requester accounts, requesters' minimum level).
    /// </summary>
    Task ApplyUaePassAsync(string realm, UaePassProvider provider, CancellationToken cancellationToken);

    /// <summary>Records (or with <see langword="null"/> removes) the Emirates ID on a Keycloak user, for UAE PASS matching.</summary>
    Task SetEmiratesIdAsync(string realm, string subject, string? emiratesId, CancellationToken cancellationToken);

    /// <summary>
    /// Demo mode: creates or restores a user without a password (demo users sign in through the
    /// demo sign-in step only): enabled, verified email, the given names and no attributes. Returns
    /// its Keycloak id.
    /// </summary>
    Task<string> EnsureDemoUserAsync(string realm, string email, string firstName, string lastName, CancellationToken cancellationToken);

    /// <summary>Demo mode: deletes every user of the realm whose username is not in <paramref name="keep"/>; returns how many.</summary>
    Task<int> DeleteUsersExceptAsync(string realm, IReadOnlySet<string> keep, CancellationToken cancellationToken);

    /// <summary>
    /// Integrations (E1): creates a confidential client that may only use the client credentials
    /// grant, whose tokens are for the Zelvo API and carry the tenant and integration ids.
    /// </summary>
    Task<IntegrationClientCredentials> CreateIntegrationClientAsync(string realm, IntegrationClientSpec spec, CancellationToken cancellationToken);

    /// <summary>Integrations: turns a client on or off (off: Keycloak issues no more tokens).</summary>
    Task SetClientEnabledAsync(string realm, string internalId, bool enabled, CancellationToken cancellationToken);

    /// <summary>Integrations: gives a client a new secret and returns it; the old one stops working.</summary>
    Task<string> RegenerateClientSecretAsync(string realm, string internalId, CancellationToken cancellationToken);

    /// <summary>
    /// Bulk import of users: finds the user with this email, or creates one (enabled, email not yet
    /// verified, no password; they sign in with an email code, UAE PASS or "Forgot password").
    /// Returns its Keycloak id.
    /// </summary>
    Task<string> FindOrCreateUserAsync(string realm, string email, string firstName, string lastName, CancellationToken cancellationToken);

    /// <summary>
    /// Task F1: the sign-in page's product name and image (realm display name), and the web app's
    /// addresses: the installation's own plus the tenant's verified custom domains.
    /// </summary>
    Task ApplyBrandingAsync(string realm, RealmBranding branding, CancellationToken cancellationToken);

    /// <summary>
    /// Task E7: "Sign in with Microsoft" (an OIDC identity provider for the tenant's Entra
    /// directory, emails trusted) or sign-in with the directory password (read-only LDAP
    /// federation); <paramref name="signIn"/> null removes it.
    /// </summary>
    Task ApplyDirectorySignInAsync(string realm, DirectorySignInKind kind, DirectorySignIn? signIn, CancellationToken cancellationToken);
}

/// <summary>Which directory sign-in (task E7).</summary>
internal enum DirectorySignInKind
{
    /// <summary>Microsoft Entra ID (identity provider alias <c>entra</c>).</summary>
    Entra,

    /// <summary>LDAP / Active Directory (user federation <c>zelvo-ldap</c>).</summary>
    Ldap,
}

/// <summary>A directory's sign-in settings; the secret is the connector's credential (Entra client secret, LDAP bind password).</summary>
internal sealed record DirectorySignIn(
    string? DirectoryId, string? ClientId, string? Secret, string? Host, int Port, bool Ldaps, bool StartTls, string? BindDn, string? BaseDn, string? UserFilter, bool ActiveDirectory,
    string DisplayName);

/// <summary>What the sign-in pages of a realm show, and where the web app runs (task F1).</summary>
/// <param name="DisplayName">Product name (plain text).</param>
/// <param name="DisplayNameHtml">Product name with the sign-in image, built by Zelvo with every value encoded.</param>
/// <param name="WebUrls">Addresses of the web app: the installation's and each verified custom domain.</param>
internal sealed record RealmBranding(string DisplayName, string DisplayNameHtml, IReadOnlyList<string> WebUrls);

/// <summary>An integration's OAuth client to create.</summary>
/// <param name="ClientId">Keycloak client id, <c>zelvo-int-…</c>.</param>
/// <param name="Name">Name shown in Keycloak.</param>
/// <param name="TenantId">The tenant, as the <c>zelvo_tenant_id</c> claim.</param>
/// <param name="ApiClientId">The Zelvo integration id, as the <c>zelvo_api_client</c> claim.</param>
/// <param name="Audience">The API audience.</param>
internal sealed record IntegrationClientSpec(string ClientId, string Name, Guid TenantId, Guid ApiClientId, string Audience);

/// <summary>What Keycloak created for an integration.</summary>
/// <param name="InternalId">Keycloak's internal id of the client.</param>
/// <param name="Secret">The client secret (shown once).</param>
/// <param name="ServiceAccountSubject">The <c>sub</c> of the client's tokens.</param>
internal sealed record IntegrationClientCredentials(string InternalId, string Secret, string ServiceAccountSubject);

/// <summary>Keycloak refused or failed an admin request.</summary>
internal sealed class KeycloakAdminException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Calls the Keycloak admin REST API. The API signs in as the <c>zelvo-provisioner</c> service
/// account of the master realm, which only has admin roles for the tenant realms; the one-off
/// <c>migrate</c> command uses the bootstrap administrator to set that account up.
/// </summary>
internal sealed class KeycloakAdminClient : IKeycloakAdmin
{
    public const string HttpClientName = "keycloak-admin";

    /// <summary>Alias of the browser sign-in flow Zelvo creates in every tenant realm.</summary>
    public const string BrowserFlow = "zelvo browser";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<CancellationToken, Task<string>> _signIn;
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public KeycloakAdminClient(IHttpClientFactory httpClients, IOptions<IdentityOptions> options)
        : this(httpClients.CreateClient(HttpClientName), options.Value.BackchannelUrl, ClientCredentials(options.Value))
    {
    }

    private KeycloakAdminClient(HttpClient http, string baseUrl, Func<KeycloakAdminClient, CancellationToken, Task<(string, int)>> signIn)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _signIn = async cancellationToken =>
        {
            var (token, expiresIn) = await signIn(this, cancellationToken).ConfigureAwait(false);
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, expiresIn - 10));
            return token;
        };
    }

    /// <summary>A client signed in as the bootstrap administrator (used only by <c>migrate</c>).</summary>
    public static KeycloakAdminClient ForAdministrator(HttpClient http, string baseUrl, string user, string password) =>
        new(http, baseUrl, (self, ct) => self.RequestTokenAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = user,
            ["password"] = password,
        }, ct));

    private static Func<KeycloakAdminClient, CancellationToken, Task<(string, int)>> ClientCredentials(IdentityOptions options) =>
        (self, ct) => string.IsNullOrEmpty(options.ProvisionerClientSecret)
            ? throw new KeycloakAdminException("Identity:ProvisionerClientSecret is not set, so Zelvo cannot change Keycloak settings.")
            : self.RequestTokenAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ProvisionerClientId,
                ["client_secret"] = options.ProvisionerClientSecret,
            }, ct);

    public async Task SetEmailCodeSignInAsync(string realm, EmailCodeSignIn mode, IReadOnlyList<string> allowedDomains, CancellationToken cancellationToken)
    {
        var executions = await GetArrayAsync($"realms/{E(realm)}/authentication/flows/{E(BrowserFlow)}/executions", cancellationToken).ConfigureAwait(false);
        var execution = executions.FirstOrDefault(e => (string?)e?["providerId"] == "zelvo-email-code")
            ?? throw new KeycloakAdminException($"The sign-in flow of realm {realm} has no email code step.");
        await SetExecutionConfigAsync(realm, execution, "zelvo-email-code", EmailCodeConfig(mode, allowedDomains), cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyUaePassAsync(string realm, UaePassProvider provider, CancellationToken cancellationToken)
    {
        var path = $"realms/{E(realm)}/identity-provider/instances";
        var existing = await GetAsync($"{path}/{E(UaePassProvider.Alias)}", cancellationToken).ConfigureAwait(false);
        if (existing is null && provider.ClientSecret is null)
        {
            throw new KeycloakAdminException($"Realm {realm} has no UAE PASS provider yet, so a client secret is needed.");
        }

        var representation = UaePassRepresentation(provider);
        if (existing is null)
        {
            using var _ = await SendAsync(HttpMethod.Post, path, representation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            representation["internalId"] = existing["internalId"]?.DeepClone();
            using var _ = await SendAsync(HttpMethod.Put, $"{path}/{E(UaePassProvider.Alias)}", representation, cancellationToken).ConfigureAwait(false);
        }

        // The profile mapper keeps the UAE PASS level of each sign-in for Zelvo's tokens.
        var mappersPath = $"{path}/{E(UaePassProvider.Alias)}/mappers";
        var mappers = await GetArrayAsync(mappersPath, cancellationToken).ConfigureAwait(false);
        var mapper = new JsonObject
        {
            ["name"] = "zelvo uaepass profile",
            ["identityProviderAlias"] = UaePassProvider.Alias,
            ["identityProviderMapper"] = UaePassProvider.ProfileMapper,
            // FORCE: the mapper also runs after linking and at every sign-in, to keep the level.
            ["config"] = new JsonObject { ["syncMode"] = "FORCE" },
        };
        var current = mappers.FirstOrDefault(m => (string?)m?["identityProviderMapper"] == UaePassProvider.ProfileMapper);
        if (current is null)
        {
            using var _ = await SendAsync(HttpMethod.Post, mappersPath, mapper, cancellationToken).ConfigureAwait(false);
        }
        else if ((string?)current["config"]?["syncMode"] != "FORCE")
        {
            mapper["id"] = current["id"]?.DeepClone();
            using var _ = await SendAsync(HttpMethod.Put, $"{mappersPath}/{E((string)current["id"]!)}", mapper, cancellationToken).ConfigureAwait(false);
        }

        var executions = await GetArrayAsync($"realms/{E(realm)}/authentication/flows/{E(UaePassProvider.FirstLoginFlow)}/executions", cancellationToken).ConfigureAwait(false);
        var step = executions.FirstOrDefault(e => (string?)e?["providerId"] == UaePassProvider.FirstLoginStep)
            ?? throw new KeycloakAdminException($"The UAE PASS first sign-in flow of realm {realm} has no Zelvo step.");
        await SetExecutionConfigAsync(realm, step, UaePassProvider.FirstLoginStep, FirstLoginConfig(provider), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Settings of the Zelvo first UAE PASS sign-in step.</summary>
    public static Dictionary<string, string> FirstLoginConfig(UaePassProvider provider) => new(StringComparer.Ordinal)
    {
        ["createRequesters"] = provider.CreateRequesters ? "true" : "false",
        ["requesterMinimumLevel"] = provider.RequesterMinimumLevel.ToString(),
    };

    public async Task SetEmiratesIdAsync(string realm, string subject, string? emiratesId, CancellationToken cancellationToken)
    {
        var path = $"realms/{E(realm)}/users/{E(subject)}";
        var user = await GetAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new KeycloakAdminException($"User {subject} does not exist in realm {realm}.", HttpStatusCode.NotFound);
        var attributes = user["attributes"] as JsonObject ?? new JsonObject();
        if (emiratesId is null)
        {
            attributes.Remove(UaePassProvider.EmiratesIdAttribute);
        }
        else
        {
            attributes[UaePassProvider.EmiratesIdAttribute] = new JsonArray(emiratesId);
        }

        // Send back the whole representation so that the user's other details are kept.
        user["attributes"] = attributes.DeepClone();
        using var _ = await SendAsync(HttpMethod.Put, path, user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EnsureDemoUserAsync(string realm, string email, string firstName, string lastName, CancellationToken cancellationToken)
    {
        var found = await GetArrayAsync($"realms/{E(realm)}/users?email={E(email)}&exact=true", cancellationToken).ConfigureAwait(false);
        var user = new JsonObject
        {
            ["username"] = email,
            ["email"] = email,
            ["emailVerified"] = true,
            ["enabled"] = true,
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["attributes"] = new JsonObject(),
            ["requiredActions"] = new JsonArray(),
        };

        if (found.Count == 0)
        {
            using var _ = await SendAsync(HttpMethod.Post, $"realms/{E(realm)}/users", user, cancellationToken).ConfigureAwait(false);
            found = await GetArrayAsync($"realms/{E(realm)}/users?email={E(email)}&exact=true", cancellationToken).ConfigureAwait(false);
            return (string)found.Single()!["id"]!;
        }

        var id = (string)found[0]!["id"]!;
        user["id"] = id;
        using (var _ = await SendAsync(HttpMethod.Put, $"realms/{E(realm)}/users/{E(id)}", user, cancellationToken).ConfigureAwait(false))
        {
        }

        // Back to the sample state: no authenticator app or password a visitor may have added, and
        // no UAE PASS link.
        foreach (var credential in await GetArrayAsync($"realms/{E(realm)}/users/{E(id)}/credentials", cancellationToken).ConfigureAwait(false))
        {
            using var _ = await SendAsync(HttpMethod.Delete, $"realms/{E(realm)}/users/{E(id)}/credentials/{E((string)credential!["id"]!)}",
                null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        }

        foreach (var link in await GetArrayAsync($"realms/{E(realm)}/users/{E(id)}/federated-identity", cancellationToken).ConfigureAwait(false))
        {
            using var _ = await SendAsync(HttpMethod.Delete, $"realms/{E(realm)}/users/{E(id)}/federated-identity/{E((string)link!["identityProvider"]!)}",
                null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        }

        return id;
    }

    public async Task<IntegrationClientCredentials> CreateIntegrationClientAsync(string realm, IntegrationClientSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var client = new JsonObject
        {
            ["clientId"] = spec.ClientId,
            ["name"] = spec.Name,
            ["description"] = "Zelvo integration (Settings > Integrations). Managed by Zelvo; do not change here.",
            ["enabled"] = true,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["clientAuthenticatorType"] = "client-secret",
            ["standardFlowEnabled"] = false,
            ["implicitFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["serviceAccountsEnabled"] = true,
            ["frontchannelLogout"] = false,
            ["fullScopeAllowed"] = false,
            ["redirectUris"] = new JsonArray(),
            ["webOrigins"] = new JsonArray(),
            ["defaultClientScopes"] = new JsonArray("basic"),
            ["optionalClientScopes"] = new JsonArray(),
            ["protocolMappers"] = new JsonArray(
                Mapper("zelvo-api audience", "oidc-audience-mapper", new JsonObject
                {
                    ["included.client.audience"] = spec.Audience,
                    ["access.token.claim"] = "true",
                    ["id.token.claim"] = "false",
                }),
                HardcodedClaim("zelvo tenant id", Identity.ZelvoClaimTypes.TenantId, spec.TenantId.ToString()),
                HardcodedClaim("zelvo api client", Integrations.ApiClientClaims.ClientId, spec.ApiClientId.ToString())),
        };

        using (var _ = await SendAsync(HttpMethod.Post, $"realms/{E(realm)}/clients", client, cancellationToken).ConfigureAwait(false))
        {
        }

        var found = await GetArrayAsync($"realms/{E(realm)}/clients?clientId={E(spec.ClientId)}", cancellationToken).ConfigureAwait(false);
        var internalId = (string?)found.SingleOrDefault()?["id"]
            ?? throw new KeycloakAdminException($"Keycloak did not create client {spec.ClientId}.");
        var secret = await GetAsync($"realms/{E(realm)}/clients/{E(internalId)}/client-secret", cancellationToken).ConfigureAwait(false);
        var account = await GetAsync($"realms/{E(realm)}/clients/{E(internalId)}/service-account-user", cancellationToken).ConfigureAwait(false);
        return new IntegrationClientCredentials(
            internalId,
            (string?)secret?["value"] ?? throw new KeycloakAdminException("Keycloak sent no client secret."),
            (string?)account?["id"] ?? throw new KeycloakAdminException("Keycloak created no service account."));
    }

    public async Task SetClientEnabledAsync(string realm, string internalId, bool enabled, CancellationToken cancellationToken)
    {
        var path = $"realms/{E(realm)}/clients/{E(internalId)}";
        var client = await GetAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new KeycloakAdminException($"Client {internalId} does not exist in realm {realm}.", HttpStatusCode.NotFound);
        client["enabled"] = enabled;
        if (client is JsonObject obj)
        {
            obj.Remove("protocolMappers");
        }

        using var _ = await SendAsync(HttpMethod.Put, path, client, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RegenerateClientSecretAsync(string realm, string internalId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"realms/{E(realm)}/clients/{E(internalId)}/client-secret", null, cancellationToken).ConfigureAwait(false);
        var secret = await response.Content.ReadFromJsonAsync<JsonNode>(Json, cancellationToken).ConfigureAwait(false);
        return (string?)secret?["value"] ?? throw new KeycloakAdminException("Keycloak sent no client secret.");
    }

    public async Task<string> FindOrCreateUserAsync(string realm, string email, string firstName, string lastName, CancellationToken cancellationToken)
    {
        var path = $"realms/{E(realm)}/users?email={E(email)}&exact=true";
        var found = await GetArrayAsync(path, cancellationToken).ConfigureAwait(false);
        if (found.Count == 0)
        {
            var user = new JsonObject
            {
                ["username"] = email,
                ["email"] = email,
                ["emailVerified"] = false,
                ["enabled"] = true,
                ["firstName"] = firstName,
                ["lastName"] = lastName,
            };
            using (var _ = await SendAsync(HttpMethod.Post, $"realms/{E(realm)}/users", user, cancellationToken, allowConflict: true).ConfigureAwait(false))
            {
            }

            found = await GetArrayAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return (string?)(found.Count > 0 ? found[0] : null)?["id"] ?? throw new KeycloakAdminException($"Keycloak did not create a user for {email}.");
    }

    private static JsonObject Mapper(string name, string type, JsonObject config) => new()
    {
        ["name"] = name,
        ["protocol"] = "openid-connect",
        ["protocolMapper"] = type,
        ["config"] = config,
    };

    private static JsonObject HardcodedClaim(string name, string claim, string value) => Mapper(name, "oidc-hardcoded-claim-mapper", new JsonObject
    {
        ["claim.name"] = claim,
        ["claim.value"] = value,
        ["jsonType.label"] = "String",
        ["access.token.claim"] = "true",
        ["id.token.claim"] = "false",
        ["userinfo.token.claim"] = "false",
    });

    public async Task<int> DeleteUsersExceptAsync(string realm, IReadOnlySet<string> keep, CancellationToken cancellationToken)
    {
        var deleted = 0;
        while (true)
        {
            var users = await GetArrayAsync($"realms/{E(realm)}/users?briefRepresentation=true&first=0&max=500", cancellationToken).ConfigureAwait(false);
            var others = users.Where(u => u is not null && !keep.Contains((string?)u["username"] ?? string.Empty)).ToList();
            foreach (var user in others)
            {
                using var _ = await SendAsync(HttpMethod.Delete, $"realms/{E(realm)}/users/{E((string)user!["id"]!)}",
                    null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
                deleted++;
            }

            if (others.Count == 0 || users.Count < 500)
            {
                return deleted;
            }
        }
    }

    /// <summary>
    /// The UAE PASS identity provider: Keycloak's generic OAuth 2.0 broker, because UAE PASS issues
    /// an opaque access token and no ID token; the profile comes from its user information endpoint.
    /// People are matched by their UAE PASS <c>uuid</c>, which UAE PASS recommends because email and
    /// mobile can change.
    /// </summary>
    public static JsonObject UaePassRepresentation(UaePassProvider provider) => new()
    {
        ["alias"] = UaePassProvider.Alias,
        ["displayName"] = "UAE PASS",
        ["providerId"] = "oauth2",
        ["enabled"] = provider.Enabled,
        ["hideOnLogin"] = false,
        // Emails are only linked to an existing account after that account's owner proves it is
        // theirs (email link or password); see the first login flow.
        ["trustEmail"] = false,
        ["storeToken"] = false,
        ["addReadTokenRoleOnCreate"] = false,
        ["linkOnly"] = false,
        ["firstBrokerLoginFlowAlias"] = UaePassProvider.FirstLoginFlow,
        ["postBrokerLoginFlowAlias"] = UaePassProvider.AfterLoginFlow,
        ["config"] = new JsonObject
        {
            // UAE PASS expects acr_values on the authorization request; Keycloak keeps the query.
            ["authorizationUrl"] = $"{provider.Endpoints.Authorization}?acr_values={Uri.EscapeDataString(UaePassEndpoints.AcrValues)}",
            ["tokenUrl"] = provider.Endpoints.Token,
            ["userInfoUrl"] = provider.Endpoints.UserInfo,
            ["clientId"] = provider.ClientId,
            // Keycloak keeps the stored secret when it receives its own mask back.
            ["clientSecret"] = provider.ClientSecret ?? "**********",
            ["clientAuthMethod"] = "client_secret_basic",
            ["defaultScope"] = UaePassEndpoints.Scope,
            ["uiLocales"] = "true",
            ["pkceEnabled"] = "false",
            ["syncMode"] = "IMPORT",
            ["userIDClaim"] = "uuid",
            ["userNameClaim"] = "email",
            ["emailClaim"] = "email",
            ["givenNameClaim"] = "firstnameEN",
            ["familyNameClaim"] = "lastnameEN",
        },
    };

    /// <summary>Settings of the email code step for <paramref name="mode"/>.</summary>
    public static Dictionary<string, string> EmailCodeConfig(EmailCodeSignIn mode, IReadOnlyList<string> allowedDomains) => new(StringComparer.Ordinal)
    {
        ["signInPolicy"] = mode switch
        {
            EmailCodeSignIn.AllowedDomains => "allowed-domains",
            EmailCodeSignIn.Anyone => "anyone",
            _ => "known-users",
        },
        ["allowedDomains"] = string.Join("##", allowedDomains),
        ["codeTtlSeconds"] = "600",
    };

    internal async Task SetExecutionConfigAsync(string realm, JsonNode execution, string alias, Dictionary<string, string> config, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["alias"] = $"{alias}-{((string?)execution["id"])?[..8]}",
            ["config"] = JsonSerializer.SerializeToNode(config, Json),
        };
        if ((string?)execution["authenticationConfig"] is { } configId)
        {
            body["id"] = configId;
            await SendAsync(HttpMethod.Put, $"realms/{E(realm)}/authentication/config/{E(configId)}", body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendAsync(HttpMethod.Post, $"realms/{E(realm)}/authentication/executions/{E((string)execution["id"]!)}/config", body, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await response.Content.ReadFromJsonAsync<JsonNode>(Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyBrandingAsync(string realm, RealmBranding branding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branding);
        using (await SendAsync(HttpMethod.Put, $"realms/{E(realm)}", new JsonObject
        {
            ["displayName"] = branding.DisplayName,
            ["displayNameHtml"] = branding.DisplayNameHtml,
        }, cancellationToken).ConfigureAwait(false))
        {
        }

        var clients = await GetArrayAsync($"realms/{E(realm)}/clients?clientId=zelvo-web", cancellationToken).ConfigureAwait(false);
        if (clients.Count == 0 || clients[0] is not JsonObject client || (string?)client["id"] is not { } id)
        {
            return;
        }

        client["redirectUris"] = new JsonArray([.. branding.WebUrls.Select(u => (JsonNode)JsonValue.Create($"{u}/auth/callback"))]);
        client["webOrigins"] = new JsonArray([.. branding.WebUrls.Select(u => (JsonNode)JsonValue.Create(u))]);
        var attributes = client["attributes"] as JsonObject ?? [];
        attributes["post.logout.redirect.uris"] = string.Join("##", branding.WebUrls.Select(u => $"{u}/*"));
        client["attributes"] = attributes;
        using (await SendAsync(HttpMethod.Put, $"realms/{E(realm)}/clients/{E(id)}", client.DeepClone(), cancellationToken).ConfigureAwait(false))
        {
        }
    }

    /// <summary>Alias of the Entra identity provider in every realm.</summary>
    internal const string EntraAlias = "entra";

    /// <summary>Name of the LDAP user federation in every realm.</summary>
    internal const string LdapComponent = "zelvo-ldap";

    public async Task ApplyDirectorySignInAsync(string realm, DirectorySignInKind kind, DirectorySignIn? signIn, CancellationToken cancellationToken)
    {
        if (kind == DirectorySignInKind.Entra)
        {
            var path = $"realms/{E(realm)}/identity-provider/instances";
            var existing = await GetAsync($"{path}/{EntraAlias}", cancellationToken).ConfigureAwait(false);
            if (signIn is null)
            {
                if (existing is not null)
                {
                    using var _ = await SendAsync(HttpMethod.Delete, $"{path}/{EntraAlias}", null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
                }

                return;
            }

            var login = $"https://login.microsoftonline.com/{E(signIn.DirectoryId!)}";
            var config = new JsonObject
            {
                ["clientId"] = signIn.ClientId,
                ["authorizationUrl"] = $"{login}/oauth2/v2.0/authorize",
                ["tokenUrl"] = $"{login}/oauth2/v2.0/token",
                ["jwksUrl"] = $"{login}/discovery/v2.0/keys",
                ["issuer"] = $"{login}/v2.0",
                ["logoutUrl"] = $"{login}/oauth2/v2.0/logout",
                ["useJwksUrl"] = "true",
                ["validateSignature"] = "true",
                ["defaultScope"] = "openid profile email",
                ["clientAuthMethod"] = "client_secret_post",
                ["pkceEnabled"] = "true",
                ["pkceMethod"] = "S256",
                ["syncMode"] = "IMPORT",
            };
            if (signIn.Secret is { Length: > 0 } secret)
            {
                config["clientSecret"] = secret;
            }
            else if (existing is null)
            {
                throw new KeycloakAdminException($"Realm {realm} has no Microsoft sign-in yet, so the client secret is needed.");
            }

            var representation = new JsonObject
            {
                ["alias"] = EntraAlias,
                ["displayName"] = signIn.DisplayName,
                ["providerId"] = "oidc",
                ["enabled"] = true,
                // Entra's own directory vouches for its emails, so the synced account is taken over.
                ["trustEmail"] = true,
                ["firstBrokerLoginFlowAlias"] = "first broker login",
                ["config"] = config,
            };
            if (existing is null)
            {
                using var _ = await SendAsync(HttpMethod.Post, path, representation, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                representation["internalId"] = existing["internalId"]?.DeepClone();
                if (signIn.Secret is null)
                {
                    config["clientSecret"] = existing["config"]?["clientSecret"]?.DeepClone();
                }

                using var _ = await SendAsync(HttpMethod.Put, $"{path}/{EntraAlias}", representation, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var components = await GetArrayAsync($"realms/{E(realm)}/components?type=org.keycloak.storage.UserStorageProvider&name={LdapComponent}", cancellationToken).ConfigureAwait(false);
        var current = (components.Count > 0 ? components[0] : null) as JsonObject;
        if (signIn is null)
        {
            if ((string?)current?["id"] is { } id)
            {
                using var _ = await SendAsync(HttpMethod.Delete, $"realms/{E(realm)}/components/{E(id)}", null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
            }

            return;
        }

        var realmId = (string?)(await GetAsync($"realms/{E(realm)}", cancellationToken).ConfigureAwait(false))?["id"]
            ?? throw new KeycloakAdminException($"Realm {realm} does not exist.");
        JsonArray One(string value) => [value];
        var ldapConfig = new JsonObject
        {
            ["enabled"] = One("true"),
            ["vendor"] = One(signIn.ActiveDirectory ? "ad" : "other"),
            ["connectionUrl"] = One($"{(signIn.Ldaps ? "ldaps" : "ldap")}://{signIn.Host}:{signIn.Port}"),
            ["startTls"] = One(signIn.StartTls ? "true" : "false"),
            ["bindDn"] = One(signIn.BindDn!),
            ["usersDn"] = One(signIn.BaseDn!),
            ["customUserSearchFilter"] = One(signIn.UserFilter ?? string.Empty),
            ["searchScope"] = One("2"),
            ["usernameLDAPAttribute"] = One(signIn.ActiveDirectory ? "sAMAccountName" : "uid"),
            ["rdnLDAPAttribute"] = One("cn"),
            ["uuidLDAPAttribute"] = One(signIn.ActiveDirectory ? "objectGUID" : "entryUUID"),
            ["userObjectClasses"] = One(signIn.ActiveDirectory ? "person, organizationalPerson, user" : "inetOrgPerson, organizationalPerson"),
            // Passwords are checked by the directory; Keycloak never writes to it.
            ["editMode"] = One("READ_ONLY"),
            ["importEnabled"] = One("true"),
            ["syncRegistrations"] = One("false"),
            ["authType"] = One("simple"),
            ["trustEmail"] = One("true"),
            ["pagination"] = One("true"),
            ["useTruststoreSpi"] = One("always"),
        };
        if (signIn.Secret is { Length: > 0 } password)
        {
            ldapConfig["bindCredential"] = One(password);
        }
        else if (current is null)
        {
            throw new KeycloakAdminException($"Realm {realm} has no directory sign-in yet, so the bind password is needed.");
        }

        var component = new JsonObject
        {
            ["name"] = LdapComponent,
            ["providerId"] = "ldap",
            ["providerType"] = "org.keycloak.storage.UserStorageProvider",
            ["parentId"] = realmId,
            ["config"] = ldapConfig,
        };
        if (current is null)
        {
            using var _ = await SendAsync(HttpMethod.Post, $"realms/{E(realm)}/components", component, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            component["id"] = current["id"]?.DeepClone();
            if (signIn.Secret is null)
            {
                ldapConfig["bindCredential"] = current["config"]?["bindCredential"]?.DeepClone();
            }

            using var _ = await SendAsync(HttpMethod.Put, $"realms/{E(realm)}/components/{E((string)current["id"]!)}", component, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<JsonNode?>> GetArrayAsync(string path, CancellationToken cancellationToken) =>
        (await GetAsync(path, cancellationToken).ConfigureAwait(false))?.AsArray().ToList() ?? [];

    internal async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, bool allowNotFound = false, bool allowConflict = false)
    {
        using var request = new HttpRequestMessage(method, $"{_baseUrl}/admin/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken).ConfigureAwait(false));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode
            || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            || (allowConflict && response.StatusCode == HttpStatusCode.Conflict))
        {
            return response;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        throw new KeycloakAdminException($"Keycloak answered {(int)status} to {method} {path}: {Shorten(detail)}", status);
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        if (_token is null || DateTimeOffset.UtcNow >= _tokenExpires)
        {
            _token = await _signIn(cancellationToken).ConfigureAwait(false);
        }

        return _token;
    }

    private async Task<(string, int)> RequestTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(
            new Uri($"{_baseUrl}/realms/master/protocol/openid-connect/token"), content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KeycloakAdminException($"Keycloak refused the Zelvo admin sign-in ({(int)response.StatusCode}).", response.StatusCode);
        }

        var token = await response.Content.ReadFromJsonAsync<JsonNode>(Json, cancellationToken).ConfigureAwait(false);
        return ((string?)token?["access_token"] ?? throw new KeycloakAdminException("Keycloak sent no access token."),
            (int?)token?["expires_in"] ?? 60);
    }

    internal static string E(string value) => Uri.EscapeDataString(value);

    private static string Shorten(string value) => value.Length <= 300 ? value : value[..300];
}
