using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zelvo.Licensing;

/// <summary>What kind of release a package is (module 13).</summary>
public enum UpdatePackageType
{
    /// <summary>Security and bug fix (1.4.0 → 1.4.1): free, covered by the existing license.</summary>
    Security,

    /// <summary>Feature release (1.4 → 1.5, 1 → 2): needs an upgrade license.</summary>
    Feature,
}

/// <summary>A Docker image in a package.</summary>
/// <param name="Reference">Image reference, for example <c>zelvo/api:1.5.0</c>.</param>
/// <param name="File">The image's <c>docker save</c> archive inside the package.</param>
public sealed record UpdatePackageImage(
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("file")] string File);

/// <summary>A file in a package with its SHA-256.</summary>
public sealed record UpdatePackageFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

/// <summary>Release notes in English and Arabic.</summary>
public sealed record UpdateReleaseNotes(
    [property: JsonPropertyName("en")] string? En,
    [property: JsonPropertyName("ar")] string? Ar);

/// <summary>
/// The manifest of an update package (<c>zelvo-1.5.0.zpkg</c>, module 13): version, type, the
/// oldest installed version it upgrades, bilingual release notes, the Docker images and every
/// other file with its SHA-256. The package's migrations and translations are inside its images;
/// further files (for example malware definitions) are listed here too. We sign the manifest; the
/// hashes tie every file to that signature.
/// </summary>
public sealed record UpdatePackageManifest
{
    public const string CurrentFormat = "zelvo-package/1";

    [JsonPropertyName("format")] public string Format { get; init; } = CurrentFormat;

    [JsonPropertyName("product")] public string Product { get; init; } = "zelvo";

    /// <summary>The version installed by the package (major.minor.patch).</summary>
    [JsonPropertyName("version")] public required string Version { get; init; }

    [JsonPropertyName("type")] public required UpdatePackageType Type { get; init; }

    /// <summary>The oldest installed version the package can upgrade.</summary>
    [JsonPropertyName("requiresVersion")] public string? RequiresVersion { get; init; }

    [JsonPropertyName("releaseNotes")] public required UpdateReleaseNotes ReleaseNotes { get; init; }

    /// <summary>How long the install takes, in minutes, for the users' notice.</summary>
    [JsonPropertyName("expectedMinutes")] public int ExpectedMinutes { get; init; } = 30;

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("images")] public IReadOnlyList<UpdatePackageImage> Images { get; init; } = [];

    [JsonPropertyName("files")] public IReadOnlyList<UpdatePackageFile> Files { get; init; } = [];

    [JsonIgnore] public Version? ParsedVersion => System.Version.TryParse(Version, out var v) ? Normalize(v) : null;

    [JsonIgnore] public Version? ParsedRequiresVersion => System.Version.TryParse(RequiresVersion, out var v) ? Normalize(v) : null;

    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Major.minor.patch, missing parts as 0.</summary>
    public static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }
}

/// <summary>Why a package was refused (<c>package-…</c> problem codes).</summary>
public static class UpdatePackageProblems
{
    public const string FormatInvalid = "package-format-invalid";
    public const string SignatureInvalid = "package-signature-invalid";
    public const string UnknownKey = "package-unknown-key";
    public const string FileMismatch = "package-file-mismatch";
    public const string NotNewer = "package-not-newer";
    public const string TypeMismatch = "package-type-mismatch";
    public const string UpgradeLicenseRequired = "package-upgrade-license-required";
    public const string InstalledTooOld = "package-installed-too-old";
    public const string ImagesMissing = "package-images-missing";
    public const string NotLicensed = "package-not-licensed";
}

/// <summary>The outcome of checking a package.</summary>
/// <param name="Manifest">The manifest, when it could be read and its signature is ours.</param>
/// <param name="Sha256">The SHA-256 of the whole package file.</param>
/// <param name="Problem">Why it was refused; null when every check passed.</param>
/// <param name="Reason">The same in words.</param>
/// <param name="Checks">Each check by name with whether it passed (signature, files, version, type, license, images).</param>
public sealed record UpdatePackageCheck(UpdatePackageManifest? Manifest, string Sha256, string? Problem, string? Reason, IReadOnlyDictionary<string, bool> Checks)
{
    public bool Passed => Problem is null;
}

/// <summary>
/// Reads and checks an update package (module 13) before it is imported and again before it is
/// installed: our signature on the manifest (through <see cref="ILicenseService"/>), every file
/// against its hash with nothing unlisted, a version newer than the installed one, a type that fits
/// the version change, for a feature release a matching upgrade license, and the images the
/// installation runs.
/// </summary>
public static class UpdatePackageReader
{
    /// <summary>The signed manifest's name inside the package.</summary>
    public const string ManifestEntry = "manifest.zelvo";

