using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Licensing;

namespace Zelvo.Api.Tests;

/// <summary>License verification, fingerprint, upgrades and watermark (module 12, task F3).</summary>
public sealed class LicensingTests : IDisposable
{
    private readonly LicensingTestSupport _support = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _support.Dispose();

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Development_license_allows_everything_outside_production(string environmentName)
    {
        var service = LicensingTestSupport.Development(environmentName);

        var status = await service.VerifyAsync(Ct);

        Assert.True(status.IsValid);
        Assert.Equal(LicenseKind.Development, status.Kind);
        Assert.All(LicensedModules.Phase1, module => Assert.True(service.IsModuleLicensed(module)));
        Assert.True(service.IsVersionLicensed(new Version(99, 0)));
    }

    [Fact]
    public async Task Development_build_without_a_license_refuses_production()
    {
        var service = LicensingTestSupport.Development(Environments.Production);

        var status = await service.VerifyAsync(Ct);

        Assert.False(status.IsValid);
        Assert.Equal(LicenseProblems.DevelopmentInProduction, status.ProblemCode);
        Assert.False(status.ActivationRequired);
        Assert.False(service.IsModuleLicensed(LicensedModules.Incidents));
    }

    [Theory]
    [InlineData("Demo", LicenseKind.Demo)]
    [InlineData("demo", LicenseKind.Demo)]
    [InlineData("Test", LicenseKind.Test)]
    [InlineData(null, LicenseKind.Development)]
    [InlineData("Standard", LicenseKind.Development)]
    public async Task Development_license_follows_the_installation_profile(string? profile, LicenseKind kind)
    {
        var service = LicensingTestSupport.Development("Development", Profile(profile));

        var status = await service.VerifyAsync(Ct);

        Assert.True(status.IsValid);
        Assert.Equal(kind, status.Kind);
        Assert.Equal(kind == LicenseKind.Demo, service.IsDemo());
    }

    [Fact]
    public async Task Development_license_refuses_an_unknown_profile()
    {
        var service = LicensingTestSupport.Development("Development", Profile("Trial"));

        Assert.False((await service.VerifyAsync(Ct)).IsValid);
        Assert.False(service.IsDemo());
    }

    [Fact]
    public void Api_refuses_to_start_in_production_without_a_license()
    {
        using var factory = new ApiFactory { EnvironmentName = Environments.Production };

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.IsType<LicenseException>(error.GetBaseException());
    }

