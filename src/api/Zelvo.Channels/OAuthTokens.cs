using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Text;
using Zelvo.Data.Channels;
using Zelvo.Integrations;

namespace Zelvo.Channels;

/// <summary>
/// The HTTP client for Microsoft and Google: connections only to addresses the mail network policy
/// allows (checked after DNS), HTTPS only, no redirects followed, a timeout per request.
/// </summary>
public sealed class ChannelHttp : IDisposable
{
    /// <summary>Creates the client.</summary>
    public ChannelHttp(ChannelOptions options, OutboundAddressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, cancellationToken) => policy.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken),
        };
        Client = new HttpClient(handler) { Timeout = options.Timeout };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Zelvo/1.0");
    }

    /// <summary>Tests only: sends through <paramref name="handler"/> instead of the network.</summary>
    internal ChannelHttp(ChannelOptions options, OutboundAddressPolicy policy, HttpMessageHandler handler)
    {
        Policy = policy;
        Options = options;
        Client = new HttpClient(handler) { Timeout = options.Timeout };
    }

    /// <summary>The client.</summary>
    public HttpClient Client { get; }

    /// <summary>The policy mail connections follow (also for IMAP and SMTP).</summary>
    public OutboundAddressPolicy Policy { get; }

    /// <summary>The installation's settings.</summary>
    public ChannelOptions Options { get; }

    /// <summary>Sends <paramref name="request"/>; network failures and refusals become <see cref="MailboxException"/>.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { } uri || uri.Scheme != Uri.UriSchemeHttps && !Options.Network.AllowInsecureHttp)
        {
            throw new MailboxException("url-not-https", "Mail providers are reached over HTTPS only.");
        }

        try
        {
            return await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is OutboundRequestBlockedException blocked)
        {
            throw new MailboxException(blocked.Code, blocked.Message, ex);
        }
        catch (OutboundRequestBlockedException blocked)
        {
            throw new MailboxException(blocked.Code, blocked.Message, blocked);
        }
        catch (HttpRequestException ex)
        {
            throw new MailboxException("unreachable", $"{uri.Host} could not be reached: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MailboxException("timeout", $"{uri.Host} did not answer in time.", ex);
        }
    }

    /// <summary>Reads a JSON answer, or throws with the provider's error.</summary>
    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ProviderError(response.StatusCode, what, body);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json) ?? throw new MailboxException("bad-answer", $"{what}: the answer was empty.");
        }
        catch (JsonException ex)
        {
            throw new MailboxException("bad-answer", $"{what}: the answer was not understood.", ex);
        }
    }

    /// <summary>The error for a failed provider call, with the provider's own message (cut short).</summary>
    public static MailboxException ProviderError(HttpStatusCode status, string what, string body)
    {
        var detail = ErrorText(body);
        var code = status switch
        {
            HttpStatusCode.Unauthorized => "sign-in-failed",
            HttpStatusCode.Forbidden => "access-denied",
            HttpStatusCode.NotFound => "mailbox-not-found",
            HttpStatusCode.TooManyRequests => "rate-limited",
            HttpStatusCode.BadRequest when detail.Contains("invalid_grant", StringComparison.Ordinal) => "sign-in-expired",
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized when detail.Contains("invalid_client", StringComparison.Ordinal) => "sign-in-failed",
            _ => "provider-error",
        };
        return new MailboxException(code, $"{what}: {(int)status} {detail}");
    }

    /// <summary>JSON options of provider answers.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public void Dispose() => Client.Dispose();

    private static string ErrorText(string body)
    {
        var text = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    text = error.ValueKind == JsonValueKind.Object
                        ? $"{(error.TryGetProperty("code", out var c) ? c.ToString() : null)} {(error.TryGetProperty("message", out var m) ? m.GetString() : null)}"
                        : $"{error.GetString()} {(root.TryGetProperty("error_description", out var d) ? d.GetString() : null)}";
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: the text as it is.
        }

        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 300 ? text : text[..300];
    }
}

/// <summary>An access token and when it expires.</summary>
/// <param name="Value">The token.</param>
/// <param name="ExpiresAt">Expiry.</param>
/// <param name="RefreshToken">A new refresh token, when the provider rotated it (OAuth sign-in).</param>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt, string? RefreshToken = null);

