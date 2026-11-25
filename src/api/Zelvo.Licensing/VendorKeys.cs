using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Zelvo.Licensing;

/// <summary>A vendor public key: its id and the RSA public key (SubjectPublicKeyInfo, base64).</summary>
/// <param name="Id">Key id, named in every signed file.</param>
/// <param name="PublicKey">The public key, DER SubjectPublicKeyInfo in base64.</param>
internal sealed record VendorKey(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("publicKey")] string PublicKey);

/// <summary>
/// The public keys whose signatures Zelvo accepts: ours, embedded from <c>vendor-keys.json</c>.
/// Development builds also accept keys from configuration (<c>Licensing:DevelopmentKeys</c>), so
/// developers can try activation and update packages with a key of their own; release builds
/// (<c>ZELVO_RELEASE</c>) never read keys from configuration, so a customer cannot add one.
/// </summary>
internal sealed class VendorKeys
{
    /// <summary>Smallest RSA key accepted.</summary>
    public const int MinimumKeySize = 3072;

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public VendorKeys(IEnumerable<VendorKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key.Id) || string.IsNullOrWhiteSpace(key.PublicKey))
            {
                continue;
            }

            var der = Convert.FromBase64String(key.PublicKey);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(der, out _);
            if (rsa.KeySize < MinimumKeySize)
            {
                throw new InvalidOperationException($"Vendor key '{key.Id}' is shorter than {MinimumKeySize} bits.");
            }

            _keys[key.Id] = der;
        }
    }

    /// <summary>Ids of the trusted keys.</summary>
    public IReadOnlyCollection<string> Ids => _keys.Keys;

    public bool Contains(string keyId) => _keys.ContainsKey(keyId);

    /// <summary>RSA-PSS SHA-256 verification with the key <paramref name="keyId"/>.</summary>
    public bool Verify(string keyId, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (!_keys.TryGetValue(keyId, out var der))
        {
            return false;
        }

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(der, out _);
        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    /// <summary>The keys this build trusts.</summary>
    public static VendorKeys Load(IConfiguration? configuration)
    {
        var keys = new List<VendorKey>(Embedded());
#if !ZELVO_RELEASE
        if (configuration is not null)
        {
            foreach (var section in configuration.GetSection("Licensing:DevelopmentKeys").GetChildren())
            {
                if (section["Id"] is { Length: > 0 } id && section["PublicKey"] is { Length: > 0 } publicKey)
                {
                    keys.Add(new VendorKey(id, publicKey));
                }
            }
        }
#endif
        return new VendorKeys(keys);
    }

    /// <summary>Keys embedded in this build.</summary>
    public static IReadOnlyList<VendorKey> Embedded()
    {
        using var stream = typeof(VendorKeys).Assembly.GetManifestResourceStream("Zelvo.Licensing.vendor-keys.json")
            ?? throw new InvalidOperationException("vendor-keys.json is not embedded.");
        return JsonSerializer.Deserialize<List<VendorKey>>(stream) ?? [];
    }

    /// <summary>Whether this is a release licensing build.</summary>
    public static bool IsReleaseBuild =>
#if ZELVO_RELEASE
        true;
#else
        false;
#endif

    /// <summary>The customer watermark compiled into this build (per-customer release builds), or null.</summary>
    public static string? BuildWatermark { get; } = typeof(VendorKeys).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ZelvoBuildWatermark")?.Value is { Length: > 0 } value ? value : null;
}
