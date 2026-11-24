using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Search;

namespace Zelvo.Api.Tests;

/// <summary>Service catalog builder and service requests (task C2, module 2) against PostgreSQL.</summary>
public sealed class CatalogApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% test document\n");
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static string Of(Guid id, string? path = null) => $"/api/v1/catalog/services/{id}{(path is null ? "" : "/" + path)}";

    private static string Published(Guid id, string? path = null) => $"/api/v1/catalog/published/{id}{(path is null ? "" : "/" + path)}";

    private static JsonObject Card(string en, string? ar = null, Action<JsonObject>? more = null)
    {
        var card = new JsonObject
        {
            ["name"] = new JsonObject { ["en"] = en, ["ar"] = ar },
            ["summary"] = new JsonObject { ["en"] = "Get " + en },
            ["description"] = new JsonObject { ["en"] = $"All about **{en}**." },
            ["channels"] = new JsonArray("Portal"),
            ["steps"] = new JsonArray(
                new JsonObject { ["name"] = new JsonObject { ["en"] = "Received", ["ar"] = "استلام" }, ["stage"] = "Submitted" },
                new JsonObject { ["name"] = new JsonObject { ["en"] = "Working", ["ar"] = "تنفيذ" }, ["stage"] = "InProgress" },
                new JsonObject { ["name"] = new JsonObject { ["en"] = "Done", ["ar"] = "اكتمل" }, ["stage"] = "Resolved" }),
            ["faq"] = new JsonArray(new JsonObject
            {
                ["question"] = new JsonObject { ["en"] = "How long?", ["ar"] = "كم المدة؟" },
                ["answer"] = new JsonObject { ["en"] = "Two days.", ["ar"] = "يومان." },
            }),
            ["days"] = 2,
        };
        more?.Invoke(card);
        return card;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string code, JsonObject? card = null, IReadOnlyList<Guid>? catalogIds = null)
    {
        var created = await client.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code, catalogIds }, HttpStatusCode.Created);
        var id = created.Id();
        if (card is not null)
        {
            if (catalogIds is not null && card["catalogIds"] is null)
            {
                card["catalogIds"] = new JsonArray([.. catalogIds.Select(c => (JsonNode)c.ToString())]);
            }

            await client.SendOkAsync(HttpMethod.Put, Of(id, "draft"), new { card, changeNote = "First" });
        }

        return id;
    }

    private static async Task PublishAsync(HttpClient author, HttpClient reviewer, Guid id)
    {
        await author.SendOkAsync(HttpMethod.Post, Of(id, "submit"));
        await reviewer.SendOkAsync(HttpMethod.Post, Of(id, "approve"));
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string field, string name, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        form.Add(new StringContent(field), "field");
        using var response = await client.PostAsync(new Uri("/api/v1/form-uploads", UriKind.Relative), form, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return Guid.Parse((string)(await response.JsonAsync())["id"]!);
    }

    [Fact]
    public async Task A_service_is_published_by_a_second_person_and_a_change_is_reviewed_while_requesters_keep_the_published_card()
    {
        var tenant = await database.CreateTenantAsync("cl");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var otherAdmin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var id = await CreateAsync(admin, "IT-LAPTOP", Card("New laptop", "حاسوب جديد"));
        var detail = await admin.SendOkAsync(HttpMethod.Get, Of(id));
        Assert.Equal(("Draft", 1), ((string?)detail["state"], (int?)detail["working"]!["number"]));
        Assert.Empty(detail["problems"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await requester.SendAsync(HttpMethod.Get, Published(id))).StatusCode);

        await admin.SendOkAsync(HttpMethod.Post, Of(id, "submit"));
        using var whileInReview = await admin.SendAsync(HttpMethod.Put, Of(id, "draft"), new { card = Card("Changed") });
        Assert.Equal(HttpStatusCode.Conflict, whileInReview.StatusCode);

        // Four-eyes: the author cannot approve; another publisher can.
        using var own = await admin.SendAsync(HttpMethod.Post, Of(id, "approve"));
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal("four-eyes", (await own.ProblemAsync()).Code);
        Assert.Single((await otherAdmin.SendOkAsync(HttpMethod.Get, Of(id))).AsArray("actions", "approve"));
        var queue = await otherAdmin.ArrayAsync("/api/v1/catalog/services/review-queue");
        Assert.Equal("IT-LAPTOP", (string?)queue.Single()!["code"]);
        var published = await otherAdmin.SendOkAsync(HttpMethod.Post, Of(id, "approve"));
        Assert.Equal(("Active", 1), ((string?)published["state"], (int?)published["published"]!["number"]));

        var reading = await requester.SendOkAsync(HttpMethod.Get, Published(id));
        Assert.Equal("New laptop", (string?)reading["name"]!["en"]);
        Assert.Equal("Two days.", (string?)reading["faq"]![0]!["answer"]!["en"]);
        Assert.Equal(3, reading["steps"]!.AsArray().Count);
        Assert.True((bool)reading["mayRequest"]!);

        // A change starts version 2; requesters keep version 1 until it is approved.
        var changed = await admin.SendOkAsync(HttpMethod.Put, Of(id, "draft"), new { card = Card("New laptop or tablet") });
        Assert.Equal((2, "Active"), ((int?)changed["working"]!["number"], (string?)changed["state"]));
        Assert.Equal("New laptop", (string?)(await requester.SendOkAsync(HttpMethod.Get, Published(id)))["name"]!["en"]);
        await admin.SendOkAsync(HttpMethod.Post, Of(id, "submit"));
        using var noComment = await otherAdmin.SendAsync(HttpMethod.Post, Of(id, "send-back"), new { });
        Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);
        var sentBack = await otherAdmin.SendOkAsync(HttpMethod.Post, Of(id, "send-back"), new { comment = "Say which tablets." });
        Assert.Equal(("Draft", "Say which tablets."), ((string?)sentBack["working"]!["state"], (string?)sentBack["working"]!["reviewComment"]));
        await PublishAsync(admin, otherAdmin, id);
        Assert.Equal("New laptop or tablet", (string?)(await requester.SendOkAsync(HttpMethod.Get, Published(id)))["name"]!["en"]);
        var versions = await admin.ArrayAsync(Of(id, "versions"));
        Assert.Equal(["Published", "Superseded"], versions.Select(v => (string?)v!["state"]));

        // Suspended services are shown with their message and cannot be requested; retired ones are gone.
        await otherAdmin.SendOkAsync(HttpMethod.Post, Of(id, "suspend"), new { message = new { en = "Stock runs out until May.", ar = "نفاد المخزون حتى مايو." } });
        var suspended = await requester.SendOkAsync(HttpMethod.Get, Published(id));
        Assert.Equal(("Suspended", "Stock runs out until May.", false), ((string?)suspended["state"], (string?)suspended["suspendedMessage"]!["en"], (bool)suspended["mayRequest"]!));
        using var refused = await requester.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = id });
        Assert.Equal("service-suspended", (await refused.ProblemAsync()).Code);
        await otherAdmin.SendOkAsync(HttpMethod.Post, Of(id, "resume"));
        await otherAdmin.SendOkAsync(HttpMethod.Post, Of(id, "retire"));
        Assert.Equal(HttpStatusCode.NotFound, (await requester.SendAsync(HttpMethod.Get, Published(id))).StatusCode);
        var reopened = await admin.SendOkAsync(HttpMethod.Post, Of(id, "reopen"));
        Assert.Equal(("Retired", 3), ((string?)reopened["state"], (int?)reopened["working"]!["number"]));

        await using var db = TestData.AppContext(database, tenant);
        var actions = await db.AuditEvents.Where(a => a.EntityId == id.ToString()).Select(a => a.Action).ToListAsync(Ct);
        Assert.Contains("catalog.service.published", actions);
        Assert.Contains("catalog.service.suspended", actions);
        Assert.Contains("catalog.service.retired", actions);
    }

    [Fact]
    public async Task Problems_stop_a_card_from_going_to_review_and_codes_are_unique()
    {
        var tenant = await database.CreateTenantAsync("cp");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var id = await CreateAsync(admin, "HR-CERT", Card("Salary certificate", more: c =>
        {
            c["fee"] = new JsonObject { ["paid"] = true };
            c["type"] = "External";
            c["externalUrl"] = "http://insecure.example";
            c["documents"] = new JsonArray(new JsonObject { ["key"] = "Bad key", ["name"] = new JsonObject { ["en"] = "Letter" }, ["fileTypes"] = new JsonArray("exe") });
            c["access"] = new JsonObject { ["mode"] = "Restricted" };
        }));
        var problems = (await admin.SendOkAsync(HttpMethod.Get, Of(id)))["problems"]!.AsArray().Select(p => (string?)p!["code"]).ToHashSet();
        Assert.Superset(new HashSet<string?> { "fee-invalid", "external-url-invalid", "external-has-form", "document-key-invalid", "file-type-not-allowed", "access-empty" }, problems);
        using var submit = await admin.SendAsync(HttpMethod.Post, Of(id, "submit"));
        Assert.Equal("service-has-problems", (await submit.ProblemAsync()).Code);

        using var taken = await admin.SendAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "HR-CERT" });
        Assert.Equal("code-taken", (await taken.ProblemAsync()).Code);
        using var badCode = await admin.SendAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);

        // The hierarchy has any depth but no circles.
        var parent = await CreateAsync(admin, "HR", Card("Human resources"));
        await admin.SendOkAsync(HttpMethod.Put, Of(id, "draft"), new { card = Card("Salary certificate", more: c => c["parentId"] = parent.ToString()) });
        var cycle = await admin.SendOkAsync(HttpMethod.Put, Of(parent, "draft"), new { card = Card("Human resources", more: c => c["parentId"] = id.ToString()) });
        Assert.Contains(cycle["problems"]!.AsArray(), p => (string?)p!["code"] == "parent-cycle");
    }

    [Fact]
    public async Task Hidden_services_are_left_out_for_everyone_outside_the_allowed_groups_roles_departments_locations_and_users()
    {
        var tenant = await database.CreateTenantAsync("ca");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var reviewer = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var inFinance = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var inGroup = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var atBranch = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var named = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var outsider = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var approver = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Approver);

        var finance = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/departments", new { name = new { en = "Finance" } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/users/{inFinance.Id}", new { departmentId = finance });
        var group = await admin.GroupAsync("Facilities", inGroup);
        var city = await admin.LocationAsync("Ajman");
        var branch = await admin.LocationAsync("Branch", city);
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/users/{atBranch.Id}", new { locationId = branch });

        var restricted = await CreateAsync(admin, "FIN-ADV", Card("Salary advance", more: c => c["access"] = new JsonObject
        {
            ["mode"] = "Restricted",
            ["departmentIds"] = new JsonArray(finance.ToString()),
            ["groupIds"] = new JsonArray(group.ToString()),
            ["locationIds"] = new JsonArray(city.ToString()),
            ["userIds"] = new JsonArray(named.Id.ToString()),
            ["roleKeys"] = new JsonArray("approver"),
        }));
        var everyone = await CreateAsync(admin, "GEN", Card("General request"));
        var open = await CreateAsync(admin, "PUB", Card("Public permit", more: c => c["access"] = new JsonObject { ["mode"] = "Public" }));
        foreach (var id in new[] { restricted, everyone, open })
        {
            await PublishAsync(admin, reviewer, id);
        }

        async Task<HashSet<string?>> SeenAsync(HttpClient client) =>
            [.. (await client.SendOkAsync(HttpMethod.Get, "/api/v1/catalog/published"))["services"]!.AsArray().Select(s => (string?)s!["code"])];

        foreach (var allowed in new[] { inFinance, inGroup, atBranch, named })
        {
            using var client = _factory.ForTenant(tenant).As(allowed);
            Assert.Equal(["FIN-ADV", "GEN", "PUB"], (await SeenAsync(client)).Order());
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Get, Published(restricted))).StatusCode);
        }

        Assert.Contains("FIN-ADV", await SeenAsync(approver));
        using var stranger = _factory.ForTenant(tenant).As(outsider);
        Assert.Equal(["GEN", "PUB"], (await SeenAsync(stranger)).Order());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Get, Published(restricted))).StatusCode);
        using var refused = await stranger.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = restricted });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        // Visitors see public services only, and sign in to request them.
        using var visitor = _factory.ForTenant(tenant);
        Assert.Equal(["PUB"], await SeenAsync(visitor));
        var card = await visitor.SendOkAsync(HttpMethod.Get, Published(open));
        Assert.Equal((false, true), ((bool)card["mayRequest"]!, (bool)card["signInToRequest"]!));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.SendAsync(HttpMethod.Get, Published(everyone))).StatusCode);

        // Search readers follow the same rules.
        await using var db = TestData.AppContext(database, tenant);
        var service = await db.CatalogServices.SingleAsync(s => s.Id == restricted, Ct);
        var version = await db.CatalogServiceVersions.SingleAsync(v => v.Id == service.PublishedVersionId, Ct);
        var document = CatalogSearch.Document(service, ServiceCards.Read(version.Content), inActiveCatalog: true)!;
        Assert.Equal(
            new[] { SearchReaders.Group(group), SearchReaders.Role("approver"), SearchReaders.Department(finance), SearchReaders.Location(city), SearchReaders.User(named.Id) }.Order(),
            document.Readers.Order());
        Assert.True(await db.SearchOutbox.AnyAsync(o => o.DocumentType == CatalogSearch.Type && o.DocumentId == restricted.ToString(), Ct));
        var branchReaders = SearchReaders.ForCaller(atBranch.Id, ["requester"], locationAncestry: [branch, city]);
        Assert.Contains(SearchReaders.Location(city), branchReaders);
    }

    [Fact]
    public async Task Catalog_editors_write_and_submit_the_services_of_their_catalogs_only()
    {
        var tenant = await database.CreateTenantAsync("ce");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var editorUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var editor = _factory.ForTenant(tenant).As(editorUser);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        var it = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/catalogs", new
        {
            key = "it", name = new { en = "IT services", ar = "خدمات تقنية المعلومات" }, editors = new[] { new { userId = (Guid?)editorUser.Id, groupId = (Guid?)null } },
        }, HttpStatusCode.Created)).Id();
        var hr = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/catalogs", new { key = "hr", name = new { en = "HR services" } }, HttpStatusCode.Created)).Id();
        var hrService = await CreateAsync(admin, "HR-LEAVE", Card("Leave request"), [hr]);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/catalog/services")).StatusCode);
        using var outside = await editor.SendAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "IT-X", catalogIds = new[] { hr } });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        var mine = await CreateAsync(editor, "IT-VPN", Card("VPN access"), [it]);
        Assert.Equal(["IT-VPN"], (await editor.SendOkAsync(HttpMethod.Get, "/api/v1/catalog/services"))["items"]!.AsArray().Select(s => (string?)s!["code"]));
        Assert.Equal(HttpStatusCode.NotFound, (await editor.SendAsync(HttpMethod.Get, Of(hrService))).StatusCode);
        using var moveAway = await editor.SendAsync(HttpMethod.Put, Of(mine, "draft"), new { card = Card("VPN access", more: c => c["catalogIds"] = new JsonArray(hr.ToString())) });
        Assert.Equal(HttpStatusCode.BadRequest, moveAway.StatusCode);

        await editor.SendOkAsync(HttpMethod.Post, Of(mine, "submit"));
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(HttpMethod.Post, Of(mine, "approve"))).StatusCode);
        var published = await admin.SendOkAsync(HttpMethod.Post, Of(mine, "approve"));
        Assert.Equal("Active", (string?)published["state"]);

        var catalogs = await admin.ArrayAsync("/api/v1/catalog/catalogs");
        Assert.Equal(1, (int?)catalogs.Single(c => (string?)c!["key"] == "it")!["serviceCount"]);
        using var inUse = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/catalog/catalogs/{it}");
        Assert.Equal("catalog-in-use", (await inUse.ProblemAsync()).Code);
    }

    [Fact]
    public async Task A_request_carries_the_form_answers_and_required_documents_and_others_request_on_behalf_only_with_an_allowed_role()
    {
        var tenant = await database.CreateTenantAsync("cr");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var reviewer = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);

        var formId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key = "laptop", name = new { en = "Laptop" },
            schema = new { elements = new[] { new { type = "Field", field = new { key = "model", type = "Text", label = new { en = "Model", ar = "الطراز" }, required = true } } } },
        }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{formId}/publish");
        var id = await CreateAsync(admin, "IT-LAPTOP", Card("New laptop", more: c =>
        {
            c["formId"] = formId.ToString();
            c["documents"] = new JsonArray(
                new JsonObject { ["key"] = "approvalLetter", ["name"] = new JsonObject { ["en"] = "Approval letter" }, ["required"] = true, ["fileTypes"] = new JsonArray("pdf") },
                new JsonObject { ["key"] = "quote", ["name"] = new JsonObject { ["en"] = "Quote" }, ["required"] = false });
            c["onBehalfRoleKeys"] = new JsonArray(BuiltInRoles.TeamLead);
        }));
        await PublishAsync(admin, reviewer, id);

        var form = await requester.SendOkAsync(HttpMethod.Get, Published(id, "request-form"));
        var versionId = (string?)form["form"]!["versionId"];
        using var missing = await requester.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = id, formVersionId = versionId, formAnswers = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var noDocument = await requester.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = id, formVersionId = versionId, formAnswers = new { model = "X1" } });
        var noDocumentProblem = await noDocument.JsonAsync();
        Assert.Equal("documents.approvalLetter:document-required",
            string.Join(",", noDocumentProblem["documentErrors"]!.AsArray().Select(e => $"{(string)e!["field"]!}:{(string)e["code"]!}")));

        var wrongField = await UploadAsync(requester, "documentQuote", "letter.pdf", Pdf);
        using var misplaced = await requester.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new
        {
            serviceId = id, formVersionId = versionId, formAnswers = new { model = "X1" }, documents = new Dictionary<string, Guid[]> { ["approvalLetter"] = [wrongField] },
        });
        Assert.Equal(HttpStatusCode.BadRequest, misplaced.StatusCode);
        var letter = await UploadAsync(requester, "documentApprovalLetter", "letter.pdf", Pdf);
        var created = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new
        {
            serviceId = id, formVersionId = versionId, formAnswers = new { model = "X1" }, documents = new Dictionary<string, Guid[]> { ["approvalLetter"] = [letter] },
        }, HttpStatusCode.Created);
        Assert.Equal(("REQ-000001", "New laptop", "New", 0), ((string?)created["number"], (string?)created["title"], (string?)created["state"], (int?)created["currentStep"]));
        Assert.Null(created["assignee"]);
        var requestId = created.Id();
        var files = await requester.ArrayAsync($"/api/v1/service-requests/{requestId}/attachments");
        Assert.Equal("approvalLetter", (string?)files.Single()!["documentKey"]);
        var answers = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{requestId}/form");
        Assert.Equal("X1", (string?)answers["answers"]!["model"]);

        // On behalf: agents' roles are not allowed by this service; team leads are.
        using var agentOnBehalf = await agent.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = id, requesterId = requesterUser.Id });
        Assert.Equal(HttpStatusCode.Forbidden, agentOnBehalf.StatusCode);
        var leadLetter = await UploadAsync(lead, "documentApprovalLetter", "letter.pdf", Pdf);
        var forSomeone = await lead.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new
        {
            serviceId = id, requesterId = requesterUser.Id, formVersionId = versionId, formAnswers = new { model = "X2" },
            documents = new Dictionary<string, Guid[]> { ["approvalLetter"] = [leadLetter] },
        }, HttpStatusCode.Created);
        Assert.Equal(requesterUser.Id.ToString(), (string?)forSomeone["requester"]!["id"]);
        Assert.NotNull(forSomeone["requestedBy"]);
        var mine = await requester.SendOkAsync(HttpMethod.Get, "/api/v1/service-requests?mine=true");
        Assert.Equal(2, (int?)mine["totalCount"]);

        // Staff work on it; the requester reopens it once resolved.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/service-requests/{requestId}/assignment", new { groupId = (Guid?)null, assigneeId = await AgentIdAsync(tenant) });
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{requestId}/transitions", new { state = "InProgress" });
        var resolved = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{requestId}/transitions", new { state = "Resolved", resolutionNotes = "Delivered." });
        Assert.Equal(2, (int?)resolved["currentStep"]);
        using var requesterResolves = await requester.SendAsync(HttpMethod.Post, $"/api/v1/service-requests/{requestId}/transitions", new { state = "Closed" });
        Assert.Equal(HttpStatusCode.Forbidden, requesterResolves.StatusCode);
        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{requestId}/transitions", new { state = "Reopened" });
        using var stranger = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Get, $"/api/v1/service-requests/{requestId}")).StatusCode);

        async Task<Guid> AgentIdAsync(Data.Tenancy.Tenant _)
        {
            var me = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/me");
            return Guid.Parse((string)me["id"]!);
        }
    }

    [Fact]
    public async Task Prerequisite_and_external_services_cannot_be_requested_until_allowed()
    {
        var tenant = await database.CreateTenantAsync("cq");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var reviewer = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var first = await CreateAsync(admin, "NAME", Card("Trade name"));
        var second = await CreateAsync(admin, "LICENCE", Card("Trade licence", more: c =>
        {
            c["prerequisiteServiceIds"] = new JsonArray(first.ToString());
        }));
        var external = await CreateAsync(admin, "EXT", Card("Visa", more: c =>
        {
            c["type"] = "External";
            c["externalUrl"] = "https://provider.example/visa";
        }));
        foreach (var id in new[] { first, second, external })
        {
            await PublishAsync(admin, reviewer, id);
        }

        using var blocked = await requester.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = second });
        Assert.Equal("prerequisite-missing", (await blocked.ProblemAsync()).Code);
        var card = await requester.SendOkAsync(HttpMethod.Get, Published(second));
        Assert.False((bool)card["prerequisites"]![0]!["completed"]!);

        var done = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = first }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{done}/transitions", new { state = "Resolved", resolutionNotes = "Reserved." });
        await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = second }, HttpStatusCode.Created);

        using var externalRequest = await requester.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = external });
        Assert.Equal("service-external", (await externalRequest.ProblemAsync()).Code);
        Assert.Equal("https://provider.example/visa", (string?)(await requester.SendOkAsync(HttpMethod.Get, Published(external)))["externalUrl"]);
        Assert.Equal(2, (int?)(await agent.SendOkAsync(HttpMethod.Get, "/api/v1/service-requests"))["totalCount"]);
    }

    [Fact]
    public async Task Services_are_copied_saved_as_templates_and_made_from_zelvo_s_ready_made_templates()
    {
        var tenant = await database.CreateTenantAsync("ct");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var templates = await admin.ArrayAsync("/api/v1/catalog/templates");
        Assert.Contains(templates, t => (string?)t!["id"] == "builtin:new-laptop" && (string?)t["name"]!["ar"] == "حاسوب محمول جديد");
        foreach (var template in templates)
        {
            // Every ready-made template is complete in both languages and has no problems.
            var code = ("T-" + ((string)template!["id"]!).Replace("builtin:", "", StringComparison.Ordinal)).ToUpperInvariant();
            var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = code[..Math.Min(code.Length, 30)], templateId = (string)template["id"]! }, HttpStatusCode.Created)).Id();
            var detail = await admin.SendOkAsync(HttpMethod.Get, Of(id));
            Assert.Empty(detail["problems"]!.AsArray());
            Assert.NotNull((string?)detail["working"]!["card"]!["name"]!["ar"]);
        }

        var laptop = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "IT-LAPTOP", templateId = "builtin:new-laptop" }, HttpStatusCode.Created)).Id();
        var copy = await admin.SendOkAsync(HttpMethod.Post, Of(laptop, "copy"), new { code = "IT-TABLET", name = new { en = "New tablet" } }, HttpStatusCode.Created);
        // A new name replaces both languages (the original's Arabic name would describe the laptop).
        Assert.Equal(("IT-TABLET", "New tablet", null), ((string?)copy["code"], (string?)copy["working"]!["card"]!["name"]!["en"], (string?)copy["working"]!["card"]!["name"]!["ar"]));
        Assert.Equal("Draft", (string?)copy["state"]);
        var saved = await admin.SendOkAsync(HttpMethod.Post, Of(laptop, "save-as-template"), new { name = new { en = "Our laptop", ar = "حاسوبنا" } }, HttpStatusCode.Created);
        var fromSaved = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "IT-DESKTOP", templateId = (string)saved["id"]! }, HttpStatusCode.Created)).Id();
        Assert.Equal("New laptop", (string?)(await admin.SendOkAsync(HttpMethod.Get, Of(fromSaved)))["working"]!["card"]!["name"]!["en"]);

        // Drafts that were never published are deleted; discarding them deletes them too.
        await admin.SendOkAsync(HttpMethod.Delete, Of(fromSaved), expected: HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Get, Of(fromSaved))).StatusCode);
    }

    [Fact]
    public async Task Service_cards_are_exported_and_imported_in_bulk_as_drafts_that_still_need_review()
    {
        var tenant = await database.CreateTenantAsync("cb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var reviewer = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/catalogs", new { key = "it", name = new { en = "IT" } }, HttpStatusCode.Created);
        var id = await CreateAsync(admin, "IT-VPN", Card("VPN access", "الوصول إلى الشبكة"));
        await PublishAsync(admin, reviewer, id);

        using var export = await admin.GetAsync(new Uri("/api/v1/bulk/services/export?format=csv", UriKind.Relative), Ct);
        var csv = await export.Content.ReadAsStringAsync(Ct);
        Assert.True(export.IsSuccessStatusCode, csv);
        Assert.Contains("IT-VPN", csv, StringComparison.Ordinal);
        Assert.Contains("الوصول إلى الشبكة", csv, StringComparison.Ordinal);

        var import = "code,nameEn,nameAr,catalogs,days,channels,steps\n"
            + "IT-VPN,VPN for staff,,it,3,Portal;Email,\n"
            + "IT-WIFI,Guest Wi-Fi,شبكة الضيوف,it,1,Portal,\"[{\"\"name\"\":{\"\"en\"\":\"\"Done\"\"},\"\"stage\"\":\"\"Resolved\"\"}]\"\n";
        using var bad = await ImportAsync(admin, import.Replace("Portal;Email", "Portal;Fax", StringComparison.Ordinal), dryRun: false);
        var badResult = await bad.JsonAsync();
        Assert.Contains(badResult["errors"]!.AsArray(), e => (string?)e!["code"] == "value-invalid");
        Assert.Single(await DbServicesAsync());

        using var applied = await ImportAsync(admin, import, dryRun: false);
        var result = await applied.JsonAsync();
        Assert.True(applied.IsSuccessStatusCode, result.ToJsonString());
        Assert.Equal((1, 1), ((int)result["created"]!, (int)result["updated"]!));
        var vpn = await admin.SendOkAsync(HttpMethod.Get, Of(id));
        Assert.Equal(("Active", "VPN access", "VPN for staff", 3), ((string?)vpn["state"], (string?)vpn["published"]!["card"]!["name"]!["en"],
            (string?)vpn["working"]!["card"]!["name"]!["en"], (int?)vpn["working"]!["card"]!["days"]));
        var services = await DbServicesAsync();
        var wifi = services.Single(s => s.Code == "IT-WIFI");
        Assert.Equal(ServiceState.Draft, wifi.State);

        async Task<List<CatalogService>> DbServicesAsync()
        {
            await using var db = TestData.AppContext(database, tenant);
            return await db.CatalogServices.AsNoTracking().ToListAsync(Ct);
        }
    }

    [Fact]
    public async Task Published_services_and_catalogs_missing_a_language_are_reported()
    {
        var tenant = await database.CreateTenantAsync("cm");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var reviewer = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/catalogs", new { key = "it", name = new { en = "IT" } }, HttpStatusCode.Created);
        var id = await CreateAsync(admin, "IT-VPN", Card("VPN access"));
        await PublishAsync(admin, reviewer, id);

        var report = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/translations/missing");
        var text = report.ToJsonString();
        Assert.Contains("service_catalogs", text, StringComparison.Ordinal);
        Assert.Contains("catalog_services", text, StringComparison.Ordinal);
        Assert.Contains("IT-VPN", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_tables_are_isolated_between_tenants()
    {
        var a = await database.CreateTenantAsync("cia");
        var b = await database.CreateTenantAsync("cib");
        using var adminA = await _factory.ForTenant(a).SignedInAsync(database, a, BuiltInRoles.TenantAdmin);
        using var reviewerA = await _factory.ForTenant(a).SignedInAsync(database, a, BuiltInRoles.TenantAdmin);
        using var adminB = await _factory.ForTenant(b).SignedInAsync(database, b, BuiltInRoles.TenantAdmin);
        await adminA.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/catalogs", new { key = "it", name = new { en = "IT" } }, HttpStatusCode.Created);
        var id = await CreateAsync(adminA, "IT-VPN", Card("VPN access", more: c => c["access"] = new JsonObject { ["mode"] = "Public" }));
        await PublishAsync(adminA, reviewerA, id);
        var request = (await adminA.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = id }, HttpStatusCode.Created)).Id();

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.SendAsync(HttpMethod.Get, Of(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.SendAsync(HttpMethod.Get, Published(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.SendAsync(HttpMethod.Get, $"/api/v1/service-requests/{request}")).StatusCode);
        Assert.Empty(await adminB.ArrayAsync("/api/v1/catalog/catalogs"));
        using var visitorB = _factory.ForTenant(b);
        Assert.Empty((await visitorB.SendOkAsync(HttpMethod.Get, "/api/v1/catalog/published"))["services"]!.AsArray());

        await using var db = TestData.AppContext(database, b);
        Assert.Empty(await db.CatalogServices.ToListAsync(Ct));
        Assert.Empty(await db.CatalogServiceVersions.ToListAsync(Ct));
        Assert.Empty(await db.ServiceCatalogs.ToListAsync(Ct));
        Assert.Empty(await db.ServiceRequests.ToListAsync(Ct));
    }

    [Fact]
    public void The_card_is_tidied_and_document_upload_fields_round_trip()
    {
        var card = ServiceCards.Normalise(new ServiceCard
        {
            Name = new LocalizedText("  Laptop ", " "),
            Keywords = ["vpn", "VPN", " "],
            Fee = new ServiceFee { Paid = false, AmountAed = 50m },
            Documents = [new ServiceDocument { Key = "passportCopy", FileTypes = [".PDF", "pdf"] }],
        });
        Assert.Equal(("Laptop", null), (card.Name.En, card.Name.Ar));
        Assert.Equal(["vpn"], card.Keywords);
        Assert.Null(card.Fee.AmountAed);
        Assert.Equal(["pdf"], card.Documents[0].FileTypes);
        Assert.Equal("documentPassportCopy", ServiceCards.UploadFieldOf("passportCopy"));
        Assert.Equal("passportCopy", ServiceCards.DocumentKeyOf("documentPassportCopy"));
        Assert.Null(ServiceCards.DocumentKeyOf("model"));
        Assert.Equal(ServiceCards.Write(card), ServiceCards.Write(ServiceCards.Read(ServiceCards.Write(card))));
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, string csv, bool dryRun)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "services.csv");
        return await client.PostAsync(new Uri($"/api/v1/bulk/services/import?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative), form, Ct);
    }
}

internal static class CatalogTestJson
{
    /// <summary>The values of an array under <paramref name="key"/>, filtered to <paramref name="value"/>.</summary>
    public static IEnumerable<string?> AsArray(this JsonObject json, string key, string value) =>
        json[key]!.AsArray().Select(v => (string?)v).Where(v => v == value);
}