    [Fact]
    public void Phase1_lists_fifteen_unique_modules()
    {
        Assert.Equal(15, LicensedModules.Phase1.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Without_a_license_the_installation_waits_for_activation()
    {
        var service = _support.Service();

        var status = await service.VerifyAsync(Ct);

        Assert.False(status.IsValid);
        Assert.Equal(LicenseProblems.NotActivated, status.ProblemCode);
        Assert.True(status.ActivationRequired);
        Assert.False(service.IsModuleLicensed(LicensedModules.Incidents));
    }

    [Fact]
    public async Task The_activation_request_names_these_servers_and_holds_no_secrets()
    {
        var service = _support.Service();

        var file = await service.CreateActivationRequestAsync(Ct);
        var request = ActivationRequestFile.Parse(file.Content.Span)!;

        Assert.EndsWith(".json", file.FileName, StringComparison.Ordinal);
        Assert.Equal(_support.Store.InstallationId, request.Fingerprint.InstallationId);
        Assert.Equal(_support.Store.DatabaseId, request.Fingerprint.DatabaseId);
        Assert.Equal([File.ReadAllText(_support.MachineIdFile)], request.Fingerprint.MachineIds);
        Assert.Equal([_support.Domain], request.Fingerprint.Domains);
        Assert.Equal("Standard", request.Profile);
        Assert.StartsWith("AR-", request.RequestId, StringComparison.Ordinal);
        var json = JsonNode.Parse(file.Content.Span)!.AsObject();
        Assert.Equal(["format", "requestId", "createdAt", "productVersion", "profile", "currentLicenseId", "currentCustomerId", "fingerprint"], json.Select(p => p.Key));
    }

    [Fact]
    public async Task A_license_for_these_servers_activates_the_licensed_modules_and_version()
    {
        var service = _support.Service();
        var request = await LicensingTestSupport.RequestAsync(service);
        var license = _support.License(request, [LicensedModules.Incidents, LicensedModules.Sla, LicensedModules.Portal], version: "1.4");

        var result = await service.ImportLicenseAsync(new MemoryStream(license), new LicenseImportContext(null, null, "license.json"), Ct);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal("License", result.FileType);
        var status = service.Current;
        Assert.True(status.IsValid);
        Assert.Equal((LicenseKind.Production, "Example Authority", "هيئة المثال", "C-0001"), (status.Kind, status.LicensedTo, status.LicensedToAr, status.CustomerId));
        Assert.True(service.IsModuleLicensed(LicensedModules.Incidents));
        Assert.False(service.IsModuleLicensed(LicensedModules.Payments));
        Assert.True(service.IsVersionLicensed(new Version(1, 4, 9)));
        Assert.True(service.IsVersionLicensed(new Version(1, 2)));
        Assert.False(service.IsVersionLicensed(new Version(1, 5, 0)));
        Assert.True(Watermark.IsValid(((IInstallationWatermark)service).Current));

        // Another service (the worker) reads the same store and agrees.
        var other = _support.Service();
        Assert.True((await other.VerifyAsync(Ct)).IsValid);
    }

    [Fact]
    public async Task An_upgrade_license_unlocks_a_feature_release_for_that_license_only()
    {
        var service = _support.Service();
        var request = await LicensingTestSupport.RequestAsync(service);
        Assert.True((await service.ImportLicenseAsync(new MemoryStream(_support.License(request, version: "1.4")), Context(), Ct)).Accepted);

        var wrong = await service.ImportLicenseAsync(new MemoryStream(_support.Upgrade(request, "LIC-OTHER", "1.5")), Context(), Ct);
        Assert.Equal((false, LicenseProblems.UpgradeMismatch), (wrong.Accepted, wrong.ProblemCode));

        var upgrade = await service.ImportLicenseAsync(new MemoryStream(_support.Upgrade(request, "LIC-TEST-0001", "1.5")), Context(), Ct);
        Assert.True(upgrade.Accepted, upgrade.Reason);
        Assert.Equal("Upgrade", upgrade.FileType);
        Assert.True(service.IsVersionLicensed(new Version(1, 5, 2)));
        Assert.False(service.IsVersionLicensed(new Version(2, 0)));
        Assert.Equal([new Version(1, 5)], service.Current.Upgrades);

        // The new activation request names the installed license, for upgrades and server moves.
        Assert.Equal("LIC-TEST-0001", (await LicensingTestSupport.RequestAsync(service)).CurrentLicenseId);
    }

    [Fact]
    public async Task A_changed_license_file_is_refused_and_stops_the_service_when_kept()
    {
        var service = _support.Service();
        var request = await LicensingTestSupport.RequestAsync(service);
        var license = _support.License(request, [LicensedModules.Incidents]);
        var tampered = Tamper(license);

        var refused = await service.ImportLicenseAsync(new MemoryStream(tampered), Context(), Ct);
        Assert.Equal((false, LicenseProblems.SignatureInvalid), (refused.Accepted, refused.ProblemCode));
        Assert.Empty(await _support.Store.ListAsync(Ct));

        // Someone with database access changes the kept file: the services refuse to start.
        Assert.True((await service.ImportLicenseAsync(new MemoryStream(license), Context(), Ct)).Accepted);
        _support.Store.Tamper(Tamper);
        var status = await service.VerifyAsync(Ct);
        Assert.Equal((false, LicenseProblems.SignatureInvalid, false), (status.IsValid, status.ProblemCode, status.ActivationRequired));
        var check = new LicenseStartupCheck(service, NullLogger<LicenseStartupCheck>.Instance);
        await Assert.ThrowsAsync<LicenseException>(() => check.StartAsync(Ct));
    }

    [Fact]
    public async Task Licenses_signed_with_another_key_or_for_other_servers_are_refused()
    {
        var service = _support.Service();
        var request = await LicensingTestSupport.RequestAsync(service);

        using var stranger = RSA.Create(3072);
        var otherKey = await service.ImportLicenseAsync(new MemoryStream(_support.License(request, keyId: "someone-else", key: stranger)), Context(), Ct);
        Assert.Equal(LicenseProblems.UnknownKey, otherKey.ProblemCode);
        var forged = await service.ImportLicenseAsync(new MemoryStream(_support.License(request, key: stranger)), Context(), Ct);
        Assert.Equal(LicenseProblems.SignatureInvalid, forged.ProblemCode);

        var elsewhere = request with { Fingerprint = request.Fingerprint with { DatabaseId = "7399999999999999999" } };
        var moved = await service.ImportLicenseAsync(new MemoryStream(_support.License(elsewhere)), Context(), Ct);
        Assert.Equal(LicenseProblems.FingerprintMismatch, moved.ProblemCode);
        Assert.Contains("database", moved.Reason, StringComparison.Ordinal);

        var demo = await service.ImportLicenseAsync(new MemoryStream(_support.License(request, kind: LicenseKind.Demo)), Context(), Ct);
        Assert.Equal(LicenseProblems.KindMismatch, demo.ProblemCode);

        var junk = await service.ImportLicenseAsync(new MemoryStream(Encoding.UTF8.GetBytes("{\"hello\":1}")), Context(), Ct);
        Assert.Equal(LicenseProblems.FormatInvalid, junk.ProblemCode);
        Assert.False(service.Current.IsValid);
    }

    [Fact]
    public async Task A_license_stops_applying_when_the_servers_change_and_a_new_activation_is_needed()
    {
        var service = _support.Service();
        var request = await LicensingTestSupport.RequestAsync(service);
        Assert.True((await service.ImportLicenseAsync(new MemoryStream(_support.License(request)), Context(), Ct)).Accepted);

        // The installation is moved to another server (another machine id).
        File.WriteAllText(_support.MachineIdFile, Guid.NewGuid().ToString("N"));
        var status = await service.VerifyAsync(Ct);
        Assert.Equal((false, LicenseProblems.FingerprintMismatch, true), (status.IsValid, status.ProblemCode, status.ActivationRequired));
        var report = await service.GetFingerprintAsync(Ct);
        Assert.Equal(false, report.Parts.Single(p => p.Key == "machineId").Matches);
        Assert.Equal(true, report.Parts.Single(p => p.Key == "databaseId").Matches);

        // The replacement license for the new fingerprint applies again.
        var replacement = _support.License(await LicensingTestSupport.RequestAsync(service), licenseId: "LIC-TEST-0002");
        Assert.True((await service.ImportLicenseAsync(new MemoryStream(replacement), Context(), Ct)).Accepted);
        Assert.Equal("LIC-TEST-0002", service.Current.LicenseId);
        Assert.Single(await _support.Store.ListAsync(Ct));
    }

    [Fact]
    public async Task A_cluster_license_binds_the_cluster_instead_of_machines()
    {
        var service = _support.Service();
        var request = await LicensingTestSupport.RequestAsync(service);
        var cluster = request with { Fingerprint = request.Fingerprint with { ClusterId = "kube-1", MachineIds = ["node-a"] } };

        var refused = await service.ImportLicenseAsync(new MemoryStream(_support.License(cluster)), Context(), Ct);

        // This installation reports no cluster id, so a cluster license does not fit it.
        Assert.Equal(LicenseProblems.FingerprintMismatch, refused.ProblemCode);
        Assert.Contains("cluster", refused.Reason, StringComparison.Ordinal);
        var match = cluster.Fingerprint.Match(cluster.Fingerprint with { MachineIds = ["node-b"] });
        Assert.True(match.All);
    }

    [Fact]
    public async Task Vendor_signatures_on_other_files_are_checked_with_the_trusted_keys()
    {
        var service = _support.Service();
        var data = RandomNumberGenerator.GetBytes(64);
        var signature = LicensingTestSupport.Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        Assert.True(service.VerifyVendorSignature(data, _support.KeyId, signature));
        data[0] ^= 1;
        Assert.False(service.VerifyVendorSignature(data, _support.KeyId, signature));
        Assert.False(service.VerifyVendorSignature(data, "unknown", signature));
        await Task.CompletedTask;
    }

    [Fact]
    public void Vendor_keys_shorter_than_3072_bits_are_refused_and_the_embedded_list_has_only_public_keys()
    {
        using var small = RSA.Create(2048);
        Assert.Throws<InvalidOperationException>(() => new VendorKeys([LicenseIssuer.PublicKeyOf("small", small)]));
        foreach (var key in VendorKeys.Embedded())
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
        }
    }

    [Fact]
    public void Watermarks_carry_a_check_character()
    {
        var watermark = Watermark.Create();

        Assert.Matches("^ZW-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]$", watermark);
        Assert.True(Watermark.IsValid(watermark));
        var changed = watermark[..3] + (watermark[3] == '0' ? '1' : '0') + watermark[4..];
        Assert.False(Watermark.IsValid(changed));
        Assert.False(Watermark.IsValid("ZW-1234"));
        Assert.NotEqual(watermark, Watermark.Create());
    }

    [Fact]
    public async Task A_license_waits_in_the_worker_until_activation()
    {
        var service = _support.Service();
        var check = new LicenseStartupCheck(service, NullLogger<LicenseStartupCheck>.Instance, new LicenseStartupOptions(ActivationWait.WaitForLicense, TimeSpan.FromMilliseconds(50)));
        var waiting = check.StartAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.False(waiting.IsCompleted);

        var request = await LicensingTestSupport.RequestAsync(service);
        Assert.True((await service.ImportLicenseAsync(new MemoryStream(_support.License(request)), Context(), Ct)).Accepted);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The API starts limited instead.
        var api = new LicenseStartupCheck(_support.Service(store: new MemoryInstallationStore()), NullLogger<LicenseStartupCheck>.Instance);
        await api.StartAsync(Ct);
    }

    private static LicenseImportContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), "license.json");

    // Flips the license's module list inside the signed payload.
    private static byte[] Tamper(byte[] license)
    {
        var json = JsonNode.Parse(license)!.AsObject();
        var payload = JsonNode.Parse(Convert.FromBase64String((string)json["payload"]!))!.AsObject();
        payload["modules"] = new JsonArray([.. LicensedModules.Phase1.Select(m => (JsonNode)m)]);
        json["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private static IConfiguration Profile(string? profile) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [InstallationProfile.ConfigurationKey] = profile }).Build();
}
