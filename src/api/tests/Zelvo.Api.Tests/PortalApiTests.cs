using System.Net;
using Zelvo.Data.Identity;
using Zelvo.Search;

namespace Zelvo.Api.Tests;

/// <summary>"My tickets" of the self-service portal (module 4, task D2) against PostgreSQL.</summary>
public sealed class PortalApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task My_tickets_lists_every_record_type_the_caller_asked_for_with_progress_and_the_portal_page()
    {
        var tenant = await database.CreateTenantAsync("portal");
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var other = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var incident = (await requester.CreateAsync(new { title = "Printer jammed" })).Id();
        var complaint = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/customer-voice", new { type = "Complaint", title = "Long wait" }, HttpStatusCode.Created)).Id();
        await other.CreateAsync(new { title = "Not mine" });

        var all = (await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray();
        Assert.Equal([complaint.ToString(), incident.ToString()], all.Select(t => (string?)t!["id"]));
        var first = all[0]!;
        Assert.Equal(("Complaint", $"/my-voice/{complaint}", true), ((string?)first["type"], (string?)first["path"], (bool)first["open"]!));
        Assert.Equal(["received", "handling", "resolved", "closed"], first["progress"]!["steps"]!.AsArray().Select(s => (string?)s!["key"]));
        Assert.Equal(0, (int)first["progress"]!["current"]!);
        Assert.Equal($"/my-incidents/{incident}", (string?)all[1]!["path"]);
        Assert.Null(first["assignee"]);

        // Resolved tickets leave the open list; the type filter narrows it.
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/transitions", new { state = "Resolved", resolutionNotes = "Cleared." });
        var open = (await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets?open=true"))["items"]!.AsArray();
        Assert.Equal([complaint.ToString()], open.Select(t => (string?)t!["id"]));
        var incidents = (await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets?type=Incident"))["items"]!.AsArray();
        Assert.Equal(("Resolved", 2), ((string?)incidents.Single()!["state"], (int)incidents.Single()!["progress"]!["current"]!));

        using var visitor = _factory.ForTenant(tenant);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.SendAsync(HttpMethod.Get, "/api/v1/me/tickets")).StatusCode);
        using var noQuery = await requester.SendAsync(HttpMethod.Get, "/api/v1/portal/search?q=");
        Assert.Equal(HttpStatusCode.BadRequest, noQuery.StatusCode);
    }
}

/// <summary>The portal's "How can we help?" search (module 4, task D2) against OpenSearch.</summary>
[Collection(SearchTestGroup.Name)]
public sealed class PortalSearchTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly SearchFixture _search;
    private readonly ApiFactory _factory;

    public PortalSearchTests(DatabaseFixture database, SearchFixture search)
    {
        _database = database;
        _search = search;
        _factory = search.Api();
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static SearchDocument Doc(string type, string id, string en, string ar, params string[] readers) => new()
    {
        Type = type,
        Id = id,
        Title = new(en, ar),
        Readers = readers.Length == 0 ? [SearchReaders.Public] : readers,
        Url = type == "article" ? $"/help/articles/{id}" : null,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task One_search_finds_services_articles_and_only_the_callers_own_tickets()
    {
        var tenant = await _database.CreateTenantAsync("portal-search");
        var service = Guid.NewGuid().ToString();
        var article = Guid.NewGuid().ToString();
        await _search.IndexAsync(tenant,
            Doc("service", service, "Printer toner replacement", "استبدال حبر الطابعة"),
            Doc("article", article, "How to clear a printer jam", "طريقة إزالة انحشار الورق في الطابعة"),
            Doc("article", Guid.NewGuid().ToString(), "Printer rota for agents", "جدول الطابعات للموظفين", SearchReaders.Role(BuiltInRoles.Agent)));
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);
        var mine = (await requester.CreateAsync(new { title = "Printer on floor 3 is jammed" })).Id();
        await (await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Requester)).CreateAsync(new { title = "Printer in the lobby" });
        await _search.WaitUntilIndexedAsync(tenant);

        var found = await requester.SendOkAsync(HttpMethod.Get, "/api/v1/portal/search?q=printer");
        Assert.Equal([$"/catalog/{service}"], found["services"]!.AsArray().Select(h => (string?)h!["path"]));
        Assert.Equal([$"/help/articles/{article}"], found["articles"]!.AsArray().Select(h => (string?)h!["path"]));
        Assert.Equal([$"/my-incidents/{mine}"], found["tickets"]!.AsArray().Select(t => (string?)t!["path"]));

        // Staff find only their own tickets here too; visitors find no tickets.
        Assert.Empty((await agent.SendOkAsync(HttpMethod.Get, "/api/v1/portal/search?q=printer"))["tickets"]!.AsArray());
        var visitor = await _factory.ForTenant(tenant).SendOkAsync(HttpMethod.Get, $"/api/v1/portal/search?q={Uri.EscapeDataString("الطابعة")}");
        Assert.Single(visitor["articles"]!.AsArray());
        Assert.Empty(visitor["tickets"]!.AsArray());
    }
}