    public static async Task<UpdatePackageCheck> CheckAsync(
        string path, ILicenseService license, Version installed, IReadOnlyCollection<string> requiredImages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(requiredImages);
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var sha = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        UpdatePackageCheck Fail(string problem, string reason, UpdatePackageManifest? manifest = null) => new(manifest, sha, problem, reason, checks);

        ZipArchive archive;
        try
        {
            archive = await ZipFile.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return Fail(UpdatePackageProblems.FormatInvalid, "The file is not a Zelvo update package.");
        }

        await using (archive.ConfigureAwait(false))
        {
            var manifestEntry = archive.GetEntry(ManifestEntry);
            if (manifestEntry is null || manifestEntry.Length > SignedFile.MaxBytes)
            {
                return Fail(UpdatePackageProblems.FormatInvalid, "The package has no Zelvo manifest.");
            }

            byte[] signedBytes;
            var stream = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                signedBytes = buffer.ToArray();
            }

            var signed = SignedFile.Parse(signedBytes);
            if (signed is null || signed.Type != SignedFileTypes.PackageManifest)
            {
                return Fail(UpdatePackageProblems.FormatInvalid, "The package's manifest is not in Zelvo's format.");
            }

            var payload = signed.PayloadBytes()!;
            var valid = license.VerifyVendorSignature(SignedFile.SigningInput(signed.Type, payload), signed.KeyId, signed.SignatureBytes()!);
            checks["signature"] = valid;
            if (!valid)
            {
                return license is LicenseService { } service && !service.TrustsKey(signed.KeyId)
                    ? Fail(UpdatePackageProblems.UnknownKey, $"The package is signed with key '{signed.KeyId}', which this installation does not trust.")
                    : Fail(UpdatePackageProblems.SignatureInvalid, "The package's signature is not valid: it was changed or was not issued by Zelvo.");
            }

            UpdatePackageManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(payload, UpdatePackageManifest.Json);
            }
            catch (JsonException)
            {
                manifest = null;
            }

            if (manifest is not { Format: UpdatePackageManifest.CurrentFormat, Product: "zelvo" } || manifest.ParsedVersion is null)
            {
                return Fail(UpdatePackageProblems.FormatInvalid, "The package's manifest is not in Zelvo's format.");
            }

            // Every entry is listed with its hash, and nothing else is in the package.
            var listed = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var entries = archive.Entries.Where(e => e.FullName != ManifestEntry && !e.FullName.EndsWith('/')).ToList();
            var filesOk = entries.Count == listed.Count && manifest.Images.All(i => listed.ContainsKey(i.File));
            foreach (var entry in entries)
            {
                if (!filesOk)
                {
                    break;
                }

                if (!SafePath(entry.FullName) || !listed.TryGetValue(entry.FullName, out var file) || file.Size != entry.Length)
                {
                    filesOk = false;
                    break;
                }

                var entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (entryStream.ConfigureAwait(false))
                {
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(entryStream, cancellationToken).ConfigureAwait(false));
                    filesOk = string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }

            checks["files"] = filesOk;
            if (!filesOk)
            {
                return Fail(UpdatePackageProblems.FileMismatch, "A file in the package does not match its signed manifest: the package was changed or is incomplete.", manifest);
            }

            return CheckInstallable(manifest, sha, license, installed, requiredImages, checks);
        }
    }

    /// <summary>The checks that depend on this installation (version, type, license, images); also run again before installing.</summary>
    public static UpdatePackageCheck CheckInstallable(
        UpdatePackageManifest manifest, string sha, ILicenseService license, Version installed, IReadOnlyCollection<string> requiredImages,
        Dictionary<string, bool>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(requiredImages);
        checks ??= new Dictionary<string, bool>(StringComparer.Ordinal) { ["signature"] = true, ["files"] = true };
        var version = manifest.ParsedVersion!;
        var current = UpdatePackageManifest.Normalize(installed);
        UpdatePackageCheck Fail(string problem, string reason) => new(manifest, sha, problem, reason, checks);

        checks["version"] = version > current;
        if (version <= current)
        {
            return Fail(UpdatePackageProblems.NotNewer, $"The package installs {version}, but {current} is installed already.");
        }

        if (manifest.ParsedRequiresVersion is { } minimum && current < minimum)
        {
            checks["version"] = false;
            return Fail(UpdatePackageProblems.InstalledTooOld, $"The package upgrades {minimum} or later; install the releases before it first.");
        }

        var sameFeatureVersion = version.Major == current.Major && version.Minor == current.Minor;
        checks["type"] = sameFeatureVersion == (manifest.Type == UpdatePackageType.Security);
        if (!checks["type"])
        {
            return Fail(UpdatePackageProblems.TypeMismatch, manifest.Type == UpdatePackageType.Security
                ? "A security release keeps the feature version; this package changes it."
                : "A feature release changes the feature version; this package does not.");
        }

        checks["license"] = license.Current.IsValid && license.IsVersionLicensed(version);
        if (!license.Current.IsValid)
        {
            return Fail(UpdatePackageProblems.NotLicensed, "Zelvo is not activated on these servers; activate it before importing updates.");
        }

        if (!checks["license"])
        {
            return Fail(UpdatePackageProblems.UpgradeLicenseRequired,
                $"Version {version.Major}.{version.Minor} is a paid feature release: import its upgrade license in Settings → License first.");
        }

        var tag = version.ToString(3);
        var missing = requiredImages.Where(image => !manifest.Images.Any(i => i.Reference == $"{image}:{tag}")).ToList();
        checks["images"] = missing.Count == 0;
        if (missing.Count > 0)
        {
            return Fail(UpdatePackageProblems.ImagesMissing, $"The package lacks images this installation runs: {string.Join(", ", missing)}.");
        }

        return new UpdatePackageCheck(manifest, sha, null, null, checks);
    }

    /// <summary>Extracts an entry to a file (the updater loads images from them).</summary>
    public static async Task ExtractAsync(string package, string entryPath, string destination, CancellationToken cancellationToken)
    {
        var archive = await ZipFile.OpenReadAsync(package, cancellationToken).ConfigureAwait(false);
        await using (archive.ConfigureAwait(false))
        {
            var entry = archive.GetEntry(entryPath) ?? throw new InvalidDataException($"{entryPath} is not in the package.");
            await entry.ExtractToFileAsync(destination, overwrite: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool SafePath(string path) =>
        path.Length is > 0 and <= 200 && !path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal)
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..")
        && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_');

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }
}

