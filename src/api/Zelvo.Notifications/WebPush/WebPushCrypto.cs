using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zelvo.Notifications.WebPush;

/// <summary>
/// Zelvo's own implementation of the web push protocols (module 19, task J2; product owner
/// decision 2026-11-26), with .NET's own cryptography and no other library:
/// <list type="bullet">
/// <item><b>RFC 8291</b> message encryption: an ephemeral P-256 key per message, ECDH with the
/// browser's key, HKDF-SHA-256 with the browser's auth secret, AES-128-GCM in the
/// <c>aes128gcm</c> content coding of <b>RFC 8188</b> (one record).</item>
/// <item><b>RFC 8292</b> VAPID: a JSON Web Token signed with ES256 by the tenant's key, naming
/// the push service's origin, valid for 12 hours, and the public key in the
/// <c>Authorization: vapid t=…, k=…</c> header.</item>
/// </list>
/// </summary>
public static class WebPushCrypto
{
    /// <summary>Record size written in the header (RFC 8188); one record holds the whole message.</summary>
    public const int RecordSize = 4096;

    /// <summary>Largest payload one record can carry (RFC 8291 section 4: 4096 minus header room, tag and delimiter).</summary>
    public const int MaxPayloadLength = 3993;

    private static readonly byte[] KeyInfoLabel = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0\u0001"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0\u0001"u8.ToArray();

    /// <summary>A new P-256 key pair: the public key as an uncompressed point and the private key as PKCS #8, both base64url.</summary>
    public static (string PublicKey, string PrivateKeyPkcs8) CreateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Base64Url.EncodeToString(PublicPoint(key.ExportParameters(false))), Base64Url.EncodeToString(key.ExportPkcs8PrivateKey()));
    }

    /// <summary>Whether <paramref name="p256dh"/> and <paramref name="auth"/> are a browser's subscription keys (a P-256 point and a 16-byte secret).</summary>
    public static bool AreValidSubscriptionKeys(string? p256dh, string? auth)
    {
        try
        {
            if (p256dh is null || auth is null || Base64Url.DecodeFromChars(auth).Length != 16)
            {
                return false;
            }

            using var key = ImportPublic(Base64Url.DecodeFromChars(p256dh));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Encrypts <paramref name="payload"/> for the browser with public key <paramref name="p256dh"/> and secret <paramref name="auth"/> (both base64url).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> payload, string p256dh, string auth)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(payload, Base64Url.DecodeFromChars(p256dh), Base64Url.DecodeFromChars(auth), ephemeral, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Encryption with a given ephemeral key and salt (tests use RFC 8291's example).</summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> payload, byte[] uaPublic, byte[] authSecret, ECDiffieHellman ephemeral, byte[] salt)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException($"A web push payload holds at most {MaxPayloadLength} bytes.", nameof(payload));
        }

        if (authSecret.Length != 16 || salt.Length != 16)
        {
            throw new ArgumentException("The auth secret and the salt are 16 bytes.");
        }

        using var browser = ImportPublicDh(uaPublic);
        var asPublic = PublicPoint(ephemeral.ExportParameters(false));
        var ecdhSecret = ephemeral.DeriveRawSecretAgreement(browser.PublicKey);

        // RFC 8291 section 3.4: IKM from the ECDH secret and the auth secret.
        var prkKey = HMACSHA256.HashData(authSecret, ecdhSecret);
        var keyInfo = new byte[KeyInfoLabel.Length + uaPublic.Length + asPublic.Length + 1];
        KeyInfoLabel.CopyTo(keyInfo, 0);
        uaPublic.CopyTo(keyInfo, KeyInfoLabel.Length);
        asPublic.CopyTo(keyInfo, KeyInfoLabel.Length + uaPublic.Length);
        keyInfo[^1] = 1;
        var ikm = HMACSHA256.HashData(prkKey, keyInfo);

        // RFC 8188 section 2.2 and 2.3: content encryption key and nonce from the salt.
        var prk = HMACSHA256.HashData(salt, ikm);
        var cek = HMACSHA256.HashData(prk, CekInfo)[..16];
        var nonce = HMACSHA256.HashData(prk, NonceInfo)[..12];

        // One record: the payload and the last-record delimiter 0x02, no padding.
        var plaintext = new byte[payload.Length + 1];
        payload.CopyTo(plaintext);
        plaintext[^1] = 2;
        var body = new byte[16 + 4 + 1 + asPublic.Length + plaintext.Length + 16];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), RecordSize);
        body[20] = (byte)asPublic.Length;
        asPublic.CopyTo(body, 21);
        var offset = 21 + asPublic.Length;
        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, plaintext, body.AsSpan(offset, plaintext.Length), body.AsSpan(offset + plaintext.Length, 16));
        CryptographicOperations.ZeroMemory(ecdhSecret);
        CryptographicOperations.ZeroMemory(cek);
        return body;
    }

    /// <summary>
    /// The <c>Authorization</c> header value for a request to <paramref name="endpoint"/> (RFC 8292):
    /// <c>vapid t=&lt;JWT&gt;, k=&lt;public key&gt;</c>.
    /// </summary>
    public static string VapidAuthorization(Uri endpoint, string subject, string publicKey, string privateKeyPkcs8, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = (now + TimeSpan.FromHours(12)).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        var signingInput = $"{header}.{claims}";
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Base64Url.DecodeFromChars(privateKeyPkcs8), out _);
        // JWS ES256 uses the raw r||s form (IEEE P1363), .NET's default for ECDsa.SignData.
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"vapid t={signingInput}.{Base64Url.EncodeToString(signature)}, k={publicKey}";
    }

    private static byte[] PublicPoint(ECParameters parameters)
    {
        var point = new byte[65];
        point[0] = 4;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);
        return point;
    }

    private static ECParameters PublicParameters(byte[] point)
    {
        if (point.Length != 65 || point[0] != 4)
        {
            throw new ArgumentException("A P-256 public key is a 65-byte uncompressed point.");
        }

        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } };
    }

    private static ECDsa ImportPublic(byte[] point)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportParameters(PublicParameters(point));
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static ECDiffieHellman ImportPublicDh(byte[] point)
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportParameters(PublicParameters(point));
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
