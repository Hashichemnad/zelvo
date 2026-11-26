using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Licensing;

namespace Zelvo.Api.Tests;

/// <summary>
/// Licensing in tests (task F3): a throwaway signing key made at run time (never a key in the
/// repository), an in-memory installation store and services built around them.
/// </summary>
internal sealed class LicensingTestSupport : IDisposable
{
    private static readonly Lazy<RSA> SharedKey = new(() => RSA.Create(3072));

    public LicensingTestSupport()
    {
        KeyId = $"test-{Guid.NewGuid():N}"[..13];
        MachineIdFile = Path.Combine(Path.GetTempPath(), $"zelvo-machine-{Guid.NewGuid():N}");
        File.WriteAllText(MachineIdFile, Guid.NewGuid().ToString("N"));
    }

    /// <summary>A key made for this test run.</summary>
    public static RSA Key => SharedKey.Value;

    public string KeyId { get; }

    public string MachineIdFile { get; }

    public MemoryInstallationStore Store { get; } = new();

    public string Domain { get; } = "zelvo.example.test";

    public VendorKeys Keys => new([LicenseIssuer.PublicKeyOf(KeyId, Key)]);

    /// <summary>Configuration that trusts the test key and points the fingerprint at the test files.</summary>
    public Dictionary<string, string?> Settings(bool developmentLicense = false, string? profile = null) => new()
    {
        ["Licensing:DevelopmentLicense"] = developmentLicense ? "true" : "false",
        ["Licensing:DevelopmentKeys:0:Id"] = KeyId,
        ["Licensing:DevelopmentKeys:0:PublicKey"] = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()),
        ["Licensing:MachineIdFiles:0"] = MachineIdFile,
        ["Licensing:Domain"] = Domain,
        [InstallationProfile.ConfigurationKey] = profile,
    };

    public LicenseService Service(string environment = "Development", bool developmentLicense = false, string? profile = null, IInstallationStore? store = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings(developmentLicense, profile)).Build();
        return new LicenseService(store ?? Store, new ServerFingerprintSource(configuration), VendorKeys.Load(configuration),
            new HostingEnvironment { EnvironmentName = environment }, configuration, NullLogger<LicenseService>.Instance);
    }

    /// <summary>A development-build service without any license (the development license).</summary>
    public static LicenseService Development(string environment, IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().Build();
        return new LicenseService(new MemoryInstallationStore(), new ServerFingerprintSource(configuration), VendorKeys.Load(configuration),
            new HostingEnvironment { EnvironmentName = environment }, configuration, NullLogger<LicenseService>.Instance);
    }

    /// <summary>A license for the servers in <paramref name="request"/>.</summary>
    public byte[] License(ActivationRequestFile request, IReadOnlyList<string>? modules = null, LicenseKind kind = LicenseKind.Production, string version = "1.4",
        string? licenseId = null, string? keyId = null, RSA? key = null) =>
        LicenseIssuer.Issue(request, new LicenseTerms(licenseId ?? "LIC-TEST-0001", kind, "C-0001", "Example Authority", "هيئة المثال", Watermark.Create(),
            modules ?? LicensedModules.Phase1, Version.Parse(version)), keyId ?? KeyId, key ?? Key, DateTimeOffset.UtcNow).ToBytes();

    public byte[] Upgrade(ActivationRequestFile request, string licenseId, string version) =>
        LicenseIssuer.Upgrade(request, licenseId, "UPG-TEST-0001", Version.Parse(version), KeyId, Key, DateTimeOffset.UtcNow).ToBytes();

    public static async Task<ActivationRequestFile> RequestAsync(ILicenseService service) =>
        ActivationRequestFile.Parse((await service.CreateActivationRequestAsync(TestContext.Current.CancellationToken)).Content.Span)!;

    /// <summary>Hosts the API with this store and key (the database is not touched for licensing).</summary>
    public ApiFactory Apply(ApiFactory factory, bool developmentLicense = false)
    {
        foreach (var (key, value) in Settings(developmentLicense))
        {
            if (value is not null)
            {
                factory.WithSetting(key, value);
            }
        }

        return factory.WithServices(services =>
        {
            services.RemoveAll<IInstallationStore>();
            services.AddSingleton<IInstallationStore>(Store);
        });
    }

    public void Dispose()
    {
        try
        {
            File.Delete(MachineIdFile);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>An installation store in memory.</summary>
internal sealed class MemoryInstallationStore : IInstallationStore
{
    private readonly List<(StoredLicenseFile File, bool Current)> _files = [];

    public string InstallationId { get; set; } = Guid.NewGuid().ToString();

    public string DatabaseId { get; set; } = "7311111111111111111";

    public Task<(string? InstallationId, string? DatabaseId)> GetIdentityAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(string?, string?)>((InstallationId, DatabaseId));

    public Task<IReadOnlyList<StoredLicenseFile>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_files)
        {
            return Task.FromResult<IReadOnlyList<StoredLicenseFile>>([.. _files.Where(f => f.Current).Select(f => f.File)]);
        }
    }

    public Task AddAsync(string fileType, string licenseId, byte[] content, string? watermark, LicenseImportContext context, CancellationToken cancellationToken)
    {
        lock (_files)
        {
            if (fileType == "License")
            {
                for (var i = 0; i < _files.Count; i++)
                {
                    _files[i] = (_files[i].File, false);
                }
            }

            _files.Add((new StoredLicenseFile(Guid.NewGuid(), fileType, licenseId, content, DateTimeOffset.UtcNow, context.FileName), true));
        }

        return Task.CompletedTask;
    }

    /// <summary>Changes a kept file's bytes, as someone with database access could.</summary>
    public void Tamper(Func<byte[], byte[]> change)
    {
        lock (_files)
        {
            var (file, current) = _files[^1];
            _files[^1] = (file with { Content = change(file.Content) }, current);
        }
    }
}
