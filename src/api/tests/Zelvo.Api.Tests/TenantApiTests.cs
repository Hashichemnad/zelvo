using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Security;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

public sealed class TenantApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri TenantUri = new("/api/v1/tenant", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Returns_the_tenant_named_in_the_header()
    {
        var tenant = await database.CreateTenantAsync(nameEn: "Ajman Municipality", nameAr: null);
        using var client = _factory.ForTenant(tenant);

        var body = await client.GetFromJsonAsync<TenantBody>(TenantUri, Ct);

        Assert.Equal(tenant.Id, body!.Id);
        Assert.Equal(tenant.Slug, body.Slug);
        Assert.Equal("Ajman Municipality", body.Name.En);
        Assert.Null(body.Name.Ar);
        Assert.Equal(["ar"], body.Name.Missing);
        Assert.Equal("Active", body.Status);
    }

    [Theory]
    [InlineData("does-not-exist", HttpStatusCode.NotFound, "tenant-not-found")]
    [InlineData("Not A Slug!", HttpStatusCode.BadRequest, "tenant-invalid")]
    public async Task Unknown_or_invalid_tenants_are_problems(string slug, HttpStatusCode status, string code)
    {
        using var client = _factory.ForTenant(slug);

        var response = await client.GetAsync(TenantUri, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, problem!.Code);
        Assert.Equal($"urn:zelvo:problem:{code}", problem.Type);
        Assert.False(string.IsNullOrEmpty(problem.TraceId));
    }

    [Fact]
    public async Task A_tenant_endpoint_without_a_tenant_is_a_problem()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(TenantUri, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tenant-required", problem!.Code);
    }

    [Fact]
    public async Task A_suspended_tenant_is_refused()
    {
        var tenant = await database.CreateTenantAsync("s");
        await database.ExecuteAsOwnerAsync("UPDATE tenants SET status = 'Suspended' WHERE id = @id", ("id", tenant.Id));
        using var client = _factory.ForTenant(tenant);

        var response = await client.GetAsync(TenantUri, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant-suspended", problem!.Code);
    }

    [Fact]
    public async Task Updating_needs_sign_in()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant);

        var response = await client.PatchAsJsonAsync(TenantUri, new { name = new { en = "X" } }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Updating_needs_the_admin_role()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.Auditor);

        var response = await client.PatchAsJsonAsync(TenantUri, new { name = new { en = "X" } }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_of_another_tenant_cannot_switch_tenants_with_the_header()
    {
        var home = await database.CreateTenantAsync("home");
        var target = await database.CreateTenantAsync("target", nameEn: "Untouched");
        using var client = _factory.ForTenant(target).SignedIn(home, ZelvoRoles.TenantAdmin);

        var response = await client.PatchAsJsonAsync(TenantUri, new { name = new { en = "Taken over" } }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = TestData.AppContext(database, target);
        Assert.Equal("Untouched", (await db.Tenants.SingleAsync(Ct)).Name.En);
    }

    [Fact]
    public async Task An_admin_updates_the_name_and_the_change_is_audited_with_the_user()
    {
        var tenant = await database.CreateTenantAsync(nameEn: "Before", nameAr: "قبل");
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.TenantAdmin);

        var response = await client.PatchAsJsonAsync(TenantUri, new { name = new { en = "After", ar = "بعد" } }, Ct);
        var body = await response.Content.ReadFromJsonAsync<TenantBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("After", body!.Name.En);
        Assert.Equal("بعد", body.Name.Ar);
        Assert.Empty(body.Name.Missing);

        await using var db = TestData.AppContext(database, tenant);
        var entry = await db.AuditEvents.OrderByDescending(e => e.Sequence).FirstAsync(Ct);
        Assert.Equal("tenant.updated", entry.Action);
        Assert.Equal("user-42", entry.ActorId);
        Assert.Contains("\"name.ar\"", entry.Changes, StringComparison.Ordinal);
        Assert.Contains("correlationId", entry.Metadata, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":{}}""")]
    [InlineData("""{"name":{"en":"   ","ar":""}}""")]
    public async Task Invalid_updates_are_validation_problems(string json)
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.TenantAdmin);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PatchAsync(TenantUri, content, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", problem!.Code);
        Assert.NotEmpty(problem.Errors!);
    }

    [Fact]
    public async Task A_name_longer_than_the_column_is_a_validation_problem()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.TenantAdmin);

        var response = await client.PatchAsJsonAsync(TenantUri, new { name = new { en = new string('x', Tenant.NameMaxLength + 1) } }, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", problem!.Code);
        Assert.Contains("name", problem.Errors!.Keys);
    }

    private sealed record TenantBody(Guid Id, string Slug, NameBody Name, string Status);

    private sealed record NameBody(string? En, string? Ar, List<string> Missing);
}