/// <summary>Builds and signs packages (the signing tool and tests only).</summary>
internal static class UpdatePackageWriter
{
    /// <summary>
    /// Writes <paramref name="output"/> from the files in <paramref name="contentDirectory"/>
    /// (images under <c>images/</c>, other files anywhere), the manifest's hashes filled in.
    /// </summary>
    public static async Task WriteAsync(UpdatePackageManifest manifest, string contentDirectory, string output, string keyId, RSA key, CancellationToken cancellationToken)
    {
        var files = new List<UpdatePackageFile>();
        var root = Path.GetFullPath(contentDirectory);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var stream = File.OpenRead(file);
            await using (stream.ConfigureAwait(false))
            {
                files.Add(new UpdatePackageFile(relative, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), stream.Length));
            }
        }

        var complete = manifest with { Files = files };
        var payload = JsonSerializer.SerializeToUtf8Bytes(complete, UpdatePackageManifest.Json);
        var signed = SignedFile.Create(SignedFileTypes.PackageManifest, payload, keyId, key);
        var target = File.Create(output);
        await using (target.ConfigureAwait(false))
        {
            using var zip = new ZipArchive(target, ZipArchiveMode.Create);
            var manifestEntry = zip.CreateEntry(UpdatePackageReader.ManifestEntry, CompressionLevel.Optimal);
            var manifestStream = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (manifestStream.ConfigureAwait(false))
            {
                await manifestStream.WriteAsync(signed.ToBytes(), cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in files)
            {
                // Image archives are large and already compact enough; storing them keeps imports fast.
                await zip.CreateEntryFromFileAsync(Path.Combine(root, file.Path), file.Path,
                    file.Path.EndsWith(".tar", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>The running product version.</summary>
public static class InstalledVersion
{
    /// <summary>Major.minor.patch of the running build (its informational version without the source revision).</summary>
    public static Version Current { get; } = System.Version.TryParse(LicenseService.ProductVersion(), out var v) ? UpdatePackageManifest.Normalize(v) : new Version(0, 0, 0);
}

/// <summary>Where updates are kept and what an install needs (<c>Updates:*</c> configuration, task F4).</summary>
public sealed class UpdateOptions
{
    /// <summary>The updates volume shared by the API and the updater: <c>packages/</c>, <c>inbox/</c> (files carried in on USB), <c>backups/</c>, <c>status/</c>.</summary>
    public string Directory { get; set; } = "/var/lib/zelvo/updates";

    /// <summary>Largest package accepted by upload (default 8 GiB).</summary>
    public long MaxPackageBytes { get; set; } = 8L * 1024 * 1024 * 1024;

    /// <summary>Zelvo's images this installation runs; a package must carry each, tagged with its version.</summary>
    public List<string> Images { get; set; } = ["zelvo/api", "zelvo/worker", "zelvo/web", "zelvo/keycloak", "zelvo/opensearch", "zelvo/updater"];

    public string PackagesDirectory => Path.Combine(Directory, "packages");

    public string InboxDirectory => Path.Combine(Directory, "inbox");

    public string BackupsDirectory => Path.Combine(Directory, "backups");

    public string StatusDirectory => Path.Combine(Directory, "status");
}

/// <summary>Registers <see cref="UpdateOptions"/>.</summary>
public static class UpdateOptionsExtensions
{
    /// <summary>
    /// Binds <c>Updates:*</c>. A configured image list replaces the default one instead of being
    /// added to it.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddZelvoUpdateOptions(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var section = configuration.GetSection("Updates");
        return Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.Configure<UpdateOptions>(services, options =>
        {
            var images = section.GetSection("Images").GetChildren().Select(c => c.Value).OfType<string>().ToList();
            if (section["Directory"] is { Length: > 0 } directory)
            {
                options.Directory = directory;
            }

            if (long.TryParse(section["MaxPackageBytes"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var max) && max > 0)
            {
                options.MaxPackageBytes = max;
            }

            if (images.Count > 0)
            {
                options.Images = images;
            }
        });
    }
}
