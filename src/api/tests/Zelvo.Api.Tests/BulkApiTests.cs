using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Bulk import and export of users in CSV and Excel (module 14).</summary>
public sealed class BulkApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task Administrators_export_users_with_the_fields_they_may_see_and_the_export_is_audited(string format)
    {
        var tenant = await database.CreateTenantAsync("bx");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var agent = await database.CreateUserAsync(tenant, "agent");

        using var response = await admin.GetAsync(new Uri($"/api/v1/bulk/users/export?format={format}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith($".{format}", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        var rows = TabularFile.Read(format == "csv" ? TabularFormat.Csv : TabularFormat.Xlsx, await response.Content.ReadAsStreamAsync(Ct), 100);

        Assert.Equal(["email", "displayName", "emiratesId", "roles", "status"], rows[0]);
        Assert.Contains(rows, r => r[0] == agent.Email && r[3] == "agent" && r[4] == "Active");
        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, a => a.Action == "bulk.users.exported" && a.Metadata!.Contains("email", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exports_leave_out_fields_the_caller_may_not_see_and_need_users_read()
    {
        var tenant = await database.CreateTenantAsync("bh");
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "requester");
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync(new Uri("/api/v1/bulk/users/export", UriKind.Relative), Ct)).StatusCode);

        // Agents may read email addresses but not Emirates IDs.
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "agent");
        using var response = await agent.GetAsync(new Uri("/api/v1/bulk/users/export", UriKind.Relative), Ct);
        var rows = TabularFile.Read(TabularFormat.Csv, await response.Content.ReadAsStreamAsync(Ct), 100);
        Assert.Equal(["email", "displayName", "roles", "status"], rows[0]);

        // Without a second factor, the agent's permissions do not apply yet.
        using var noMfa = _factory.ForTenant(tenant).As(await database.CreateUserAsync(tenant, "agent"), mfa: false);
        using var stepUp = await noMfa.GetAsync(new Uri("/api/v1/bulk/users/export", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, stepUp.StatusCode);

        using var unknown = await agent.GetAsync(new Uri("/api/v1/bulk/nothing/export", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var types = await (await agent.GetAsync(new Uri("/api/v1/bulk", UriKind.Relative), Ct)).JsonAsync();
        Assert.True((bool)types[0]!["canExport"]!);
        Assert.False((bool)types[0]!["canImport"]!);
    }

    [Fact]
    public async Task A_check_lists_every_problem_by_row_and_column_and_changes_nothing()
    {
        var tenant = await database.CreateTenantAsync("bc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var existing = await database.CreateUserAsync(tenant, "agent");
        var csv = $"""
            email,displayName,emiratesId,roles,status
            new@example.ae,New Person,,approver,
            not-an-email,,,,
            {existing.Email},Renamed,784-1990-123,agent;nobody,Sleeping
            new@example.ae,,,,
            """;

        using var response = await ImportAsync(admin, csv, "users.csv", dryRun: true);
        var result = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((bool)result["dryRun"]!);
        Assert.False((bool)result["applied"]!);
        var errors = result["errors"]!.AsArray().Select(e => ((int)e!["row"]!, (string?)e["column"], (string)e["code"]!)).ToList();
        Assert.Contains((3, "email", "email-invalid"), errors);
        Assert.Contains((4, "emiratesId", "emirates-id-invalid"), errors);
        Assert.Contains((4, "roles", "role-unknown"), errors);
        Assert.Contains((4, "status", "status-invalid"), errors);
        Assert.Contains((5, "email", "email-duplicate"), errors);

        using var applied = await ImportAsync(admin, csv, "users.csv", dryRun: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, applied.StatusCode);
        Assert.DoesNotContain(_factory.Keycloak.ImportedUsers.Keys, k => k.Realm == $"zelvo-{tenant.Slug}");
        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal(2, await db.Users.CountAsync(Ct));
    }

    [Fact]
    public async Task An_import_creates_new_people_in_keycloak_and_updates_existing_users_together()
    {
        var tenant = await database.CreateTenantAsync("bi");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var existing = await database.CreateUserAsync(tenant, "requester");
        var csv = $"""
            Email,DisplayName,EmiratesId,Roles,Status
            sara@example.ae,Sara Al Mansoori,784-1990-1234567-1,agent;approver,
            {existing.Email.ToUpperInvariant()},'=Renamed,,agent,Disabled
            """;

        using var check = await ImportAsync(admin, csv, "users.csv", dryRun: true);
        var preview = await check.JsonAsync();
        Assert.Equal(1, (int?)preview["created"]);
        Assert.Equal(1, (int?)preview["updated"]);

        using var response = await ImportAsync(admin, csv, "users.csv", dryRun: false);
        var result = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((bool)result["applied"]!);

        var realm = $"zelvo-{tenant.Slug}";
        var (subject, firstName, lastName) = _factory.Keycloak.ImportedUsers[(realm, "sara@example.ae")];
        Assert.Equal(("Sara", "Al Mansoori"), (firstName, lastName));
        Assert.Contains(_factory.Keycloak.EmiratesIdCalls, c => c.Realm == realm && c.Subject == subject && c.EmiratesId == "784199012345671");

        await using var db = TestData.AppContext(database, tenant);
        var sara = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).SingleAsync(u => u.Subject == subject, Ct);
        Assert.Equal("Sara Al Mansoori", sara.DisplayName);
        Assert.Equal(["agent", "approver"], sara.Roles.Select(r => r.Role!.Key).Order(StringComparer.Ordinal));
        var updated = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).SingleAsync(u => u.Id == existing.Id, Ct);
        Assert.Equal("=Renamed", updated.DisplayName);
        Assert.Equal(UserStatus.Disabled, updated.Status);
        Assert.Equal(["agent"], updated.Roles.Select(r => r.Role!.Key));

        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, a => a.Action == "bulk.users.imported");
        Assert.Contains(audit, a => a.Action == "user_account.created");
    }

    [Fact]
    public async Task Imports_keep_an_active_tenant_administrator_and_nobody_disables_themselves()
    {
        var tenant = await database.CreateTenantAsync("bg");
        var adminUser = await database.CreateUserAsync(tenant, "tenant-admin");
        using var admin = _factory.ForTenant(tenant).As(adminUser);

        using var demote = await ImportAsync(admin, $"email,roles\n{adminUser.Email},agent\n", "users.csv", dryRun: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, demote.StatusCode);
        Assert.Contains((await demote.JsonAsync())["errors"]!.AsArray(), e => (string?)e!["code"] == "last-tenant-admin");

        using var self = await ImportAsync(admin, $"email,status\n{adminUser.Email},Disabled\n", "users.csv", dryRun: true);
        Assert.Contains((await self.JsonAsync())["errors"]!.AsArray(), e => (string?)e!["code"] == "cannot-disable-self");
    }

    [Fact]
    public async Task Excel_files_are_imported_and_bad_files_are_refused()
    {
        var tenant = await database.CreateTenantAsync("bf");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");

        var workbook = TabularFile.Write(TabularFormat.Xlsx, ["email", "displayName"], [["omar@example.ae", "عمر"]]);
        using var xlsx = await ImportAsync(admin, workbook, "people.xlsx", dryRun: false);
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Equal(1, (int?)(await xlsx.JsonAsync())["created"]);

        using var wrongType = await ImportAsync(admin, "email\nx@example.ae\n", "people.txt", dryRun: true);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        using var noEmail = await ImportAsync(admin, "displayName\nX\n", "people.csv", dryRun: true);
        Assert.Contains((await noEmail.JsonAsync())["errors"]!.AsArray(), e => (string?)e!["code"] == "column-missing");
        using var notExcel = await ImportAsync(admin, "garbage", "people.xlsx", dryRun: true);
        Assert.Equal(HttpStatusCode.BadRequest, notExcel.StatusCode);

        using var template = await admin.GetAsync(new Uri("/api/v1/bulk/users/template?format=xlsx", UriKind.Relative), Ct);
        var header = TabularFile.Read(TabularFormat.Xlsx, await template.Content.ReadAsStreamAsync(Ct), 10);
        Assert.Equal(["email", "displayName", "emiratesId", "roles", "status"], header[0]);
    }

    [Fact]
    public async Task Imports_need_users_manage_and_field_rules_apply()
    {
        var tenant = await database.CreateTenantAsync("bp");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "agent");
        using var refused = await ImportAsync(agent, "email\nx@example.ae\n", "users.csv", dryRun: true);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string csv, string fileName, bool dryRun) =>
        ImportAsync(client, Encoding.UTF8.GetBytes(csv.Replace("\r\n", "\n", StringComparison.Ordinal)), fileName, dryRun);

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, byte[] content, string fileName, bool dryRun)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync(new Uri($"/api/v1/bulk/users/import?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative), form, Ct);
    }
}