/// <summary>
/// Access tokens for Microsoft Graph and the Gmail API: app-only tokens (Microsoft client credentials,
/// Google service accounts with domain-wide delegation) and delegated tokens from an administrator's
/// sign-in (refresh tokens). Tokens are cached in memory per mailbox until a minute before expiry.
/// </summary>
public sealed class OAuthTokens(ChannelHttp http, TimeProvider time)
{
    /// <summary>Scopes Zelvo asks Microsoft for when an administrator signs in.</summary>
    public const string MicrosoftDelegatedScopes = "openid email offline_access https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Mail.Send";

    /// <summary>Scope Zelvo asks Google for (read, mark read and send).</summary>
    public const string GmailScope = "https://www.googleapis.com/auth/gmail.modify";

    /// <summary>Scopes Zelvo asks Google for when an administrator signs in.</summary>
    public const string GoogleDelegatedScopes = $"openid email {GmailScope}";

    private readonly ConcurrentDictionary<string, AccessToken> _cache = new(StringComparer.Ordinal);

    /// <summary>A token for <paramref name="mailbox"/>. A rotated refresh token is returned in <see cref="AccessToken.RefreshToken"/> for the caller to store.</summary>
    public async Task<AccessToken> GetAsync(Mailbox mailbox, MailboxCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(credential);
        var key = $"{mailbox.TenantId:N}:{mailbox.Id:N}:{mailbox.CredentialSetAt?.UtcTicks}";
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > time.GetUtcNow().AddMinutes(1))
        {
            return cached with { RefreshToken = null };
        }

