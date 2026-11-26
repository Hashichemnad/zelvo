using System.Security.Cryptography;
using System.Text.Json;
using Zelvo.Licensing;

namespace Zelvo.SigningTool;

/// <summary>
/// <c>zelvo-sign package</c> (module 13): builds and signs an update package from a folder with the
/// release's <c>docker save</c> archives (and any other files) and a manifest that names the
/// version, type, release notes and images. The tool lists every file with its SHA-256 and signs
/// the manifest, so any change to the package breaks the signature.
/// </summary>
internal static class PackageCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static async Task<int> RunAsync(Options options, TextWriter output, Func<string, string?> passphrase,
        Func<Options, Func<string, string?>, Task<(string KeyId, RSA Key)>> loadKey)
    {
        var manifestPath = options.Required("manifest");
        var directory = options.Required("dir");
        var outPath = options.Required("out");
        UpdatePackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false), Json);
        }
        catch (JsonException ex)
        {
            throw new ToolException($"{manifestPath} is not a package manifest: {ex.Message}");
        }

        if (manifest?.ParsedVersion is null)
        {
            throw new ToolException("The manifest needs a version such as 1.5.0.");
        }

        if (!Directory.Exists(directory))
        {
            throw new ToolException($"{directory} does not exist.");
        }

        foreach (var image in manifest.Images)
        {
            if (!File.Exists(Path.Combine(directory, image.File)))
            {
                throw new ToolException($"{image.File} (for {image.Reference}) is not in {directory}.");
            }

            if (!image.Reference.EndsWith($":{manifest.ParsedVersion.ToString(3)}", StringComparison.Ordinal))
            {
                throw new ToolException($"{image.Reference} is not tagged with the package's version {manifest.ParsedVersion.ToString(3)}.");
            }
        }

        var (keyId, key) = await loadKey(options, passphrase).ConfigureAwait(false);
        using (key)
        {
            await UpdatePackageWriter.WriteAsync(manifest with { CreatedAt = DateTimeOffset.UtcNow }, directory, outPath, keyId, key, CancellationToken.None).ConfigureAwait(false);
        }

        output.WriteLine($"Update package {manifest.ParsedVersion.ToString(3)} ({manifest.Type}) with {manifest.Images.Count} images written to {outPath}, signed with key {keyId}.");
        return 0;
    }
}
