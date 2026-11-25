using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Zelvo.Licensing;

namespace Zelvo.SigningTool;

/// <summary>
/// <c>zelvo-sign</c>: the offline signing tool kept on the product owner's computer (module 12).
/// No portal and no server: activation requests come in by email or USB, licenses go back the same
/// way. The private key is made here, encrypted with a passphrase, and never leaves this computer;
/// the tool refuses to write it inside a git working tree so it cannot be committed by accident.
/// </summary>
internal static class SigningTool
{
    private const int KeySize = 4096;

    // Strong encryption of the private key file at rest (PKCS#8, AES-256, PBKDF2-SHA256).
    private static readonly PbeParameters KeyEncryption = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<string, string?> passphrase)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            return args switch
            {
                ["keygen", .. var rest] => KeyGen(Options.Parse(rest), output, passphrase),
                ["issue", .. var rest] => await IssueAsync(Options.Parse(rest), output, passphrase).ConfigureAwait(false),
                ["upgrade", .. var rest] => await UpgradeAsync(Options.Parse(rest), output, passphrase).ConfigureAwait(false),
                ["inspect", var file] => await InspectAsync(file, output).ConfigureAwait(false),
                ["verify", var file, .. var rest] => await VerifyAsync(file, Options.Parse(rest), output).ConfigureAwait(false),
                ["package", .. var rest] => await PackageCommand.RunAsync(Options.Parse(rest), output, passphrase, LoadKeyAsync).ConfigureAwait(false),
                _ => Usage(error),
            };
        }
        catch (ToolException ex)
        {
            await error.WriteLineAsync($"zelvo-sign: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
    }

    private static int KeyGen(Options options, TextWriter output, Func<string, string?> passphrase)
    {
        var directory = Path.GetFullPath(options.Required("out"));
        var id = options.Get("id") ?? $"zelvo-{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{2,39}$"))
        {
            throw new ToolException("--id is 3-40 lower-case letters, digits and hyphens.");
        }

        if (InsideGitWorkingTree(directory))
        {
            throw new ToolException($"{directory} is inside a git working tree. Keep the private key outside any repository, on the signing computer only.");
        }

        var privatePath = Path.Combine(directory, $"{id}.private.pem");
        if (File.Exists(privatePath))
        {
            throw new ToolException($"{privatePath} exists already; a key is never overwritten.");
        }

        var secret = passphrase("Passphrase for the new private key: ");
        if (secret is not { Length: >= 12 })
        {
            throw new ToolException($"Give a passphrase of at least 12 characters (typed, or in {Passphrase.EnvironmentVariable}).");
        }

        if (Environment.GetEnvironmentVariable(Passphrase.EnvironmentVariable) is not { Length: > 0 } && passphrase("Type it again: ") != secret)
        {
            throw new ToolException("The passphrases differ.");
        }

        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(KeySize);
        var pem = PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", rsa.ExportEncryptedPkcs8PrivateKey(secret, KeyEncryption));
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Readable by the signing account only.
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(privatePath, create))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(pem);
        }

        var entry = LicenseIssuer.PublicKeyOf(id, rsa);
        var publicJson = JsonSerializer.Serialize(entry, Indented);
        File.WriteAllText(Path.Combine(directory, $"{id}.public.json"), publicJson);
        output.WriteLine($"Private key: {privatePath} (encrypted; keep it on this computer and back it up offline).");
        output.WriteLine("Add this entry to src/api/Zelvo.Licensing/vendor-keys.json (public key; safe to commit):");
        output.WriteLine(publicJson);
        return 0;
    }

    private static async Task<int> IssueAsync(Options options, TextWriter output, Func<string, string?> passphrase)
    {
        var request = await ReadRequestAsync(options.Required("request")).ConfigureAwait(false);
        var (keyId, key) = await LoadKeyAsync(options, passphrase).ConfigureAwait(false);
        using (key)
        {
            var kind = (options.Get("kind") ?? "production").ToUpperInvariant() switch
            {
                "PRODUCTION" => LicenseKind.Production,
                "TEST" => LicenseKind.Test,
                "DEMO" => LicenseKind.Demo,
                _ => throw new ToolException("--kind is production, test or demo."),
            };
            var expected = kind switch { LicenseKind.Test => "Test", LicenseKind.Demo => "Demo", _ => "Standard" };
            if (!string.Equals(request.Profile, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException($"The activation request comes from a {request.Profile} installation; a {kind} license would not work there.");
            }

            var modules = options.Get("modules") is { } list && list != "all"
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : LicensedModules.Phase1;
            var watermark = options.Get("watermark") ?? Watermark.Create();
            if (!Watermark.IsValid(watermark))
            {
                throw new ToolException("--watermark is not a valid watermark (ZW-XXXX-XXXX-XXXX-C).");
            }

            var version = PayloadVersion(options.Get("version") ?? FeatureVersion(request.ProductVersion));
            var terms = new LicenseTerms(options.Required("license-id"), kind, options.Required("customer-id"), options.Get("customer-en"), options.Get("customer-ar"),
                watermark, [.. modules], version);
            if (terms.CustomerNameEn is null && terms.CustomerNameAr is null)
            {
                throw new ToolException("Give the customer's name with --customer-en and/or --customer-ar.");
            }

            SignedFile file;
            try
            {
                file = LicenseIssuer.Issue(request, terms, keyId, key, DateTimeOffset.UtcNow);
            }
            catch (ArgumentException ex)
            {
                throw new ToolException(ex.Message);
            }

            var outPath = options.Required("out");
            await File.WriteAllBytesAsync(outPath, file.ToBytes()).ConfigureAwait(false);
            output.WriteLine($"License {terms.LicenseId} for {terms.CustomerNameEn ?? terms.CustomerNameAr} written to {outPath}.");
            output.WriteLine($"Watermark: {watermark} (record it in the customer sheet).");
            output.WriteLine($"Bound to installation {request.Fingerprint.InstallationId}, database {request.Fingerprint.DatabaseId}, domain {string.Join(", ", request.Fingerprint.Domains)}.");
            return 0;
        }
    }

    private static async Task<int> UpgradeAsync(Options options, TextWriter output, Func<string, string?> passphrase)
    {
        var request = await ReadRequestAsync(options.Required("request")).ConfigureAwait(false);
        var licenseId = options.Get("license-id") ?? request.CurrentLicenseId
            ?? throw new ToolException("The activation request names no installed license; give --license-id.");
        var version = PayloadVersion(options.Required("version"));
        var (keyId, key) = await LoadKeyAsync(options, passphrase).ConfigureAwait(false);
        using (key)
        {
            var upgradeId = options.Get("upgrade-id") ?? $"UPG-{licenseId}-{version.ToString(2)}";
            var file = LicenseIssuer.Upgrade(request, licenseId, upgradeId, version, keyId, key, DateTimeOffset.UtcNow);
            var outPath = options.Required("out");
            await File.WriteAllBytesAsync(outPath, file.ToBytes()).ConfigureAwait(false);
            output.WriteLine($"Upgrade license {upgradeId} unlocking {version.ToString(2)} for license {licenseId} written to {outPath}.");
            return 0;
        }
    }

    private static async Task<int> InspectAsync(string path, TextWriter output)
    {
        var content = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        if (ActivationRequestFile.Parse(content) is { } request)
        {
            output.WriteLine($"Activation request {request.RequestId} made {request.CreatedAt:u}");
            output.WriteLine($"  Version {request.ProductVersion}, profile {request.Profile}, current license {request.CurrentLicenseId ?? "none"} (customer {request.CurrentCustomerId ?? "none"})");
            WriteFingerprint(output, request.Fingerprint);
            return 0;
        }

        if (SignedFile.Parse(content) is { } file && LicenseIssuer.Peek(file) is { } payload)
        {
            output.WriteLine($"{file.Type} {payload.UpgradeId ?? payload.LicenseId} signed with key {file.KeyId} (not verified; use verify)");
            output.WriteLine($"  License {payload.LicenseId}, kind {payload.Kind ?? "-"}, version {payload.Version}, issued {payload.IssuedAt:u}");
            if (payload.Customer is { } customer)
            {
                output.WriteLine($"  Customer {customer.Id}: {customer.Name.En} / {customer.Name.Ar}; watermark {payload.Watermark}");
                output.WriteLine($"  Modules: {string.Join(", ", payload.Modules)}");
            }

            WriteFingerprint(output, payload.Fingerprint);
            return 0;
        }

        throw new ToolException($"{path} is neither an activation request nor a Zelvo license.");
    }

    private static async Task<int> VerifyAsync(string path, Options options, TextWriter output)
    {
        var keysPath = options.Get("keys");
        var keys = keysPath is null
            ? new VendorKeys(VendorKeys.Embedded())
            : new VendorKeys(JsonSerializer.Deserialize<List<VendorKey>>(await File.ReadAllBytesAsync(keysPath).ConfigureAwait(false)) ?? []);
        var file = SignedFile.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false))
            ?? throw new ToolException($"{path} is not a signed Zelvo file.");
        var check = file.Verify(keys);
        output.WriteLine(check switch
        {
            SignatureCheck.Valid => $"Valid {file.Type} signature by key {file.KeyId}.",
            SignatureCheck.UnknownKey => $"Key {file.KeyId} is not among the trusted keys.",
            _ => "The signature is NOT valid.",
        });
        return check == SignatureCheck.Valid ? 0 : 1;
    }

    /// <summary>Reads an encrypted private key; its id is the file name before <c>.private.pem</c> unless <c>--key-id</c> says otherwise.</summary>
    internal static async Task<(string KeyId, RSA Key)> LoadKeyAsync(Options options, Func<string, string?> passphrase)
    {
        var path = options.Required("key");
        var name = Path.GetFileName(path);
        var keyId = options.Get("key-id") ?? (name.EndsWith(".private.pem", StringComparison.Ordinal) ? name[..^".private.pem".Length] : null)
            ?? throw new ToolException("Give --key-id, or keep the key file's name <key-id>.private.pem.");
        var pem = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var secret = passphrase($"Passphrase for {name}: ") ?? throw new ToolException("A passphrase is needed for the private key.");
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(pem, secret);
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
            throw new ToolException("The private key could not be opened: wrong passphrase or not a Zelvo key file.");
        }

        return (keyId, rsa);
    }

    private static async Task<ActivationRequestFile> ReadRequestAsync(string path) =>
        ActivationRequestFile.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false))
        ?? throw new ToolException($"{path} is not a Zelvo activation request.");

    private static Version PayloadVersion(string value) =>
        LicensePayload.ParseFeatureVersion(value) ?? throw new ToolException($"'{value}' is not a version such as 1.4.");

    private static string FeatureVersion(string productVersion) =>
        LicensePayload.ParseFeatureVersion(productVersion)?.ToString(2) ?? "1.0";

    private static void WriteFingerprint(TextWriter output, Fingerprint fingerprint)
    {
        output.WriteLine($"  Installation {fingerprint.InstallationId}, database {fingerprint.DatabaseId}");
        output.WriteLine($"  Machines {string.Join(", ", fingerprint.MachineIds)}; cluster {fingerprint.ClusterId ?? "-"}; domains {string.Join(", ", fingerprint.Domains)}");
    }

    internal static bool InsideGitWorkingTree(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return true;
            }
        }

        return false;
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("""
            zelvo-sign: Zelvo's offline signing tool (keep it, and the private key, on the signing computer only)

              keygen  --out <dir> [--id <key-id>]
                      Make a new RSA-4096 vendor key pair. The private key is encrypted with a passphrase
                      (typed, or ZELVO_SIGNING_PASSPHRASE) and never written inside a git working tree.
              inspect <activation-request-or-license>
              issue   --request <activation-request.json> --key <key-id>.private.pem --license-id <id>
                      --customer-id <id> --customer-en <name> [--customer-ar <name>] [--kind production|test|demo]
                      [--modules all|m01-incidents,...] [--version 1.0] [--watermark ZW-...] --out <license.json>
              upgrade --request <activation-request.json> --key <key> --version 1.5 [--license-id <id>]
                      [--upgrade-id <id>] --out <upgrade-license.json>
              verify  <signed-file> [--keys <vendor-keys.json>]
              package --manifest <manifest.json> --dir <content-dir> --key <key> --out <zelvo-x.y.z.zpkg>
            """);
        return 1;
    }
}

/// <summary>A refusal with a message for the person using the tool.</summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary><c>--name value</c> options.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Options Parse(IReadOnlyList<string> args)
    {
        var options = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
            {
                throw new ToolException($"Unexpected argument '{args[i]}'.");
            }

            options._values[args[i][2..]] = args[++i];
        }

        return options;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string Required(string name) => Get(name) ?? throw new ToolException($"--{name} is required.");
}
