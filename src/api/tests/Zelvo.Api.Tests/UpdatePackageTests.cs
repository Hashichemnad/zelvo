using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Data.Installation;
using Zelvo.Licensing;
using Zelvo.Updater;

namespace Zelvo.Api.Tests;

/// <summary>Update packages and the installer (module 13, task F4), with packages and keys made at run time.</summary>
public sealed class UpdatePackageTests : IDisposable
{
    private readonly LicensingTestSupport _licensing = new();
    private readonly UpdateTestSupport _updates = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _licensing.Dispose();
        _updates.Dispose();
    }

    [Fact]
    public async Task A_signed_security_release_passes_every_check()
    {
        var license = LicensingTestSupport.Development("Development", new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(_licensing.Settings(developmentLicense: true)).Build());
        await license.VerifyAsync(Ct);
        var package = await _updates.PackageAsync(_licensing, "1.4.1");

        var check = await UpdatePackageReader.CheckAsync(package, license, new Version(1, 4, 0), UpdateTestSupport.Images, Ct);

        Assert.True(check.Passed, check.Reason);
        Assert.Equal(("1.4.1", UpdatePackageType.Security, 2), (check.Manifest!.Version, check.Manifest.Type, check.Manifest.Images.Count));
        Assert.Equal(["signature", "files", "version", "type", "license", "images"], check.Checks.Keys);
        Assert.Equal(64, check.Sha256.Length);
    }

    [Fact]
    public async Task Changed_incomplete_or_foreign_packages_are_refused()
    {
        var license = await DevelopmentAsync();

        var changed = await _updates.PackageAsync(_licensing, "1.4.1");
        UpdateTestSupport.Change(changed, "images/api.tar", add: false);
        Assert.Equal(UpdatePackageProblems.FileMismatch, (await CheckAsync(changed, license)).Problem);

        var extra = await _updates.PackageAsync(_licensing, "1.4.1");
        UpdateTestSupport.Change(extra, "images/extra.tar", add: true);
        Assert.Equal(UpdatePackageProblems.FileMismatch, (await CheckAsync(extra, license)).Problem);

        using var stranger = RSA.Create(3072);
        var foreign = await _updates.PackageAsync(_licensing, "1.4.1", keyId: "someone-else", key: stranger);
        Assert.Equal(UpdatePackageProblems.UnknownKey, (await CheckAsync(foreign, license)).Problem);
        var forged = await _updates.PackageAsync(_licensing, "1.4.1", key: stranger);
        Assert.Equal(UpdatePackageProblems.SignatureInvalid, (await CheckAsync(forged, license)).Problem);

        var junk = Path.Combine(_updates.Folder, "junk.zpkg");
        await File.WriteAllBytesAsync(junk, RandomNumberGenerator.GetBytes(100), Ct);
        Assert.Equal(UpdatePackageProblems.FormatInvalid, (await CheckAsync(junk, license)).Problem);
    }

    [Fact]
    public async Task Versions_types_images_and_upgrade_licenses_are_checked()
    {
        var license = _licensing.Service();
        var request = await LicensingTestSupport.RequestAsync(license);
        Assert.True((await license.ImportLicenseAsync(new MemoryStream(_licensing.License(request, version: "1.4")), new LicenseImportContext(null, null, null), Ct)).Accepted);

        Assert.Equal(UpdatePackageProblems.NotNewer, (await CheckAsync(await _updates.PackageAsync(_licensing, "1.4.0"), license)).Problem);
        Assert.Equal(UpdatePackageProblems.TypeMismatch, (await CheckAsync(await _updates.PackageAsync(_licensing, "1.5.0"), license)).Problem);
        Assert.Equal(UpdatePackageProblems.TypeMismatch, (await CheckAsync(await _updates.PackageAsync(_licensing, "1.4.2", UpdatePackageType.Feature), license)).Problem);
        Assert.Equal(UpdatePackageProblems.ImagesMissing, (await CheckAsync(await _updates.PackageAsync(_licensing, "1.4.1", images: ["zelvo/api"]), license)).Problem);
        Assert.Equal(UpdatePackageProblems.InstalledTooOld, (await CheckAsync(await _updates.PackageAsync(_licensing, "1.4.5", requiresVersion: "1.4.3"), license)).Problem);
        Assert.True((await CheckAsync(await _updates.PackageAsync(_licensing, "1.4.1"), license)).Passed);

        // A feature release is paid: it needs the upgrade license.
        var feature = await _updates.PackageAsync(_licensing, "1.5.0", UpdatePackageType.Feature);
        var refused = await CheckAsync(feature, license);
        Assert.Equal(UpdatePackageProblems.UpgradeLicenseRequired, refused.Problem);
        Assert.Contains("upgrade license", refused.Reason, StringComparison.Ordinal);
        Assert.True((await license.ImportLicenseAsync(new MemoryStream(_licensing.Upgrade(request, "LIC-TEST-0001", "1.5")), new LicenseImportContext(null, null, null), Ct)).Accepted);
        Assert.True((await CheckAsync(feature, license)).Passed);
    }

    [Fact]
    public async Task The_signing_tool_packages_a_release_the_installation_accepts()
    {
        var folder = Path.Combine(_updates.Folder, "tool");
        Directory.CreateDirectory(Path.Combine(folder, "content", "images"));
        foreach (var image in new[] { "api", "web" })
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "content", "images", $"{image}.tar"), RandomNumberGenerator.GetBytes(512), Ct);
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), """
            { "version": "1.4.3", "type": "Security", "releaseNotes": { "en": "Fixes", "ar": "إصلاحات" },
              "images": [ { "reference": "zelvo/api:1.4.3", "file": "images/api.tar" }, { "reference": "zelvo/web:1.4.3", "file": "images/web.tar" } ] }
            """, Ct);
        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        Assert.Equal(0, await SigningTool.SigningTool.RunAsync(["keygen", "--out", Path.Combine(folder, "keys"), "--id", "zelvo-pkg"], TextWriter.Null, TextWriter.Null, _ => passphrase));
        var package = Path.Combine(folder, "zelvo-1.4.3.zpkg");
        Assert.Equal(0, await SigningTool.SigningTool.RunAsync(["package", "--manifest", Path.Combine(folder, "manifest.json"), "--dir", Path.Combine(folder, "content"),
            "--key", Path.Combine(folder, "keys", "zelvo-pkg.private.pem"), "--out", package], TextWriter.Null, TextWriter.Null, _ => passphrase));

        var publicKey = System.Text.Json.JsonSerializer.Deserialize<VendorKey>(await File.ReadAllTextAsync(Path.Combine(folder, "keys", "zelvo-pkg.public.json"), Ct))!;
        var settings = new Dictionary<string, string?>(_licensing.Settings(developmentLicense: true))
        {
            ["Licensing:DevelopmentKeys:0:Id"] = publicKey.Id, ["Licensing:DevelopmentKeys:0:PublicKey"] = publicKey.PublicKey,
        };
        var license = LicensingTestSupport.Development("Development", new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        await license.VerifyAsync(Ct);
        Assert.True((await CheckAsync(package, license)).Passed);

        // Images tagged with another version are refused by the tool.
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), """
            { "version": "1.4.4", "type": "Security", "releaseNotes": { "en": "x", "ar": "س" }, "images": [ { "reference": "zelvo/api:1.4.3", "file": "images/api.tar" } ] }
            """, Ct);
        Assert.Equal(2, await SigningTool.SigningTool.RunAsync(["package", "--manifest", Path.Combine(folder, "manifest.json"), "--dir", Path.Combine(folder, "content"),
            "--key", Path.Combine(folder, "keys", "zelvo-pkg.private.pem"), "--out", package], TextWriter.Null, TextWriter.Null, _ => passphrase));
    }

    [Fact]
    public async Task An_install_backs_up_first_switches_the_version_and_reports_every_step()
    {
        var (installer, host, progress, _, env) = await InstallerAsync();
        var plan = await PlanAsync("1.4.1");

        var outcome = await installer.InstallAsync(plan, Ct);

        Assert.Equal(UpdateInstallState.Succeeded, outcome.State);
        Assert.All(outcome.Steps, s => Assert.Equal("Done", s.State));
        Assert.Equal(UpdateInstaller.InstallSteps, outcome.Steps.Select(s => s.Key));
        Assert.Equal(["load", "load", "stop", "backup:zelvo", "backup:keycloak", "migrate", "start"], host.Calls);
        Assert.Contains("ZELVO_VERSION=1.4.1", await File.ReadAllLinesAsync(env, Ct));
        Assert.Contains("OTHER=kept", await File.ReadAllLinesAsync(env, Ct));
        Assert.True(File.Exists(Path.Combine(outcome.BackupPath!, "zelvo.dump")));
        Assert.Contains("ZELVO_VERSION=1.4.0", await File.ReadAllLinesAsync(Path.Combine(outcome.BackupPath!, ".env"), Ct));
        Assert.True(progress.Reports.Count >= 16);
    }

    [Fact]
    public async Task An_install_puts_the_releases_signed_manifest_in_place_and_a_rollback_restores_the_old_one()
    {
        var (installer, _, _, health, env) = await InstallerAsync();
        var release = Path.Combine(Path.GetDirectoryName(env)!, "release");
        Directory.CreateDirectory(release);
        await File.WriteAllTextAsync(Path.Combine(release, "release-manifest.json"), "{\"version\":\"1.4.0\"}", Ct);
        await File.WriteAllTextAsync(Path.Combine(release, "release-manifest.json.sig"), "signature of 1.4.0", Ct);

        var installed = await installer.InstallAsync(await PlanAsync("1.4.1", releaseManifest: true), Ct);
        Assert.Equal(UpdateInstallState.Succeeded, installed.State);
        Assert.Contains("1.4.1", await File.ReadAllTextAsync(Path.Combine(release, "release-manifest.json"), Ct), StringComparison.Ordinal);
        Assert.Equal("signature of 1.4.1", await File.ReadAllTextAsync(Path.Combine(release, "release-manifest.json.sig"), Ct));

        health.FirstFails = true;
        await File.WriteAllLinesAsync(env, ["OTHER=kept", "ZELVO_VERSION=1.4.1"], Ct);
        var failed = await installer.InstallAsync(await PlanAsync("1.4.2", releaseManifest: true), Ct);
        Assert.Equal(UpdateInstallState.RolledBack, failed.State);
        Assert.Equal("signature of 1.4.1", await File.ReadAllTextAsync(Path.Combine(release, "release-manifest.json.sig"), Ct));
    }

    [Fact]
    public async Task A_failed_migration_restores_the_databases_and_the_previous_version()
    {
        var (installer, host, _, _, env) = await InstallerAsync();
        host.Failing.Add("migrate");

        var outcome = await installer.InstallAsync(await PlanAsync("1.4.1"), Ct);

        Assert.Equal(UpdateInstallState.RolledBack, outcome.State);
        Assert.Contains("migrate failed", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(["stop", "backup:zelvo", "backup:keycloak", "migrate", "stop", "restore:zelvo", "restore:keycloak", "start"], host.Calls.Where(c => c != "load"));
        Assert.Contains("ZELVO_VERSION=1.4.0", await File.ReadAllLinesAsync(env, Ct));
        Assert.Equal(("Failed", "Skipped", "Done"), (Step(outcome, "migrate"), Step(outcome, "start"), Step(outcome, "rollback-database")));
        Assert.Equal("Done", Step(outcome, "rollback-health"));
    }

    [Fact]
    public async Task An_unhealthy_new_version_is_rolled_back()
    {
        var (installer, host, _, health, env) = await InstallerAsync();
        health.FirstFails = true;

        var outcome = await installer.InstallAsync(await PlanAsync("1.4.1"), Ct);

        Assert.Equal(UpdateInstallState.RolledBack, outcome.State);
        Assert.Equal("Failed", Step(outcome, "health"));
        Assert.Contains("restore:zelvo", host.Calls);
        Assert.Contains("ZELVO_VERSION=1.4.0", await File.ReadAllLinesAsync(env, Ct));
    }

    [Fact]
    public async Task A_failure_before_the_stop_changes_nothing_and_a_failed_rollback_is_reported()
    {
        var (installer, host, _, _, _) = await InstallerAsync();
        host.Loaded.Clear();
        var early = await installer.InstallAsync(await PlanAsync("1.4.1"), Ct);
        Assert.Equal(UpdateInstallState.RolledBack, early.State);
        Assert.Equal("Failed", Step(early, "load-images"));
        Assert.DoesNotContain("stop", host.Calls);

        var (second, secondHost, _, _, _) = await InstallerAsync();
        secondHost.Failing.Add("migrate");
        secondHost.Failing.Add("restore:zelvo");
        var failed = await second.InstallAsync(await PlanAsync("1.4.1"), Ct);
        Assert.Equal(UpdateInstallState.Failed, failed.State);
        Assert.Equal("Failed", Step(failed, "rollback-database"));
        Assert.DoesNotContain("start", secondHost.Calls);

        // A package that is not the imported one is refused before anything happens.
        var (third, thirdHost, _, _, _) = await InstallerAsync();
        var plan = await PlanAsync("1.4.1");
        var other = await third.InstallAsync(plan with { Sha256 = new string('0', 64) }, Ct);
        Assert.Equal(("Failed", UpdateInstallState.RolledBack), (Step(other, "verify"), other.State));
        Assert.Empty(thirdHost.Calls);
    }

    [Fact]
    public void The_updater_asks_compose_only_for_flags_it_has_and_never_pulls_images()
    {
        var host = new DockerComposeHost(Microsoft.Extensions.Options.Options.Create(new UpdaterOptions { ComposeDirectory = "/srv/zelvo" }));
        var commands = new Dictionary<string, List<string>>
        {
            ["stop"] = host.StopArguments(["api"]),
            ["up"] = host.StartArguments(["api", "web"]),
            ["run"] = host.MigrateArguments(),
            ["exec"] = host.BackupArguments("zelvo"),
        };
        Assert.Equal(["run", "--rm", "--pull", "never", "migrate"], commands["run"].SkipWhile(a => a != "run"));
        Assert.Contains("never", commands["up"]);
        Assert.Equal("zelvo", host.RestoreArguments("zelvo")[^1]);

        // Where Docker Compose is installed (CI, developers), every flag must be one Compose knows.
        System.Diagnostics.Process? probe = null;
        try
        {
            probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", "compose version") { RedirectStandardOutput = true, RedirectStandardError = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        if (probe is null || !probe.WaitForExit(30_000) || probe.ExitCode != 0)
        {
            Assert.Skip("Docker Compose is not installed here.");
        }

        foreach (var (command, arguments) in commands)
        {
            using var help = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", $"compose {command} --help") { RedirectStandardOutput = true })!;
            var text = help.StandardOutput.ReadToEnd();
            help.WaitForExit();
            var flags = arguments.SkipWhile(a => a != command).Skip(1).TakeWhile(a => a.StartsWith('-')).Where(a => a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2);
            Assert.All(flags, flag => Assert.Contains(flag, text, StringComparison.Ordinal));
        }

        probe.Dispose();
    }

    private static string Step(InstallOutcome outcome, string key) => outcome.Steps.Single(s => s.Key == key).State;

    private async Task<InstallPlan> PlanAsync(string version, bool releaseManifest = false)
    {
        var package = await _updates.PackageAsync(_licensing, version, releaseManifest: releaseManifest);
        var sha = (await CheckAsync(package, await DevelopmentAsync(), new Version(1, 4, 0))).Sha256;
        return new InstallPlan(Guid.NewGuid(), package, sha, "1.4.0", version);
    }

    private async Task<(UpdateInstaller Installer, FakeContainerHost Host, MemoryProgress Progress, FakeHealth Health, string Env)> InstallerAsync()
    {
        var compose = Directory.CreateTempSubdirectory("zelvo-compose-").FullName;
        var env = Path.Combine(compose, ".env");
        await File.WriteAllLinesAsync(env, ["OTHER=kept", "ZELVO_VERSION=1.4.0"], Ct);
        await File.WriteAllTextAsync(Path.Combine(compose, "compose.yaml"), "services: {}", Ct);
        var host = new FakeContainerHost();
        host.Loaded["api.tar"] = "zelvo/api:1.4.1";
        host.Loaded["web.tar"] = "zelvo/web:1.4.1";
        var progress = new MemoryProgress();
        var health = new FakeHealth();
        var installer = new UpdateInstaller(host, progress, await DevelopmentAsync(),
            Microsoft.Extensions.Options.Options.Create(new UpdaterOptions { ComposeDirectory = compose }), Microsoft.Extensions.Options.Options.Create(_updates.Options),
            health, TimeProvider.System, NullLogger<UpdateInstaller>.Instance);
        return (installer, host, progress, health, env);
    }

    private async Task<LicenseService> DevelopmentAsync()
    {
        var license = LicensingTestSupport.Development("Development", new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(_licensing.Settings(developmentLicense: true)).Build());
        await license.VerifyAsync(Ct);
        return license;
    }

    private static Task<UpdatePackageCheck> CheckAsync(string package, ILicenseService license, Version? installed = null) =>
        UpdatePackageReader.CheckAsync(package, license, installed ?? new Version(1, 4, 0), UpdateTestSupport.Images, Ct);
}
