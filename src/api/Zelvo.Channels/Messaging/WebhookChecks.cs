using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Zelvo.Channels.Messaging;

/// <summary>Checks that incoming webhooks really come from the provider (task E3).</summary>
public static class WebhookSignatures
{
    /// <summary>
    /// Meta: <c>X-Hub-Signature-256</c> is <c>sha256=</c> and the hex HMAC-SHA256 of the raw body
    /// with the app's secret. Compared in constant time.
    /// </summary>
    public static bool WhatsAppValid(byte[] body, string? header, string appSecret)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(appSecret);
        if (header is null || !header.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(header.AsSpan(7)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Twilio: <c>X-Twilio-Signature</c> is the Base64 HMAC-SHA1, with the auth token, of the
    /// address Twilio was given followed by every form field's name and value, sorted by name.
    /// </summary>
    public static bool TwilioValid(Uri url, IEnumerable<KeyValuePair<string, string>> form, string? header, string authToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(authToken);
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var expected = TwilioSign(url, form, authToken);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(header));
    }

    /// <summary>Twilio's signature of <paramref name="url"/> and <paramref name="form"/> (tests and the development receiver).</summary>
    public static string TwilioSign(Uri url, IEnumerable<KeyValuePair<string, string>> form, string authToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(form);
        var data = new StringBuilder(url.AbsoluteUri);
        foreach (var (key, value) in form.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            data.Append(key).Append(value);
        }

        // Twilio signs with HMAC-SHA1; Zelvo has no choice of algorithm here.
#pragma warning disable CA5350
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(data.ToString())));
#pragma warning restore CA5350
    }

    /// <summary>Equal strings, compared in constant time (tokens in addresses).</summary>
    public static bool SameSecret(string? given, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return given is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}

/// <summary>The keys Bot Framework signs Teams' requests with.</summary>
public interface ITeamsSigningKeys
{
    /// <summary>The current signing keys.</summary>
    Task<ICollection<SecurityKey>> KeysAsync(CancellationToken cancellationToken);
}

/// <summary>Bot Framework's published keys, read from its OpenID metadata and cached (refreshed daily, or at once for an unknown key).</summary>
public sealed class BotFrameworkSigningKeys(ChannelHttp http) : ITeamsSigningKeys
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configuration = new(
        http.Options.BotFrameworkOpenIdUrl.AbsoluteUri, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http.Client) { RequireHttps = true });

    /// <inheritdoc />
    public async Task<ICollection<SecurityKey>> KeysAsync(CancellationToken cancellationToken) =>
        (await _configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false)).SigningKeys;
}

/// <summary>
/// Checks the <c>Authorization</c> of a request from Microsoft Teams: a JSON Web Token that Bot
/// Framework signed (issuer <c>https://api.botframework.com</c>) for the Teams app's id, still valid,
/// whose <c>serviceurl</c> claim is the activity's service address. Only then does Zelvo trust the
/// person's Entra identity in the activity (product owner decision 2026-11-25).
/// </summary>
public sealed class TeamsTokenValidator(ITeamsSigningKeys keys, ChannelOptions options)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Whether <paramref name="authorization"/> is a valid token for <paramref name="appId"/> and <paramref name="serviceUrl"/>.</summary>
    public async Task<bool> ValidAsync(string? authorization, string appId, string? serviceUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appId);
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || string.IsNullOrEmpty(serviceUrl))
        {
            return false;
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = options.BotFrameworkIssuer,
            ValidAudience = appId,
            IssuerSigningKeys = await keys.KeysAsync(cancellationToken).ConfigureAwait(false),
            ValidateLifetime = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
        };
        var result = await _handler.ValidateTokenAsync(authorization["Bearer ".Length..], parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return false;
        }

        var claim = result.ClaimsIdentity.FindFirst("serviceurl")?.Value ?? result.ClaimsIdentity.FindFirst(ClaimTypes.Uri)?.Value;
        return claim is not null && string.Equals(claim.TrimEnd('/'), serviceUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
