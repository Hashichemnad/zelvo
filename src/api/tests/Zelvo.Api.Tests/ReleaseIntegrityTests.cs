using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Licensing;
using Zelvo.Updater;

namespace Zelvo.Api.Tests;

/// <summary>Release integrity: the signed release manifest and component self-checks (module 12, task F6).</summary>
public sealed class ReleaseIntegrityTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly string _folder = Directory.CreateTempSubdirectory("zelvo-release-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string AppDirectory => Path.Combine(_folder, "app");

    private string ManifestPath => Path.Combine(_folder, "release", "release-manifest.json");

    public ReleaseIntegrityTests()
    {
        Directory.CreateDirectory(Path.Combine(AppDirectory, "wwwroot", "fonts"));
        Directory.CreateDirectory(Path.Combine(AppDirectory, ".next", "cache"));
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        File.WriteAllText(Path.Combine(AppDirectory, "Zelvo.Api.dll"), "assembly");
        File.WriteAllText(Path.Combine(AppDirectory, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(AppDirectory, "wwwroot", "fonts", "زيلفو.woff2"), "font");
        File.WriteAllText(Path.Combine(AppDirectory, "wwwroot", "a b.txt"), "space");
        File.WriteAllText(Path.Combine(AppDirectory, ".next", "cache", "page"), "written while running");
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task The_directory_hash_is_the_one_the_release_pipeline_computes_with_sha256sum()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var start = new ProcessStartInfo("sh", ["-c", "find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum"])
        {
            WorkingDirectory = AppDirectory,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);

        Assert.Equal(output.Split(' ')[0], ReleaseIntegrity.HashDirectory(AppDirectory));
    }

    [Fact]
    public void A_component_whose_files_match_the_signed_manifest_is_verified()
    {
        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory, [".next/cache"]), sign: _key);
        File.WriteAllText(Path.Combine(AppDirectory, ".next", "cache", "page"), "changed while running");

        var result = ReleaseIntegrity.Check("api", Options(), Keys());

        Assert.Equal(ReleaseCheckStatus.Verified, result.Status);
        Assert.Equal("1.4.0", result.Version);
    }

    [Fact]
    public void A_changed_file_makes_the_component_modified()
    {
        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory, [".next/cache"]), sign: _key);
        File.AppendAllText(Path.Combine(AppDirectory, "Zelvo.Api.dll"), "patched");

        var result = ReleaseIntegrity.Check("api", Options(), Keys());

        Assert.Equal(ReleaseCheckStatus.Modified, result.Status);
        Assert.NotEqual(result.Expected, result.Actual);
    }

    [Fact]
    public void An_added_file_makes_the_component_modified()
    {
        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory, [".next/cache"]), sign: _key);
        File.WriteAllText(Path.Combine(AppDirectory, "Extra.dll"), "added");

        Assert.Equal(ReleaseCheckStatus.Modified, ReleaseIntegrity.Check("api", Options(), Keys()).Status);
    }

    [Fact]
    public void A_manifest_changed_after_signing_or_signed_with_another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory, [".next/cache"]), sign: other);
        Assert.Equal(ReleaseCheckStatus.ManifestInvalid, ReleaseIntegrity.Check("api", Options(), Keys()).Status);

        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory, [".next/cache"]), sign: _key);
        File.WriteAllText(ManifestPath, File.ReadAllText(ManifestPath).Replace("1.4.0", "1.5.0", StringComparison.Ordinal));
        Assert.Equal(ReleaseCheckStatus.ManifestInvalid, ReleaseIntegrity.Check("api", Options(), Keys()).Status);

        File.Delete(ManifestPath + ".sig");
        Assert.Equal(ReleaseCheckStatus.ManifestInvalid, ReleaseIntegrity.Check("api", Options(), Keys()).Status);
    }

    [Fact]
    public void Missing_manifests_and_unknown_components_are_reported()
    {
        Assert.Equal(ReleaseCheckStatus.NoManifest, ReleaseIntegrity.Check("api", Options(), Keys()).Status);

        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory), sign: _key);
        Assert.Equal(ReleaseCheckStatus.ComponentUnknown, ReleaseIntegrity.Check("reports", Options(), Keys()).Status);
    }

    [Fact]
    public void Only_ecdsa_p256_keys_are_accepted()
    {
        using var rsa = RSA.Create(2048);
        Assert.Throws<LicenseException>(() => ImageSigningKeys.FromPem([("rsa", rsa.ExportSubjectPublicKeyInfoPem())]));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<LicenseException>(() => ImageSigningKeys.FromPem([("p384", p384.ExportSubjectPublicKeyInfoPem())]));
    }

    [Fact]
    public async Task A_release_build_refuses_to_start_a_modified_component_and_reports_it()
    {
        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory, [".next/cache"]), sign: _key);
        File.AppendAllText(Path.Combine(AppDirectory, "appsettings.json"), " ");
        var store = new MemoryStore();
        var enforced = new ReleaseIntegrityCheck(new ReleaseComponentIdentity("api"), Options(), Keys(), store, TimeProvider.System, NullLogger<ReleaseIntegrityCheck>.Instance)
        {
            Enforce = true,
        };
        var development = new ReleaseIntegrityCheck(new ReleaseComponentIdentity("api"), Options(), Keys(), store, TimeProvider.System, NullLogger<ReleaseIntegrityCheck>.Instance)
        {
            Enforce = false,
        };

        await Assert.ThrowsAsync<LicenseException>(() => enforced.StartAsync(Ct));
        await development.StartAsync(Ct);

        var report = Assert.Single(await store.ListAsync(Ct));
        Assert.Equal("Modified", report.Status);
        Assert.Equal("1.4.0", report.Version);
    }

    [Fact]
    public void The_api_flags_components_that_failed_or_run_another_release_once_per_change()
    {
        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory), sign: _key);
        var monitor = new ReleaseComponentMonitor(new MemoryStore(), Options(), Keys(), NullLogger<ReleaseComponentMonitor>.Instance);
        var now = DateTimeOffset.UtcNow;
        ReleaseComponentReport[] reports =
        [
            new("api", "Verified", "1.4.0", "1.4.0", "ok", now),
            new("worker", "Modified", "1.4.0", "1.4.0", "files differ", now),
            new("updater", "Verified", "1.3.0", "1.3.0", "ok", now),
            new("images", "ImageMismatch", "1.4.0", "1.4.0", "web runs another image", now),
        ];

        Assert.Equal(["worker", "updater", "images"], monitor.Review(reports));
        Assert.Empty(monitor.Review(reports));
        Assert.Empty(monitor.Review([reports[0] with { Component = "worker" }]));
    }

    [Fact]
    public async Task The_updater_reports_containers_that_run_another_image_than_the_release()
    {
        WriteManifest(ReleaseIntegrity.HashDirectory(AppDirectory), sign: _key);
        var host = new Zelvo.Api.Tests.FakeContainerHost();
        host.Running.Add(new RunningContainer("api", "sha256:" + new string('c', 64)));
        host.Running.Add(new RunningContainer("postgres", "sha256:" + new string('d', 64)));
        var store = new MemoryStore();
        var job = new ImageIntegrityJob(host, Options(), Keys(), store, TimeProvider.System, NullLogger<ImageIntegrityJob>.Instance);

        Assert.Equal("ImagesVerified", (await job.CheckAsync(Ct))!.Status);

        host.Running.Add(new RunningContainer("api", "sha256:" + new string('e', 64)));
        var report = (await job.CheckAsync(Ct))!;
        Assert.Equal("ImageMismatch", report.Status);
        Assert.Contains("api", report.Detail, StringComparison.Ordinal);
        Assert.Equal("ImageMismatch", Assert.Single(await store.ListAsync(Ct)).Status);
    }

    private ReleaseIntegrityOptions Options() => new() { ManifestPath = ManifestPath, ApplicationDirectory = AppDirectory };

    private ImageSigningKeys Keys() => ImageSigningKeys.FromPem([("test", _key.ExportSubjectPublicKeyInfoPem())]);

    private void WriteManifest(string filesSha256, ECDsa sign)
    {
        var manifest = new ReleaseManifest(ReleaseManifest.CurrentFormat, "1.4.0", DateTimeOffset.UtcNow,
        [
            new ReleaseComponent("api", "registry.example/zelvo/api", "sha256:" + new string('a', 64), "sha256:" + new string('c', 64), filesSha256, "/app", [".next/cache"]),
            new ReleaseComponent("keycloak", "registry.example/zelvo/keycloak", "sha256:" + new string('b', 64)),
        ]);
        var content = JsonSerializer.SerializeToUtf8Bytes(manifest, WebJson);
        File.WriteAllBytes(ManifestPath, content);
        // cosign sign-blob: base64 of an ASN.1 DER ECDSA signature over SHA-256 of the file.
        File.WriteAllText(ManifestPath + ".sig", Convert.ToBase64String(sign.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)), Encoding.ASCII);
    }

    private sealed class MemoryStore : IReleaseComponentStore
    {
        private readonly Dictionary<string, ReleaseComponentReport> _reports = [];

        public Task RecordAsync(ReleaseComponentReport report, CancellationToken cancellationToken)
        {
            _reports[report.Component] = report;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReleaseComponentReport>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReleaseComponentReport>>([.. _reports.Values]);
    }
}
