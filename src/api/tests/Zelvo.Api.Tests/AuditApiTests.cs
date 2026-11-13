using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Security;

namespace Zelvo.Api.Tests;

public sealed class AuditApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Reading_the_audit_log_needs_an_auditor_or_admin()
    {
        var tenant = await database.CreateTenantAsync();
        using var anonymous = _factory.ForTenant(tenant);
        using var agent = _factory.ForTenant(tenant).SignedIn(tenant, "agent");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Events(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(Events(), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_auditor_pages_through_entries_newest_first_and_the_view_is_audited()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.Auditor);

        var first = await client.GetFromJsonAsync<PageBody>(Events("?page=1&pageSize=1"), Ct);
        var second = await client.GetFromJsonAsync<PageBody>(Events("?page=1&pageSize=10"), Ct);

        Assert.Single(first!.Items);
        Assert.Equal("tenant.created", first.Items[0].Action);
        Assert.Equal(1, first.TotalCount);
        Assert.Equal(1, first.Page);
        Assert.Equal(1, first.PageSize);

        // The first read is itself in the log now, newest first.
        Assert.Equal(2, second!.TotalCount);
        Assert.Equal("audit.events.viewed", second.Items[0].Action);
        Assert.Equal("View", second.Items[0].Category);
        Assert.Equal("user-42", second.Items[0].Actor.Id);
        Assert.Equal(64, second.Items[0].Hash.Length);
        Assert.Equal(second.Items[1].Hash, second.Items[0].PreviousHash);
    }

    [Fact]
    public async Task Entries_can_be_filtered()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.TenantAdmin);
        await client.GetAsync(Events(), Ct);

        var created = await client.GetFromJsonAsync<PageBody>(Events($"?category=Create&entityType=tenant&entityId={tenant.Id}"), Ct);

        Assert.Equal("tenant.created", Assert.Single(created!.Items).Action);
    }

    [Theory]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=0")]
    [InlineData("?page=0")]
    public async Task Out_of_range_paging_is_a_validation_problem(string query)
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.Auditor);

        var response = await client.GetAsync(Events(query), Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", problem!.Code);
    }

    [Fact]
    public async Task Verification_reports_an_intact_chain_and_is_audited()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant).SignedIn(tenant, ZelvoRoles.Auditor);

        var result = await client.GetFromJsonAsync<VerificationBody>(new Uri("/api/v1/audit/verification", UriKind.Relative), Ct);

        Assert.True(result!.Valid);
        Assert.Equal(1, result.EventsChecked);
        Assert.Equal(1, result.HeadSequence);
        Assert.Equal(64, result.HeadHash!.Length);
        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "audit.chain.verified", Ct));
    }

    private static Uri Events(string query = "") => new($"/api/v1/audit/events{query}", UriKind.Relative);

    private sealed record PageBody(List<EventBody> Items, int Page, int PageSize, long TotalCount);

    private sealed record EventBody(long Sequence, string Category, string Action, ActorBody Actor, string Hash, string? PreviousHash);

    private sealed record ActorBody(string Type, string? Id, string? Name);

    private sealed record VerificationBody(bool Valid, long EventsChecked, long? FirstInvalidSequence, long? HeadSequence, string? HeadHash);
}
