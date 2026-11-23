using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zelvo.Integrations;

/// <summary>
/// Signs webhook payloads in the Standard Webhooks format (standardwebhooks.com), which receivers
/// can check with its libraries:
/// <list type="bullet">
/// <item>headers <c>webhook-id</c>, <c>webhook-timestamp</c> (Unix seconds) and
/// <c>webhook-signature</c>;</item>
/// <item>signature <c>v1,</c> + base64 of HMAC-SHA256 over <c>{id}.{timestamp}.{body}</c>, keyed with
/// the bytes of the secret (<c>whsec_</c> + base64);</item>
/// <item>during a secret rotation, one signature per secret, separated by spaces.</item>
/// </list>
/// </summary>
public static class WebhookSigner
{
    /// <summary>Prefix of signing secrets.</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>A new random secret (32 bytes).</summary>
    public static string NewSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The <c>webhook-signature</c> header value for <paramref name="body"/>, signed with each secret.</summary>
    public static string Sign(string messageId, DateTimeOffset timestamp, string body, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var content = Encoding.UTF8.GetBytes(
            $"{messageId}.{timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}.{body}");
        return string.Join(' ', secrets.Select(secret => "v1," + Convert.ToBase64String(HMACSHA256.HashData(KeyOf(secret), content))));
    }

    /// <summary>Checks a signature header as a receiver would (used by tests and the documentation example).</summary>
    public static bool Verify(string messageId, DateTimeOffset timestamp, string body, string signatureHeader, string secret)
    {
        ArgumentNullException.ThrowIfNull(signatureHeader);
        var expected = Encoding.ASCII.GetBytes(Sign(messageId, timestamp, body, [secret]));
        return signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(candidate => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), expected));
    }

    private static byte[] KeyOf(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return secret.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? Convert.FromBase64String(secret[SecretPrefix.Length..])
            : throw new ArgumentException("Webhook secrets start with whsec_.", nameof(secret));
    }
}
