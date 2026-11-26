using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Zelvo.Api.Installation;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Licensing;
using Zelvo.Updater;

namespace Zelvo.Api.Tests;

/// <summary>Settings → Updates and the updater service (module 13, task F4) against PostgreSQL.</summary>
/// <remarks>Packages and installs belong to the whole installation, so these tests run one after another and clean up after themselves.</remarks>
public sealed class UpdateApiTests(DatabaseFixture database) : IDisposable
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
    public async Task Packages_are_checked_on_import_scheduled_with_a_notice_and_installed_by_the_updater()
    {
        await using var factory = Factory();
        var tenant = await database.CreateTenantAsync("upd");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var lead = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var visitor = factory.ForTenant(tenant);

        var state = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/updates");
        Assert.Equal("1.4.0", (string?)state["installedVersion"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.GetAsync(new Uri("/api/v1/updates", UriKind.Relative), Ct)).StatusCode);

        // A changed package is refused and not kept.
        var tampered = await _updates.PackageAsync(_licensing, "1.4.1");
        UpdateTestSupport.Change(tampered, "images/web.tar", add: false);
        var refused = await UploadAsync(admin, tampered);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(UpdatePackageProblems.FileMismatch, (string?)(await refused.JsonAsync())["code"]);
        Assert.Empty(Directory.GetFiles(_updates.Options.PackagesDirectory));

        var uploaded = await UploadAsync(admin, await _updates.PackageAsync(_licensing, "1.4.1"));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var package = (await uploaded.JsonAsync()).AsObject();
        Assert.Equal(("1.4.1", "Security", true, "إصلاحات"), ((string?)package["version"], (string?)package["type"], (bool)package["installable"]!, (string?)package["releaseNotes"]!["ar"]));
        var packageId = Guid.Parse((string)package["id"]!);

        // Scheduled for tonight, with the notice from now: everyone sees it.
        var tonight = DateTimeOffset.UtcNow.AddHours(6);
        var scheduled = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/updates/packages/{packageId}/install",
            new { scheduledFor = tonight, noticeFrom = DateTimeOffset.UtcNow.AddMinutes(-1) }, HttpStatusCode.Created);
        Assert.Equal(("Scheduled", 20), ((string?)scheduled["state"], (int)scheduled["expectedMinutes"]!));
        var notice = (await visitor.GetFromJsonAsync<JsonObject>("/api/v1/system/maintenance", Ct))!;
        Assert.Equal((true, false, "1.4.1", 20), ((bool)notice["announced"]!, (bool)notice["inProgress"]!, (string?)notice["version"], (int)notice["expectedMinutes"]!));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/updates/packages/{packageId}/install", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/updates/packages/{packageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/updates/packages/{packageId}/install",
            new { scheduledFor = DateTimeOffset.UtcNow.AddDays(200) })).StatusCode);

        // Cancelled, then installed now; the updater picks it up.
        var cancelled = await admin.SendOkAsync(HttpMethod.Delete, $"/api/v1/updates/installs/{(string)scheduled["id"]!}");
        Assert.Equal("Cancelled", (string?)cancelled["state"]);
        Assert.False((bool)(await visitor.GetFromJsonAsync<JsonObject>("/api/v1/system/maintenance", Ct))!["announced"]!);
        var now = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/updates/packages/{packageId}/install", new { }, HttpStatusCode.Created);

        var host = new FakeContainerHost();
        host.Loaded["api.tar"] = "zelvo/api:1.4.1";
        host.Loaded["web.tar"] = "zelvo/web:1.4.1";
        var outcome = await RunUpdaterAsync(factory, host);
        Assert.Equal(Zelvo.Data.Installation.UpdateInstallState.Succeeded, outcome!.State);

        var install = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/updates/installs/{(string)now["id"]!}");
        Assert.Equal("Succeeded", (string?)install["state"]);
        Assert.Equal(UpdateInstaller.InstallSteps, install["steps"]!.AsArray().Select(s => (string)s!["key"]!));
        var after = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/updates");
        Assert.True((bool)after["updaterOnline"]!);
        Assert.Equal("Installed", (string?)after["packages"]!.AsArray().Single(p => (string?)p!["id"] == packageId.ToString())!["status"]);
        Assert.Contains("\"state\":\"done\"", await File.ReadAllTextAsync(Path.Combine(_updates.Options.StatusDirectory, "status.json"), Ct), StringComparison.Ordinal);

        var audit = await database.AuditAsync(tenant);
        Assert.Empty(new[] { "update.package.refused", "update.package.imported", "update.install.scheduled", "update.install.cancelled", "update.install.started", "update.install.succeeded" }
            .Except(audit.Select(e => e.Action)));

        Assert.Contains(audit, e => e.Action == "update.install.succeeded" && e.ActorType == "System");
    }

    [Fact]
    public async Task Packages_carried_in_on_usb_are_imported_from_the_inbox_and_removed_when_not_wanted()
    {
        await using var factory = Factory();
        var tenant = await database.CreateTenantAsync("updi");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var source = await _updates.PackageAsync(_licensing, "1.4.2");
        File.Move(source, Path.Combine(_updates.Options.InboxDirectory, "zelvo-1.4.2.zpkg"));

        var state = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/updates");
        Assert.Equal(["zelvo-1.4.2.zpkg"], state["inbox"]!.AsArray().Select(f => (string)f!["fileName"]!));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Post, "/api/v1/updates/inbox/..%2Fsecret.zpkg/import")).StatusCode);
        var imported = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/updates/inbox/zelvo-1.4.2.zpkg/import", null, HttpStatusCode.Created);
        Assert.Empty(Directory.GetFiles(_updates.Options.InboxDirectory));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/updates/packages/{(string)imported["id"]!}")).StatusCode);
        Assert.Empty(Directory.GetFiles(_updates.Options.PackagesDirectory));
        Assert.Contains(await database.AuditAsync(tenant), e => e.Action == "update.package.removed");

        // An older package than the installed version is refused.
        File.Move(await _updates.PackageAsync(_licensing, "1.3.9"), Path.Combine(_updates.Options.InboxDirectory, "old.zpkg"));
        var old = await admin.SendAsync(HttpMethod.Post, "/api/v1/updates/inbox/old.zpkg/import");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, old.StatusCode);
        Assert.Equal(UpdatePackageProblems.NotNewer, (string?)(await old.JsonAsync())["code"]);
    }

    private ApiFactory Factory() =>
        _licensing.Apply(ApiFactory.ForDatabase(database), developmentLicense: true).WithServices(services =>
        {
            services.AddSingleton(new InstalledVersionProvider { Current = new Version(1, 4, 0) });
            services.AddSingleton<IOptions<UpdateOptions>>(Options.Create(_updates.Options));
        });

    private async Task<InstallOutcome?> RunUpdaterAsync(ApiFactory factory, FakeContainerHost host)
    {
        var compose = Directory.CreateTempSubdirectory("zelvo-compose-").FullName;
        await File.WriteAllLinesAsync(Path.Combine(compose, ".env"), ["ZELVO_VERSION=1.4.0"], Ct);
        await File.WriteAllTextAsync(Path.Combine(compose, "compose.yaml"), "services: {}", Ct);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZelvoData(database.AppConnectionString);
        await using var provider = services.BuildServiceProvider();
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var license = factory.Services.GetRequiredService<ILicenseService>();
        var updates = Options.Create(_updates.Options);
        var status = new StatusFile(updates, TimeProvider.System);
        var installer = new UpdateInstaller(host, new InstallProgress(dataSource, status), license, Options.Create(new UpdaterOptions { ComposeDirectory = compose }),
            updates, new FakeHealth(), TimeProvider.System, NullLogger<UpdateInstaller>.Instance);
        var updater = new UpdaterService(provider.GetRequiredService<IServiceScopeFactory>(), dataSource, installer, host, Options.Create(new UpdaterOptions()),
            status, NullLogger<UpdaterService>.Instance);
        return await updater.RunOnceAsync(Ct);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string package)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(package));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(package));
        return await client.PostAsync(new Uri("/api/v1/updates/packages", UriKind.Relative), form, Ct);
    }
}
