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
}

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
