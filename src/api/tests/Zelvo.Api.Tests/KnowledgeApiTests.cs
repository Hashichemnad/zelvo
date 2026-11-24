using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Identity;
using Zelvo.Data.Auditing;
using Zelvo.Data.Knowledge;

namespace Zelvo.Api.Tests;

/// <summary>Knowledge base (module 7, task D1) against PostgreSQL.</summary>
public sealed class KnowledgeApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri Articles = new("/api/v1/knowledge/articles", UriKind.Relative);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static Uri Of(Guid id, string? path = null) => new($"/api/v1/knowledge/articles/{id}{(path is null ? "" : "/" + path)}", UriKind.Relative);

    private static Uri Published(Guid id, string? path = null) => new($"/api/v1/knowledge/published/{id}{(path is null ? "" : "/" + path)}", UriKind.Relative);

    private static object Content(string en, string? ar = null, string visibility = "External") => new
    {
        title = new { en, ar },
        summary = new { en = "Summary of " + en },
        body = new { en = $"## {en}\n\nSteps for **{en}**.", ar = ar is null ? null : $"خطوات {ar}" },
        keywords = new[] { "vpn" },
        visibility,
    };

    private static async Task<(Guid Id, JsonNode Detail)> CreateAsync(HttpClient author, object content)
    {
        using var response = await author.PostAsJsonAsync(Articles, content, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var detail = await response.JsonAsync();
        return (Guid.Parse((string)detail["article"]!["id"]!), detail);
    }

    private static async Task<JsonNode> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.JsonAsync();
    }

    private static async Task PublishAsync(HttpClient author, HttpClient reviewer, Guid id)
    {
        await OkAsync(await author.PostAsync(Of(id, "submit"), null, Ct));
        await OkAsync(await reviewer.PostAsJsonAsync(Of(id, "approve"), new { comment = "Looks good" }, Ct));
    }

    [Fact]
    public async Task An_article_goes_from_draft_through_review_to_published_and_a_change_is_reviewed_again()
    {
        var tenant = await database.CreateTenantAsync("kl");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var (id, created) = await CreateAsync(agent, Content("Connect to the VPN", "الاتصال بالشبكة"));
        Assert.Equal(("KB-000001", "Draft"), ((string?)created["article"]!["number"], (string?)created["article"]!["state"]));
        Assert.True((bool)created["actions"]!["submit"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await requester.GetAsync(Published(id), Ct)).StatusCode);

        var submitted = await OkAsync(await agent.PostAsync(Of(id, "submit"), null, Ct));
        Assert.Equal("InReview", (string?)submitted["article"]!["state"]);
        using var whileInReview = await agent.PutAsJsonAsync(Of(id, "draft"), Content("Changed"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, whileInReview.StatusCode);

        // Agents cannot publish; the lead can, as someone other than the author.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync(Of(id, "approve"), new { }, Ct)).StatusCode);
        var queue = await OkAsync(await lead.GetAsync(new Uri("/api/v1/knowledge/review-queue", UriKind.Relative), Ct));
        Assert.True((bool)queue.AsArray().Single()!["canReview"]!);
        var published = await OkAsync(await lead.PostAsJsonAsync(Of(id, "approve"), new { comment = "Clear" }, Ct));
        Assert.Equal("Published", (string?)published["article"]!["state"]);
        Assert.Equal(1, (int?)published["published"]!["number"]);

        var reading = await OkAsync(await requester.GetAsync(Published(id), Ct));
        Assert.Equal("Connect to the VPN", (string?)reading["article"]!["title"]!["en"]);
        Assert.Contains("**Connect to the VPN**", (string?)reading["body"]!["en"], StringComparison.Ordinal);

        // A change makes version 2; readers keep version 1 until it is approved.
        var draft = await OkAsync(await agent.PutAsJsonAsync(Of(id, "draft"), Content("Connect to the company VPN"), Ct));
        Assert.Equal((2, "Draft"), ((int?)draft["working"]!["number"], (string?)draft["working"]!["state"]));
        Assert.Equal("Published", (string?)draft["article"]!["state"]);
        Assert.Equal("Connect to the VPN", (string?)(await OkAsync(await requester.GetAsync(Published(id), Ct)))["article"]!["title"]!["en"]);
        await OkAsync(await agent.PostAsync(Of(id, "submit"), null, Ct));
        using var noComment = await lead.PostAsJsonAsync(Of(id, "reject"), new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);
        var sentBack = await OkAsync(await lead.PostAsJsonAsync(Of(id, "reject"), new { comment = "Name the client app." }, Ct));
        Assert.Equal(("Draft", "Name the client app."), ((string?)sentBack["working"]!["state"], (string?)sentBack["working"]!["reviewComment"]));

        await PublishAsync(agent, lead, id);
        Assert.Equal("Connect to the company VPN", (string?)(await OkAsync(await requester.GetAsync(Published(id), Ct)))["article"]!["title"]!["en"]);
        var versions = (await OkAsync(await agent.GetAsync(Of(id, "versions"), Ct))).AsArray();
        Assert.Equal(["Published", "Superseded"], versions.Select(v => (string?)v!["state"]));
        Assert.Equal("Clear", (string?)versions[1]!["reviewComment"]);
    }

    [Fact]
    public async Task Someone_other_than_the_author_approves_even_a_publisher_s_own_article()
    {
        var tenant = await database.CreateTenantAsync("k4");
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var otherLead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        var (id, _) = await CreateAsync(lead, Content("Printer setup"));
        await OkAsync(await lead.PostAsync(Of(id, "submit"), null, Ct));

        using var own = await lead.PostAsJsonAsync(Of(id, "approve"), new { }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal("own-article", (await own.ProblemAsync()).Code);
        var detail = await OkAsync(await lead.GetAsync(Of(id), Ct));
        Assert.False((bool)detail["actions"]!["review"]!);
        await OkAsync(await otherLead.PostAsJsonAsync(Of(id, "approve"), new { }, Ct));
    }

    [Fact]
    public async Task Internal_articles_are_for_staff_and_visitors_read_external_ones_only_when_the_tenant_allows_it()
    {
        var tenant = await database.CreateTenantAsync("kv");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var visitor = _factory.ForTenant(tenant);
        var (external, _) = await CreateAsync(agent, Content("Reset your password"));
        var (internalId, _) = await CreateAsync(agent, Content("Restart the mail relay", visibility: "Internal"));
        await PublishAsync(agent, lead, external);
        await PublishAsync(agent, lead, internalId);

        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(Published(external), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(Published(internalId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await requester.GetAsync(Published(internalId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync(Published(internalId), Ct)).StatusCode);
        var forRequester = await OkAsync(await requester.GetAsync(new Uri("/api/v1/knowledge/published", UriKind.Relative), Ct));
        Assert.Equal([external.ToString()], forRequester["items"]!.AsArray().Select(a => (string?)a!["id"]));
        var forAgent = await OkAsync(await agent.GetAsync(new Uri("/api/v1/knowledge/published", UriKind.Relative), Ct));
        Assert.Equal(2, (int?)forAgent["totalCount"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync(Articles, Ct)).StatusCode);

        await using (var db = TestData.AppContext(database, tenant))
        {
            var readers = await ReadersAsync(db, external);
            Assert.Equal(["public"], readers);
            Assert.DoesNotContain("public", await ReadersAsync(db, internalId));
            Assert.Contains("role:agent", await ReadersAsync(db, internalId));
        }

        using var off = await admin.PutAsJsonAsync(new Uri("/api/v1/settings/knowledge", UriKind.Relative), new { publicAccess = false, numberPrefix = "HELP" }, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        using var refused = await visitor.GetAsync(Published(external), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("sign-in-required", (await refused.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.OK, (await requester.GetAsync(Published(external), Ct)).StatusCode);
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.Equal(["users"], await ReadersAsync(db, external));
        }

        var (next, _) = await CreateAsync(agent, Content("Next"));
        Assert.Equal("HELP-000003", (string?)(await OkAsync(await agent.GetAsync(Of(next), Ct)))["article"]!["number"]);
    }

    [Fact]
    public async Task Views_count_once_per_viewer_and_day_and_feedback_once_per_person_or_device()
    {
        var tenant = await database.CreateTenantAsync("kf");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var visitor = _factory.ForTenant(tenant);
        var (id, _) = await CreateAsync(agent, Content("Book a meeting room"));
        await PublishAsync(agent, lead, id);
        var device = Guid.NewGuid().ToString();

        Assert.True((bool)(await OkAsync(await visitor.PostAsJsonAsync(Published(id, "views"), new { visitorId = device }, Ct)))["counted"]!);
        Assert.False((bool)(await OkAsync(await visitor.PostAsJsonAsync(Published(id, "views"), new { visitorId = device }, Ct)))["counted"]!);
        var counted = await OkAsync(await requester.PostAsJsonAsync(Published(id, "views"), new { }, Ct));
        Assert.Equal(2, (int?)counted["viewCount"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await visitor.PostAsJsonAsync(Published(id, "views"), new { }, Ct)).StatusCode);

        await OkAsync(await visitor.PutAsJsonAsync(Published(id, "feedback"), new { helpful = false, comment = "Mail me at someone@example.ae", visitorId = device }, Ct));
        await OkAsync(await requester.PutAsJsonAsync(Published(id, "feedback"), new { helpful = true }, Ct));
        var changed = await OkAsync(await visitor.PutAsJsonAsync(Published(id, "feedback"), new { helpful = true, visitorId = device }, Ct));
        Assert.Equal((2, 0), ((int?)changed["helpfulCount"], (int?)changed["notHelpfulCount"]));
        var mine = await OkAsync(await visitor.GetAsync(new Uri($"/api/v1/knowledge/published/{id}/feedback?visitorId={device}", UriKind.Relative), Ct));
        Assert.True((bool)mine["helpful"]!);

        var list = await OkAsync(await agent.GetAsync(Of(id, "feedback"), Ct));
        Assert.Equal(2, (int?)list["totalCount"]);
        await using var db = TestData.AppContext(database, tenant);
        Assert.All(await db.KbFeedbackEntries.ToListAsync(Ct), f => Assert.DoesNotContain(device, f.VisitorHash ?? string.Empty, StringComparison.Ordinal));
        var audit = await db.AuditEvents.Where(e => e.EntityType == "kb_feedback").ToListAsync(Ct);
        Assert.All(audit, e => Assert.DoesNotContain("someone@example.ae", e.Changes ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resolved_incident_becomes_a_draft_article_without_the_requester_s_personal_data()
    {
        var tenant = await database.CreateTenantAsync("kt");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var incident = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Printer jams", description = $"Hi, I'm {requester.Email}, the second floor printer jams." }));

        using var tooEarly = await agent.PostAsync(new Uri($"/api/v1/incidents/{incident}/knowledge-article", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);

        await OkAsync(await agent.MoveAsync(incident, "Resolved", notes: "Replaced the pickup roller."));
        using var created = await agent.PostAsync(new Uri($"/api/v1/incidents/{incident}/knowledge-article", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var article = await created.JsonAsync();
        var body = (string?)article["working"]!["body"]!["en"];
        Assert.Contains("Replaced the pickup roller.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(requester.Email, body, StringComparison.Ordinal);
        Assert.Contains("[EMAIL_1]", body, StringComparison.Ordinal);
        Assert.Equal(incident.ToString(), (string?)article["sourceTicket"]!["id"]);
        Assert.Equal("Internal", (string?)article["article"]!["visibility"]);

        var linked = await OkAsync(await agent.GetAsync(new Uri($"/api/v1/incidents/{incident}/knowledge-articles", UriKind.Relative), Ct));
        Assert.Single(linked.AsArray());
    }

    [Fact]
    public async Task Images_are_checked_and_shown_to_whoever_may_read_the_article()
    {
        var tenant = await database.CreateTenantAsync("ki");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var visitor = _factory.ForTenant(tenant);
        var (id, _) = await CreateAsync(agent, Content("Dock setup"));

        using var notImage = await agent.PostAsync(Of(id, "images"), File("notes.pdf", "%PDF-1.4"u8.ToArray()), Ct);
        Assert.Equal("image-type-not-allowed", (await notImage.ProblemAsync()).Code);
        using var renamed = await agent.PostAsync(Of(id, "images"), File("photo.png", "%PDF-1.4"u8.ToArray()), Ct);
        Assert.Equal("image-content-mismatch", (await renamed.ProblemAsync()).Code);
        var image = await OkAsync(await agent.PostAsync(Of(id, "images"), File("dock.png", Png), Ct));
        var imageUri = new Uri($"/api/v1/knowledge/articles/{id}/images/{image["id"]}", UriKind.Relative);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(imageUri, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync(imageUri, Ct)).StatusCode);
        await PublishAsync(agent, lead, id);
        using var shown = await visitor.GetAsync(imageUri, Ct);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("image/png", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", shown.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Png, await shown.Content.ReadAsByteArrayAsync(Ct));
        Assert.Single((await OkAsync(await visitor.GetAsync(Published(id), Ct)))["images"]!.AsArray());
    }

    [Fact]
    public async Task Retired_articles_are_not_read_or_found_and_can_be_reopened_as_a_draft()
    {
        var tenant = await database.CreateTenantAsync("kr");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var (id, _) = await CreateAsync(agent, Content("Old fax procedure"));
        await PublishAsync(agent, lead, id);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync(Of(id, "retire"), null, Ct)).StatusCode);
        var retired = await OkAsync(await lead.PostAsync(Of(id, "retire"), null, Ct));
        Assert.Equal("Retired", (string?)retired["article"]!["state"]);
        Assert.Equal(HttpStatusCode.NotFound, (await requester.GetAsync(Published(id), Ct)).StatusCode);
        await using (var db = TestData.AppContext(database, tenant))
        {
            var last = await db.SearchOutbox.Where(e => e.DocumentId == id.ToString()).OrderBy(e => e.Id).LastAsync(Ct);
            Assert.Equal(Data.Search.SearchOperation.Delete, last.Operation);
        }

        var reopened = await OkAsync(await agent.PostAsync(Of(id, "reopen"), null, Ct));
        Assert.Equal(("Draft", 2, "Old fax procedure"), ((string?)reopened["article"]!["state"], (int?)reopened["working"]!["number"], (string?)reopened["working"]!["title"]!["en"]));
        await PublishAsync(agent, lead, id);
        Assert.Equal(HttpStatusCode.OK, (await requester.GetAsync(Published(id), Ct)).StatusCode);
    }

    [Fact]
    public async Task Never_published_drafts_can_be_discarded_and_changes_need_the_current_version()
    {
        var tenant = await database.CreateTenantAsync("kd");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var (id, created) = await CreateAsync(agent, Content("Scratch"));

        using var stale = new HttpRequestMessage(HttpMethod.Put, Of(id, "draft")) { Content = JsonContent.Create(Content("Changed")) };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await agent.SendAsync(stale, Ct)).StatusCode);

        using var get = await agent.GetAsync(Of(id), Ct);
        using var current = new HttpRequestMessage(HttpMethod.Put, Of(id, "draft")) { Content = JsonContent.Create(Content("Changed")) };
        current.Headers.IfMatch.Add(get.Headers.ETag!);
        Assert.Equal(HttpStatusCode.OK, (await agent.SendAsync(current, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await agent.DeleteAsync(Of(id, "draft"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync(Of(id), Ct)).StatusCode);
        Assert.NotNull(created);
    }

    [Fact]
    public async Task Published_articles_missing_a_language_are_reported_and_body_changes_are_audited_by_length()
    {
        var tenant = await database.CreateTenantAsync("km");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var (id, _) = await CreateAsync(agent, Content("English only"));
        await PublishAsync(agent, lead, id);

        var report = await OkAsync(await admin.GetAsync(new Uri("/api/v1/translations/missing", UriKind.Relative), Ct));
        Assert.Contains(report["items"]!.AsArray(), m => (string?)m!["entityType"] == "kb_articles" && (string?)m["entityId"] == "KB-000001" && (string?)m["field"] == "title");

        await using var db = TestData.AppContext(database, tenant);
        var created = await db.AuditEvents.Where(e => e.EntityType == "kb_article_version").OrderBy(e => e.Sequence).FirstAsync(Ct);
        Assert.Contains("characters)", created.Changes, StringComparison.Ordinal);
        Assert.DoesNotContain("Steps for", created.Changes, StringComparison.Ordinal);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "knowledge.article.published" && e.Category == AuditCategory.Change, Ct));
    }

    [Fact]
    public async Task Knowledge_tables_are_isolated_between_tenants()
    {
        var first = await database.CreateTenantAsync("kx");
        var second = await database.CreateTenantAsync("kx");
        using var agent = await _factory.ForTenant(first).SignedInAsync(database, first, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(first).SignedInAsync(database, first, BuiltInRoles.TeamLead);
        using var other = await _factory.ForTenant(second).SignedInAsync(database, second, BuiltInRoles.TeamLead);
        var (id, _) = await CreateAsync(agent, Content("Confidential"));
        await PublishAsync(agent, lead, id);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Of(id), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ForTenant(second).GetAsync(Published(id), Ct)).StatusCode);
        await using var db = TestData.AppContext(database, second);
        Assert.False(await db.KbArticles.AnyAsync(Ct));
        Assert.False(await db.KbArticleVersions.AnyAsync(Ct));
    }

    private static async Task<List<string>> ReadersAsync(Data.ZelvoDbContext db, Guid id)
    {
        var entry = await db.SearchOutbox.Where(e => e.DocumentId == id.ToString()).OrderBy(e => e.Id).LastAsync(Ct);
        return [.. JsonNode.Parse(entry.Document!)!["readers"]!.AsArray().Select(r => (string)r!)];
    }

    private static MultipartFormDataContent File(string name, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        return form;
    }
}
