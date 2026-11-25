using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zelvo.Licensing;

/// <summary>
/// A file we sign (license, upgrade license, update package manifest): the payload's exact bytes,
/// the id of the vendor key and an RSA-PSS (SHA-256) signature over the file type and the payload.
/// Any change to the payload or the type breaks the signature; JSON is never re-serialized before
/// checking, so no canonical form is needed.
/// </summary>
/// <param name="Format">Always <see cref="CurrentFormat"/>.</param>
/// <param name="Type">What the payload is (<see cref="SignedFileTypes"/>).</param>
/// <param name="KeyId">The vendor key that signed it.</param>
/// <param name="Algorithm">Always <see cref="Rsa"/>.</param>
/// <param name="Payload">The payload's UTF-8 JSON, base64.</param>
/// <param name="Signature">The signature, base64.</param>
internal sealed record SignedFile(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature)
{
    public const string CurrentFormat = "zelvo-signed/1";
    public const string Rsa = "RSA-PSS-SHA256";

    /// <summary>Largest signed file read (licenses and manifests are a few kilobytes).</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The bytes the signature covers: format, type and payload, so a signature of one file type never fits another.</summary>
    public static byte[] SigningInput(string type, ReadOnlySpan<byte> payload)
    {
        var header = Encoding.UTF8.GetBytes($"{CurrentFormat}\n{type}\n");
        var input = new byte[header.Length + payload.Length];
        header.CopyTo(input, 0);
        payload.CopyTo(input.AsSpan(header.Length));
        return input;
    }

    /// <summary>Signs a payload with a private key (the signing tool and tests only).</summary>
    public static SignedFile Create(string type, ReadOnlySpan<byte> payload, string keyId, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var signature = privateKey.SignData(SigningInput(type, payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new SignedFile(CurrentFormat, type, keyId, Rsa, Convert.ToBase64String(payload), Convert.ToBase64String(signature));
    }

    /// <summary>Reads a signed file; null when it is not one (no exception for bad input).</summary>
    public static SignedFile? Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > MaxBytes)
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<SignedFile>(content);
            return file is { Format: CurrentFormat, Algorithm: Rsa } && !string.IsNullOrEmpty(file.Type) && !string.IsNullOrEmpty(file.KeyId)
                && file.PayloadBytes() is not null && file.SignatureBytes() is not null ? file : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The payload's bytes, or null when they are not base64.</summary>
    public byte[]? PayloadBytes() => FromBase64(Payload);

    /// <summary>The signature's bytes, or null when they are not base64.</summary>
    public byte[]? SignatureBytes() => FromBase64(Signature);

    /// <summary>Checks the signature with the trusted key of <see cref="KeyId"/>.</summary>
    public SignatureCheck Verify(VendorKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.Contains(KeyId))
        {
            return SignatureCheck.UnknownKey;
        }

        var payload = PayloadBytes();
        var signature = SignatureBytes();
        return payload is not null && signature is not null && keys.Verify(KeyId, SigningInput(Type, payload), signature)
            ? SignatureCheck.Valid
            : SignatureCheck.Invalid;
    }

    /// <summary>The file as written to disk.</summary>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Indented);

    private static byte[]? FromBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Result of checking a signature.</summary>
internal enum SignatureCheck
{
    Valid,
    Invalid,
    UnknownKey,
}

/// <summary>Types of signed files.</summary>
internal static class SignedFileTypes
{
    public const string License = "license";
    public const string Upgrade = "upgrade";
    public const string PackageManifest = "package-manifest";
}
