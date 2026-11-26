using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Licensing;

namespace Zelvo.Api.Tests;

/// <summary>Settings → License, activation mode and module gating (module 12, task F3) against PostgreSQL.</summary>
public sealed class LicenseApiTests(DatabaseFixture database) : IDisposable
{
    private readonly LicensingTestSupport _support = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _support.Dispose();

    [Fact]
    public async Task Release_components_report_their_integrity_to_administrators()
    {
        await using var factory = ApiFactory.ForDatabase(database);
        var tenant = await database.CreateTenantAsync("rel");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var lead = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        var store = factory.Services.GetRequiredService<IReleaseComponentStore>();

        // The application role reports (and replaces) a component's check; it cannot remove one.
        await store.RecordAsync(new ReleaseComponentReport("worker", "Modified", "1.4.0", "1.4.0", "files differ", DateTimeOffset.UtcNow), Ct);
        await store.RecordAsync(new ReleaseComponentReport("worker", "Verified", "1.4.0", "1.4.0", "ok", DateTimeOffset.UtcNow), Ct);
        await using (var connection = new NpgsqlConnection(database.AppConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var delete = new NpgsqlCommand("DELETE FROM installation_components", connection);
            await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct));
        }

        var components = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/license/components");
        Assert.Equal(("NoManifest", false), ((string?)components["manifestStatus"], (bool)components["releaseBuild"]!));
        Assert.Null(components["release"]);
        var worker = components["components"]!.AsArray().Single(c => (string?)c!["component"] == "worker")!;
        Assert.Equal(("Verified", "1.4.0"), ((string?)worker["status"], (string?)worker["release"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.GetAsync(new Uri("/api/v1/license/components", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_installation_waits_for_activation_and_is_activated_with_a_license_for_its_servers()
    {
        await using var factory = _support.Apply(ApiFactory.ForDatabase(database));
        var tenant = await database.CreateTenantAsync("lic");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var lead = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);

        // Activation mode: only what the activation needs answers.
        var info = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/system/info", Ct))!;
        Assert.Equal((false, true), ((bool)info["activated"]!, (bool)info["activationRequired"]!));
        Assert.Empty(info["modules"]!.AsArray());
        var blocked = await admin.GetAsync(new Uri("/api/v1/incidents", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("urn:zelvo:problem:license-required", (string?)(await blocked.JsonAsync())["type"]);
        var me = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/me");
        var permissions = me["permissions"]!.AsArray().Select(p => (string)p!).ToList();
        Assert.Contains("installation.manage", permissions);
        Assert.DoesNotContain("incidents.read", permissions);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/api/v1/setup", UriKind.Relative), Ct)).StatusCode);

        var license = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/license");
        Assert.Equal((false, true, "not-activated"), ((bool)license["activated"]!, (bool)license["activationRequired"]!, (string?)license["problem"]));
        Assert.Equal(["installationId", "databaseId", "machineId", "clusterId", "domain"], license["fingerprint"]!.AsArray().Select(p => (string)p!["key"]!));
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.GetAsync(new Uri("/api/v1/license", UriKind.Relative), Ct)).StatusCode);

        // The activation request goes to Zelvo, which returns a license for these servers.
        using var requested = await admin.PostAsync(new Uri("/api/v1/license/activation-request", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        Assert.StartsWith("zelvo-activation-", requested.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        var request = ActivationRequestFile.Parse(await requested.Content.ReadAsByteArrayAsync(Ct))!;
        Assert.Equal(_support.Store.InstallationId, request.Fingerprint.InstallationId);

        var tampered = await ImportAsync(admin, Encoding.UTF8.GetBytes("{\"format\":\"zelvo-signed/1\"}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tampered.StatusCode);
        Assert.Equal("urn:zelvo:problem:license-format-invalid", (string?)(await tampered.JsonAsync())["type"]);

        var modules = LicensedModules.Phase1.Where(m => m != LicensedModules.Payments).ToList();
        var imported = await ImportAsync(admin, _support.License(request, modules));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var activated = (await imported.JsonAsync()).AsObject();
        Assert.Equal((true, "Production", "Example Authority", "هيئة المثال", "1.4"),
            ((bool)activated["activated"]!, (string?)activated["kind"], (string?)activated["licensedTo"]!["en"], (string?)activated["licensedTo"]!["ar"], (string?)activated["licensedVersion"]));
        Assert.False((bool)activated["modules"]!.AsArray().Single(m => (string?)m!["key"] == LicensedModules.Payments)!["licensed"]!);
        Assert.All(activated["fingerprint"]!.AsArray().Where(p => p!["matches"] is not null), p => Assert.True((bool)p!["matches"]!));
        Assert.Single(activated["files"]!.AsArray());
        Assert.DoesNotContain("watermark", activated.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // Now the licensed modules answer, and the one left out does not.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/api/v1/incidents", UriKind.Relative), Ct)).StatusCode);
        var payments = await admin.GetAsync(new Uri("/api/v1/payments", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, payments.StatusCode);
        Assert.Equal("urn:zelvo:problem:module-not-licensed", (string?)(await payments.JsonAsync())["type"]);
        var after = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/me"))["permissions"]!.AsArray().Select(p => (string)p!).ToList();
        Assert.Contains("incidents.read", after);
        Assert.DoesNotContain("payments.read", after);
        Assert.DoesNotContain(LicensedModules.Payments, (await admin.GetFromJsonAsync<JsonObject>("/api/v1/system/info", Ct))!["modules"]!.AsArray().Select(m => (string?)m));

        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, e => e.Action == "license.activation-requested");
        Assert.Contains(audit, e => e.Action == "license.import-refused");
        Assert.Contains(audit, e => e.Action == "license.imported");
    }

    [Fact]
    public async Task The_setup_wizard_starts_with_the_license_step()
    {
        await using var factory = ApiFactory.ForDatabase(database);
        var tenant = await database.CreateTenantAsync("lics");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var setup = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/setup");

        var first = setup["steps"]![0]!;
        Assert.Equal(("license", true), ((string?)first["key"], (bool)first["configured"]!));
    }

    [Fact]
    public async Task Only_the_operator_tenant_manages_the_installation()
    {
        await using var factory = ApiFactory.ForDatabase(database).WithSetting("Installation:OperatorTenant", "operator-elsewhere");
        var tenant = await database.CreateTenantAsync("licop");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(new Uri("/api/v1/license", UriKind.Relative), Ct)).StatusCode);
        var permissions = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/me"))["permissions"]!.AsArray().Select(p => (string)p!);
        Assert.DoesNotContain("installation.manage", permissions);
    }

    [Fact]
    public async Task The_installation_tables_keep_licenses_and_the_database_identity_for_the_application_role()
    {
        // A database of its own: licenses are installation-wide, so other tests must not see them.
        var name = $"lic_{Guid.NewGuid():N}"[..20];
        await database.ExecuteAsOwnerAsync($"CREATE DATABASE {name}");
        var owner = new NpgsqlConnectionStringBuilder(database.OwnerConnectionString) { Database = name }.ConnectionString;
        var app = new NpgsqlConnectionStringBuilder(database.AppConnectionString) { Database = name }.ConnectionString;
        await using (var connection = new NpgsqlConnection(owner))
        {
            await connection.OpenAsync(Ct);
            var appRole = new NpgsqlConnectionStringBuilder(database.AppConnectionString).Username;
            await using var grant = new NpgsqlCommand($"""
                GRANT USAGE ON SCHEMA public TO {appRole};
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {appRole};
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {appRole};
                """, connection);
            await grant.ExecuteNonQueryAsync(Ct);
        }

        await DatabaseMigrator.MigrateAsync(owner, Ct);
        var store = new PostgresInstallationStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = app }).Build());

        var (installationId, databaseId) = await store.GetIdentityAsync(Ct);
        Assert.True(Guid.TryParse(installationId, out _));
        Assert.True(ulong.TryParse(databaseId, out _));
        Assert.Empty(await store.ListAsync(Ct));

        var content = Encoding.UTF8.GetBytes("{}");
        await store.AddAsync("License", "LIC-1", content, Watermark.Create(), new LicenseImportContext(Guid.NewGuid(), Guid.NewGuid(), "a.json"), Ct);
        await store.AddAsync("Upgrade", "UPG-1", content, null, new LicenseImportContext(null, null, null), Ct);
        await store.AddAsync("License", "LIC-2", content, Watermark.Create(), new LicenseImportContext(null, null, "b.json"), Ct);
        var files = await store.ListAsync(Ct);
        Assert.Equal([("License", "LIC-2")], files.Select(f => (f.FileType, f.LicenseId)));

        // The application role can neither remove license files nor replace the installation id.
        await using var appConnection = new NpgsqlConnection(app);
        await appConnection.OpenAsync(Ct);
        foreach (var sql in new[] { "DELETE FROM installation_licenses", "DELETE FROM installation", "INSERT INTO installation (singleton, id) VALUES (false, gen_random_uuid())" })
        {
            await using var command = new NpgsqlCommand(sql, appConnection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        NpgsqlConnection.ClearAllPools();
        await database.ExecuteAsOwnerAsync($"DROP DATABASE {name} WITH (FORCE)");
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", "license.json");
        return await client.PostAsync(new Uri("/api/v1/license/import", UriKind.Relative), form, Ct);
    }
}