        var token = (mailbox.Provider, mailbox.Auth) switch
        {
            (MailboxProvider.Microsoft365, MailboxAuth.ClientSecret) => await MicrosoftAppAsync(mailbox, credential.Secret, cancellationToken).ConfigureAwait(false),
            (MailboxProvider.Gmail, MailboxAuth.ServiceAccount) => await GoogleServiceAccountAsync(mailbox, credential.Secret, cancellationToken).ConfigureAwait(false),
            (MailboxProvider.Microsoft365 or MailboxProvider.Gmail, MailboxAuth.OAuthSignIn) => await RefreshAsync(mailbox, credential, cancellationToken).ConfigureAwait(false),
            _ => throw new MailboxException("auth-not-supported", "This way of signing in does not fit the mailbox's provider."),
        };
        _cache[key] = token with { RefreshToken = null };
        return token;
    }

    /// <summary>Forgets cached tokens of a mailbox (after its credential changed).</summary>
    public void Forget(Guid mailboxId)
    {
        foreach (var key in _cache.Keys.Where(k => k.Contains($":{mailboxId:N}:", StringComparison.Ordinal)))
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>The sign-in page an administrator is sent to (authorization code flow with PKCE).</summary>
    public Uri AuthorizeUrl(Mailbox mailbox, Uri redirectUri, string state, string codeChallenge)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(redirectUri);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = mailbox.ClientId ?? string.Empty,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["login_hint"] = mailbox.Address,
        };
        Uri endpoint;
        if (mailbox.Provider == MailboxProvider.Microsoft365)
        {
            endpoint = new Uri(http.Options.MicrosoftLoginUrl, $"{Uri.EscapeDataString(Directory(mailbox))}/oauth2/v2.0/authorize");
            query["scope"] = MicrosoftDelegatedScopes;
            query["response_mode"] = "query";
            query["prompt"] = "select_account";
        }
        else
        {
            endpoint = http.Options.GoogleAuthUrl;
            query["scope"] = GoogleDelegatedScopes;
            query["access_type"] = "offline";
            query["prompt"] = "consent";
        }

        return new Uri($"{endpoint.AbsoluteUri}?{string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"))}");
    }

    /// <summary>Exchanges the code of a finished sign-in; returns the refresh token and the account that signed in.</summary>
    public async Task<(string RefreshToken, string? Email)> RedeemAsync(
        Mailbox mailbox, string clientSecret, string code, string verifier, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(redirectUri);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = mailbox.ClientId ?? string.Empty,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
        };
        if (mailbox.Provider == MailboxProvider.Microsoft365)
        {
            form["scope"] = MicrosoftDelegatedScopes;
        }

        var answer = await TokenAsync(TokenUrl(mailbox), form, "Signing in", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(answer.RefreshToken))
        {
            throw new MailboxException("no-refresh-token", "The provider gave no lasting access. Allow offline access for the app and sign in again.");
        }

        return (answer.RefreshToken, EmailOf(answer.IdToken));
    }

    /// <summary>A random PKCE verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private async Task<AccessToken> MicrosoftAppAsync(Mailbox mailbox, string secret, CancellationToken cancellationToken)
    {
        var answer = await TokenAsync(TokenUrl(mailbox), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = mailbox.ClientId ?? string.Empty,
            ["client_secret"] = secret,
            ["scope"] = "https://graph.microsoft.com/.default",
        }, "Signing in to Microsoft 365", cancellationToken).ConfigureAwait(false);
        return answer.ToToken(time);
    }

    private async Task<AccessToken> RefreshAsync(Mailbox mailbox, MailboxCredential credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(credential.RefreshToken))
        {
            throw new MailboxException("sign-in-needed", "Sign in to the mailbox in Settings → Mailboxes to give Zelvo access.");
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = mailbox.ClientId ?? string.Empty,
            ["client_secret"] = credential.Secret,
            ["refresh_token"] = credential.RefreshToken,
        };
        if (mailbox.Provider == MailboxProvider.Microsoft365)
        {
            form["scope"] = MicrosoftDelegatedScopes;
        }

        var answer = await TokenAsync(TokenUrl(mailbox), form, "Renewing access", cancellationToken).ConfigureAwait(false);
        return answer.ToToken(time) with { RefreshToken = answer.RefreshToken is { Length: > 0 } r && r != credential.RefreshToken ? r : null };
    }

    private async Task<AccessToken> GoogleServiceAccountAsync(Mailbox mailbox, string keyJson, CancellationToken cancellationToken)
    {
        var key = ServiceAccountKey.Parse(keyJson);
        var now = time.GetUtcNow();
        var header = Base64Url.EncodeToString("""{"alg":"RS256","typ":"JWT"}"""u8);
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iss"] = key.ClientEmail,
            ["scope"] = GmailScope,
            // The configured token address, never one from the key file.
            ["aud"] = http.Options.GoogleTokenUrl.AbsoluteUri,
            ["sub"] = mailbox.Address,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(55).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(key.PrivateKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new MailboxException("credential-invalid", "The service account key has no readable private key.", ex);
        }

        var signature = Base64Url.EncodeToString(rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var answer = await TokenAsync(http.Options.GoogleTokenUrl, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = $"{header}.{claims}.{signature}",
        }, "Signing in to Google", cancellationToken).ConfigureAwait(false);
        return answer.ToToken(time);
    }

    private async Task<TokenAnswer> TokenAsync(Uri url, Dictionary<string, string> form, string what, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var answer = await ChannelHttp.ReadJsonAsync<TokenAnswer>(response, what, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(answer.AccessToken) ? throw new MailboxException("sign-in-failed", $"{what}: no access token was given.") : answer;
    }

    private Uri TokenUrl(Mailbox mailbox) => mailbox.Provider == MailboxProvider.Microsoft365
        ? new Uri(http.Options.MicrosoftLoginUrl, $"{Uri.EscapeDataString(Directory(mailbox))}/oauth2/v2.0/token")
        : http.Options.GoogleTokenUrl;

    private static string Directory(Mailbox mailbox) => mailbox.DirectoryId is { Length: > 0 } directory ? directory : "organizations";

    /// <summary>The email claim of an ID token received straight from the provider's token endpoint over TLS (OpenID Connect 3.1.3.7 allows skipping the signature check then).</summary>
    private static string? EmailOf(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = document.RootElement;
            foreach (var claim in new[] { "email", "preferred_username", "upn" })
            {
                if (root.TryGetProperty(claim, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } email && email.Contains('@', StringComparison.Ordinal))
                {
                    return email.Trim().ToLowerInvariant();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    private sealed record TokenAnswer(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken)
    {
        public AccessToken ToToken(TimeProvider time) => new(AccessToken!, time.GetUtcNow().AddSeconds(Math.Clamp(ExpiresIn ?? 3600, 60, 86_400)));
    }
}

/// <summary>The fields Zelvo uses from a Google service account key file.</summary>
/// <param name="ClientEmail">The service account.</param>
/// <param name="PrivateKey">Its private key (PEM).</param>
public sealed record ServiceAccountKey(string ClientEmail, string PrivateKey)
{
    /// <summary>Reads a key file; throws <see cref="MailboxException"/> when it is not one.</summary>
    public static ServiceAccountKey Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("client_email", out var email) && email.GetString() is { Length: > 0 } clientEmail
                && root.TryGetProperty("private_key", out var key) && key.GetString() is { Length: > 0 } privateKey)
            {
                return new(clientEmail, privateKey);
            }
        }
        catch (JsonException)
        {
            // Below.
        }

        throw new MailboxException("credential-invalid", "Paste the service account's JSON key file (with client_email and private_key).");
    }
}
