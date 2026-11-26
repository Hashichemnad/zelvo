using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Zelvo.Licensing;

/// <summary>
/// One component of a release in its manifest: the image as published and the hash of the
/// application files in it (task F6, module 12 tamper detection).
/// </summary>
/// <param name="Name">The component (<c>api</c>, <c>worker</c>, <c>web</c>, <c>updater</c>, <c>keycloak</c>, …).</param>
/// <param name="Image">The image name as published, without tag or digest.</param>
/// <param name="Digest">The image's registry digest (<c>sha256:…</c>), which the cosign signature covers too.</param>
/// <param name="ImageId">The image id (the config digest, <c>sha256:…</c>), which stays the same when the image is saved and loaded from a file.</param>
/// <param name="FilesSha256">The <see cref="ReleaseIntegrity.HashDirectory"/> hash of <paramref name="AppDirectory"/>, or null for images Zelvo does not check from inside.</param>
/// <param name="AppDirectory">The directory in the image the hash covers.</param>
/// <param name="Exclude">Paths under it that the component writes while running (relative, with <c>/</c>).</param>
public sealed record ReleaseComponent(
    string Name, string Image, string Digest, string? ImageId = null, string? FilesSha256 = null, string? AppDirectory = null, IReadOnlyList<string>? Exclude = null);

/// <summary>
/// The release manifest (<c>release-manifest.json</c>, format <c>zelvo-release/1</c>): what the
/// release pipeline built, signed with Zelvo's image-signing key (cosign <c>sign-blob</c>, ECDSA
/// P-256 over SHA-256) in <c>release-manifest.json.sig</c>. Installations keep both next to their
/// configuration; every service checks the signature and its own files at startup.
/// </summary>
public sealed record ReleaseManifest(string Format, string Version, DateTimeOffset CreatedAt, IReadOnlyList<ReleaseComponent> Components)
{
    /// <summary>The format this build reads.</summary>
    public const string CurrentFormat = "zelvo-release/1";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

/// <summary>The outcome of a component's check.</summary>
public enum ReleaseCheckStatus
{
    /// <summary>The manifest is ours and the component's files match it.</summary>
    Verified,

    /// <summary>No manifest is installed (development builds; refused in release builds).</summary>
    NoManifest,

    /// <summary>The manifest or its signature is missing, unreadable or not signed with Zelvo's key.</summary>
    ManifestInvalid,

    /// <summary>The manifest does not list this component.</summary>
    ComponentUnknown,

    /// <summary>The component's files differ from the release: a modified component.</summary>
    Modified,
}

/// <summary>A component's check result.</summary>
public sealed record ReleaseCheckResult(string Component, ReleaseCheckStatus Status, string? Version, string? Expected, string? Actual, string Reason)
{
    /// <summary>Whether the component may run as a release component.</summary>
    public bool IsVerified => Status == ReleaseCheckStatus.Verified;
}

/// <summary>Where the release manifest is and what this component's files are (configuration section <c>Release</c>).</summary>
public sealed class ReleaseIntegrityOptions
{
    /// <summary>The manifest file; the signature is the same path with <c>.sig</c> added.</summary>
    public string ManifestPath { get; set; } = "/etc/zelvo/release/release-manifest.json";

    /// <summary>The application directory to hash; by default the running application's.</summary>
    public string? ApplicationDirectory { get; set; }

    /// <summary>The options from configuration.</summary>
    public static ReleaseIntegrityOptions From(IConfiguration? configuration)
    {
        var options = new ReleaseIntegrityOptions();
        if (configuration?["Release:ManifestPath"] is { Length: > 0 } manifest)
        {
            options.ManifestPath = manifest;
        }

        if (configuration?["Release:ApplicationDirectory"] is { Length: > 0 } directory)
        {
            options.ApplicationDirectory = directory;
        }

        return options;
    }
}

/// <summary>
/// Zelvo's public keys for image and release signatures (<c>image-signing-keys.json</c>, embedded):
/// cosign public keys (PEM, ECDSA P-256). Only public keys are ever committed; the private key is a
/// release pipeline secret (product owner decision 2026-11-26).
/// </summary>
public sealed class ImageSigningKeys
{
    private readonly IReadOnlyList<(string Id, ECDsa Key)> _keys;

    private ImageSigningKeys(IReadOnlyList<(string Id, ECDsa Key)> keys) => _keys = keys;

    /// <summary>The key ids.</summary>
    public IReadOnlyList<string> Ids => [.. _keys.Select(k => k.Id)];

    /// <summary>The embedded keys, plus development keys from <c>Release:DevelopmentKeys</c> outside release builds.</summary>
    public static ImageSigningKeys Load(IConfiguration? configuration = null)
    {
        var entries = new List<KeyEntry>();
        using (var stream = typeof(ImageSigningKeys).Assembly.GetManifestResourceStream("Zelvo.Licensing.image-signing-keys.json"))
        {
            if (stream is not null)
            {
                entries.AddRange(JsonSerializer.Deserialize<List<KeyEntry>>(stream, ReleaseManifest.Json) ?? []);
            }
        }

#if !ZELVO_RELEASE
        // Development builds also accept keys from configuration, to try the checks; release
        // builds never read keys from configuration, so a customer cannot add one.
        if (configuration is not null)
        {
            foreach (var section in configuration.GetSection("Release:DevelopmentKeys").GetChildren())
            {
                if (section["Id"] is { Length: > 0 } id && section["PublicKey"] is { Length: > 0 } publicKey)
                {
                    entries.Add(new KeyEntry(id, publicKey));
                }
            }
        }
#endif

        return FromPem(entries.Select(e => (e.Id, e.PublicKey)));
    }

    /// <summary>Keys from <c>(id, PEM)</c> pairs; anything but an ECDSA P-256 key is refused.</summary>
    public static ImageSigningKeys FromPem(IEnumerable<(string Id, string Pem)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var loaded = new List<(string, ECDsa)>();
        foreach (var (id, pem) in keys)
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(pem);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                key.Dispose();
                throw new LicenseException($"Image signing key '{id}' is not an ECDSA P-256 public key.");
            }

            if (key.KeySize != 256)
            {
                key.Dispose();
                throw new LicenseException($"Image signing key '{id}' is not an ECDSA P-256 key.");
            }

            loaded.Add((id, key));
        }

        return new ImageSigningKeys(loaded);
    }

    /// <summary>Whether <paramref name="signature"/> (cosign's base64 DER signature) signs <paramref name="content"/> with one of the keys.</summary>
    public bool Verify(ReadOnlySpan<byte> content, string signature)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var (_, key) in _keys)
        {
            if (key.VerifyData(content, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record KeyEntry(string Id, string PublicKey);
}

/// <summary>
/// Checks a component against the signed release manifest (module 12: "a modified or unlicensed
/// component refuses to start and the event is logged"). Works offline: the manifest and its
/// signature are files of the installation, the public key is compiled in.
/// </summary>
public static class ReleaseIntegrity
{
    /// <summary>
    /// The hash of a directory's files: SHA-256 over the lines <c>"&lt;sha256&gt;  ./&lt;path&gt;\n"</c>
    /// sorted by the paths' UTF-8 bytes, the same as
    /// <c>find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum</c>, which the
    /// release pipeline runs in each image. Paths under <paramref name="exclude"/> are left out.
    /// </summary>
    public static string HashDirectory(string directory, IReadOnlyList<string>? exclude = null)
    {
        var root = Path.GetFullPath(directory);
        var skipped = (exclude ?? []).Select(e => e.Trim('/')).Where(e => e.Length > 0).ToList();
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, ReturnSpecialDirectories = false })
            .Select(f => "./" + Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => !skipped.Any(s => f.StartsWith("./" + s + "/", StringComparison.Ordinal) || f == "./" + s))
            .OrderBy(f => f, Utf8Ordinal.Instance)
            .ToList();
        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            using var stream = File.OpenRead(Path.Combine(root, file[2..]));
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            total.AppendData(Encoding.UTF8.GetBytes($"{hash}  {file}\n"));
        }

        return Convert.ToHexStringLower(total.GetHashAndReset());
    }

    /// <summary>Reads and checks the manifest's signature; null with the reason when it is not ours.</summary>
    public static ReleaseManifest? ReadManifest(string manifestPath, ImageSigningKeys keys, out ReleaseCheckStatus status, out string reason)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var signaturePath = manifestPath + ".sig";
        if (!File.Exists(manifestPath))
        {
            status = ReleaseCheckStatus.NoManifest;
            reason = $"No release manifest at {manifestPath}.";
            return null;
        }

        status = ReleaseCheckStatus.ManifestInvalid;
        if (!File.Exists(signaturePath))
        {
            reason = $"The release manifest has no signature ({signaturePath}).";
            return null;
        }

        var content = File.ReadAllBytes(manifestPath);
        if (!keys.Verify(content, File.ReadAllText(signaturePath)))
        {
            reason = "The release manifest is not signed with Zelvo's image-signing key.";
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(content, ReleaseManifest.Json);
            if (manifest is null || manifest.Format != ReleaseManifest.CurrentFormat)
            {
                reason = $"The release manifest is not in the {ReleaseManifest.CurrentFormat} format.";
                return null;
            }

            status = ReleaseCheckStatus.Verified;
            reason = "The release manifest is signed by Zelvo.";
            return manifest;
        }
        catch (JsonException)
        {
            reason = "The release manifest cannot be read.";
            return null;
        }
    }

    /// <summary>Checks <paramref name="component"/>'s files against the manifest.</summary>
    public static ReleaseCheckResult Check(string component, ReleaseIntegrityOptions options, ImageSigningKeys keys)
    {
        ArgumentNullException.ThrowIfNull(options);
        var manifest = ReadManifest(options.ManifestPath, keys, out var status, out var reason);
        if (manifest is null)
        {
            return new ReleaseCheckResult(component, status, null, null, null, reason);
        }

        var entry = manifest.Components.FirstOrDefault(c => string.Equals(c.Name, component, StringComparison.Ordinal));
        if (entry?.FilesSha256 is not { } expected)
        {
            return new ReleaseCheckResult(component, ReleaseCheckStatus.ComponentUnknown, manifest.Version, null, null,
                $"Release {manifest.Version} does not list the component '{component}'.");
        }

        var directory = options.ApplicationDirectory ?? AppContext.BaseDirectory;
        var actual = HashDirectory(directory, entry.Exclude);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? new ReleaseCheckResult(component, ReleaseCheckStatus.Verified, manifest.Version, expected, actual,
                $"The files of '{component}' are those of release {manifest.Version}.")
            : new ReleaseCheckResult(component, ReleaseCheckStatus.Modified, manifest.Version, expected, actual,
                $"The files of '{component}' differ from release {manifest.Version}: the component was modified.");
    }

    /// <summary>The version of the running build (informational version without the commit).</summary>
    public static string BuildVersion =>
        typeof(ReleaseIntegrity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private sealed class Utf8Ordinal : IComparer<string>
    {
        public static readonly Utf8Ordinal Instance = new();

        public int Compare(string? x, string? y) =>
            Encoding.UTF8.GetBytes(x ?? string.Empty).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? string.Empty));
    }
}
