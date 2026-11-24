using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Tests;

/// <summary>Form builder (module 2, task C1) against PostgreSQL: forms, field sets, bindings, answers on incidents, uploads and profile defaults.</summary>
public sealed class FormApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Pdf = "%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n"u8.ToArray();
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static JsonObject Text(string key, string en, object? more = null) => Merge(new JsonObject
    {
        ["key"] = key, ["type"] = "Text", ["label"] = new JsonObject { ["en"] = en, ["ar"] = en + " (ar)" },
    }, more);

    private static JsonObject Merge(JsonObject field, object? more)
    {
        if (more is not null)
        {
            foreach (var (k, v) in JsonSerializerNode(more).AsObject())
            {
                field[k] = v?.DeepClone();
            }
        }

        return field;
    }

    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value)!;

    private static object FieldElement(object field) => new { type = "Field", field };

    private static async Task<Guid> PublishedFormAsync(HttpClient admin, string key, params object[] elements)
    {
        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms",
            new { key, name = new { en = "Form " + key, ar = "نموذج " + key }, schema = new { elements } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{id}/publish");
        return id;
    }

    private static Task<JsonObject> BindAsync(HttpClient admin, Guid formId, Guid? categoryId = null) =>
        admin.SendOkAsync(HttpMethod.Put, "/api/v1/form-bindings", new { recordType = "Incident", categoryId, formId });

    private static async Task<JsonObject> ResolveAsync(HttpClient client, Guid? categoryId = null, Guid? requesterId = null) =>
        await client.SendOkAsync(HttpMethod.Get, $"/api/v1/forms/resolve?recordType=Incident{(categoryId is null ? "" : $"&categoryId={categoryId}")}{(requesterId is null ? "" : $"&requesterId={requesterId}")}");

    private static async Task<HttpResponseMessage> ReportAsync(HttpClient client, object body) =>
        await client.SendAsync(HttpMethod.Post, "/api/v1/incidents", body);

    private static string[] FormErrors(JsonNode problem) =>
        [.. problem["formErrors"]!.AsArray().Select(e => $"{(string)e!["field"]!}:{(string)e["code"]!}").Order(StringComparer.Ordinal)];

    private static MultipartFormDataContent Upload(string field, string name, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        form.Add(new StringContent(field), "field");
        return form;
    }

    [Fact]
    public async Task A_category_form_is_filled_checked_on_the_server_and_kept_with_its_version()
    {
        var tenant = await database.CreateTenantAsync("fm");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var hardware = await admin.CategoryAsync("Hardware");
        var laptop = await admin.CategoryAsync("Laptop", parentId: hardware);

        var formId = await PublishedFormAsync(admin, "laptop-problem",
            FieldElement(new { key = "addMonitor", type = "Checkbox", label = new { en = "Add a monitor", ar = "إضافة شاشة" } }),
            FieldElement(new
            {
                key = "monitorSize", type = "Dropdown", label = new { en = "Monitor size", ar = "حجم الشاشة" },
                options = new[] { new { value = "24", label = new { en = "24 inch", ar = "24 بوصة" } }, new { value = "27", label = new { en = "27 inch", ar = "27 بوصة" } } },
                rules = new[]
                {
                    new { action = "Show", match = "All", conditions = new[] { new { field = "addMonitor", @operator = "Checked" } } },
                    new { action = "Require", match = "All", conditions = new[] { new { field = "addMonitor", @operator = "Checked" } } },
                },
            }),
            FieldElement(Text("iban", "Refund IBAN", new { validation = new { format = "Iban" } })),
            FieldElement(Text("serial", "Serial number", new { required = true })));
        // On the parent category: its children use it too.
        await BindAsync(admin, formId, hardware);

        Assert.Null((await ResolveAsync(requester))["form"]);
        var resolved = await ResolveAsync(requester, laptop);
        Assert.Equal("laptop-problem", (string?)resolved["form"]!["key"]);
        var versionId = (string)resolved["form"]!["versionId"]!;
        Assert.Equal(["addMonitor", "monitorSize", "iban", "serial"], resolved["fields"]!.AsArray().Select(f => (string)f!["field"]!["key"]!));

        // Required fields, formats and rules are checked by the server.
        using var refused = await ReportAsync(requester, new
        {
            title = "Laptop broken", categoryId = laptop, formVersionId = versionId,
            formAnswers = new { addMonitor = true, iban = "AE07 0331 2345 6789 0123 457", other = "x" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(["iban:format-iban", "monitorSize:required", "other:field-unknown", "serial:required"], FormErrors(await refused.JsonAsync()));

        using var created = await ReportAsync(requester, new
        {
            title = "Laptop broken", categoryId = laptop, formVersionId = versionId,
            formAnswers = new { addMonitor = false, monitorSize = "27", iban = "ae07 0331 2345 6789 0123 456", serial = " SN-1 " },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var incident = Guid.Parse((string)(await created.JsonAsync())["id"]!);

        // Hidden answers are dropped; formats are stored normalised; personal data is kept apart.
        var view = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/form");
        Assert.Equal(1, (int?)view["form"]!["versionNumber"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"addMonitor":false,"serial":"SN-1","iban":"AE070331234567890123456"}"""), view["answers"]), view["answers"]!.ToJsonString());
        var stored = await database.ScalarAsync<string>("SELECT personal_answers::text FROM ticket_form_responses WHERE ticket_id = @id", ("id", incident));
        Assert.Contains("AE070331234567890123456", stored, StringComparison.Ordinal);
        var audit = await database.AuditAsync(tenant);
        Assert.DoesNotContain(audit, e => e.Metadata?.Contains("AE070331234567890123456", StringComparison.Ordinal) == true);

        // Staff see the answers; their view of personal answers is audited.
        var staffView = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/form");
        Assert.Equal("AE070331234567890123456", (string?)staffView["answers"]!["iban"]);
        Assert.Contains(await database.AuditAsync(tenant), e => e.Action == "forms.personal-data.viewed");

        // A new version: tickets sent before keep theirs; a form filled with the old version is refused.
        var draft = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/forms/{formId}");
        var elements = draft["draft"]!["elements"]!.AsArray();
        elements.Add(JsonSerializerNode(FieldElement(Text("asset", "Asset tag"))));
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/forms/{formId}/draft", new { schema = new { elements } });
        var republished = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{formId}/publish");
        Assert.Equal(2, (int?)republished["publishedVersion"]);
        using var stale = await ReportAsync(requester, new { title = "Laptop broken", categoryId = laptop, formVersionId = versionId, formAnswers = new { serial = "SN-2" } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("form-changed", (string?)(await stale.JsonAsync())["code"]);
        var kept = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/form");
        Assert.Equal(4, kept["fields"]!.AsArray().Count);

        // Versions, and no form at all outside the bound categories.
        Assert.Equal(2, (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/forms/{formId}/versions"))["items"]!.AsArray().Count);
        using var noForm = await ReportAsync(requester, new { title = "Printer", formAnswers = new { serial = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, noForm.StatusCode);
    }

    [Fact]
    public async Task Only_form_managers_build_forms_and_problems_stop_publishing()
    {
        var tenant = await database.CreateTenantAsync("fp");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/forms")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/forms", new { key = "x", name = new { en = "X" } })).StatusCode);

        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key = "broken",
            name = new { en = "Broken" },
            schema = new { elements = new[] { FieldElement(new { key = "size", type = "Dropdown", label = new { en = "Size" } }) } },
        }, HttpStatusCode.Created)).Id();
        var form = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/forms/{id}");
        Assert.Equal("Draft", (string?)form["state"]);
        Assert.Equal(["options-required"], form["problems"]!.AsArray().Select(p => (string)p!["code"]!));

        using var publish = await admin.SendAsync(HttpMethod.Post, $"/api/v1/forms/{id}/publish");
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        Assert.Equal("form-has-problems", (string?)(await publish.JsonAsync())["code"]);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, "/api/v1/forms", new { key = "broken", name = new { en = "Again" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, $"/api/v1/forms/{id}/draft", new { schema = new { elements = 3 } })).StatusCode);

        // Missing Arabic labels are reported (LANG-5).
        var missing = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/translations/missing");
        Assert.Contains("fields.size.label", missing.ToJsonString(), StringComparison.Ordinal);

        // Drafts are deleted; published forms are retired and reactivated.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/forms/{id}")).StatusCode);
        var published = await PublishedFormAsync(admin, "ok", FieldElement(Text("a", "A")));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/forms/{published}")).StatusCode);
        Assert.Equal("Retired", (string?)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{published}/retire"))["state"]);
        Assert.Equal("Published", (string?)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{published}/reactivate"))["state"]);
        var copy = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{published}/copy", new { key = "ok-copy" }, HttpStatusCode.Created);
        Assert.Equal(("Draft", "Form ok"), ((string?)copy["state"], (string?)copy["name"]!["en"]));
    }

    [Fact]
    public async Task A_field_set_change_reaches_every_form_that_uses_it_unless_it_would_break_one()
    {
        var tenant = await database.CreateTenantAsync("fs");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var setId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/field-sets", new
        {
            key = "requester-details", name = new { en = "Requester details", ar = "بيانات مقدم الطلب" },
            schema = new { fields = new[] { Text("fullName", "Full name") } },
        }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/field-sets/{setId}/publish");

        var a = await PublishedFormAsync(admin, "a", new { type = "FieldSet", fieldSetId = setId }, FieldElement(Text("issue", "Issue")));
        var b = await PublishedFormAsync(admin, "b", new { type = "FieldSet", fieldSetId = setId },
            FieldElement(Text("note", "Note", new { rules = new[] { new { action = "Require", match = "All", conditions = new[] { new { field = "fullName", @operator = "NotEmpty" } } } } })));
        await BindAsync(admin, a);
        Assert.Equal(["fullName", "issue"], (await ResolveAsync(requester))["fields"]!.AsArray().Select(f => (string)f!["field"]!["key"]!));

        // A new field reaches the published forms at once.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/field-sets/{setId}/draft", new { schema = new { fields = new[] { Text("fullName", "Full name"), Text("phone", "Mobile", new { validation = new { format = "Phone" } }) } } });
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/field-sets/{setId}/publish");
        Assert.Equal(["fullName", "phone", "issue"], (await ResolveAsync(requester))["fields"]!.AsArray().Select(f => (string)f!["field"]!["key"]!));
        var usage = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/field-sets/{setId}");
        Assert.Equal(2, usage["usedBy"]!.AsArray().Count);

        // A key that a form already has, or removing a field a form's rule looks at, is refused.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/field-sets/{setId}/draft", new { schema = new { fields = new[] { Text("issue", "Issue") } } });
        using var clash = await admin.SendAsync(HttpMethod.Post, $"/api/v1/field-sets/{setId}/publish");
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var body = await clash.JsonAsync();
        Assert.Equal("field-set-breaks-forms", (string?)body["code"]);
        Assert.Equal(["a", "b"], body["details"]!.AsArray().Select(d => (string)d!["key"]!).Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/field-sets/{setId}/retire")).StatusCode);

        // A field set in use cannot be deleted; the published version stays in place meanwhile.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/field-sets/{setId}")).StatusCode);
        Assert.Equal(3, (await ResolveAsync(requester))["fields"]!.AsArray().Count);
        _ = b;
    }

    [Fact]
    public async Task Profile_defaults_fill_the_form_and_read_only_fields_keep_them()
    {
        var tenant = await database.CreateTenantAsync("fd");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(person);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var manager = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        var finance = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/departments", new { name = new { en = "Finance", ar = "المالية" } }, HttpStatusCode.Created)).Id();
        var site = await admin.LocationAsync("Ajman");

        var updated = await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/users/{person.Id}", new
        {
            departmentId = finance.ToString(), managerId = manager.Id.ToString(), locationId = site.ToString(), employeeId = "E-1001", phone = "050 123 4567",
        });
        Assert.Equal(("Finance", "+971501234567", "E-1001"), ((string?)updated["department"]!["name"]!["en"], (string?)updated["phone"], (string?)updated["employeeId"]));

        var form = await PublishedFormAsync(admin, "profile",
            FieldElement(Text("dept", "Department", new { readOnly = true, profileDefault = "Department" })),
            FieldElement(new { key = "boss", type = "User", label = new { en = "Manager" }, profileDefault = "Manager" }),
            FieldElement(new { key = "where", type = "Location", label = new { en = "Location" }, profileDefault = "Location" }),
            FieldElement(Text("mobile", "Mobile", new { profileDefault = "Phone" })),
            FieldElement(Text("staffNo", "Employee number", new { profileDefault = "EmployeeId" })));
        await BindAsync(admin, form);

        var resolved = await ResolveAsync(requester);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse($$"""{"dept":"Finance","boss":"{{manager.Id}}","where":"{{site}}","mobile":"+971501234567","staffNo":"E-1001"}"""),
            resolved["defaults"]), resolved["defaults"]!.ToJsonString());
        Assert.NotNull(resolved["references"]!["users"]![manager.Id.ToString()]);

        // Staff logging for someone see the phone only when their roles may see users' phones.
        var forOther = await ResolveAsync(agent, requesterId: person.Id);
        Assert.False(forOther["defaults"]!.AsObject().ContainsKey("mobile"));
        Assert.Equal("E-1001", (string?)forOther["defaults"]!["staffNo"]);

        using var created = await ReportAsync(requester, new { title = "Printer jam", formAnswers = new { dept = "Other department" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (string)(await created.JsonAsync())["id"]!;
        var view = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{id}/form");
        Assert.Equal("Finance", (string?)view["answers"]!["dept"]);
        Assert.Equal("+971501234567", (string?)view["answers"]!["mobile"]);

        // Profile rules: no circles of managers; departments in use stay.
        var circle = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/users/{manager.Id}", new { managerId = person.Id.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, circle.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/users/{person.Id}", new { phone = "12" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/departments/{finance}")).StatusCode);
        Assert.Equal("""null""", (await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/users/{person.Id}", new { departmentId = "" }))["department"]?.ToJsonString() ?? "null");
    }

    [Fact]
    public async Task Files_and_signatures_are_uploaded_first_and_belong_to_the_ticket_once_sent()
    {
        var tenant = await database.CreateTenantAsync("fu");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var other = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var form = await PublishedFormAsync(admin, "files",
            FieldElement(new { key = "evidence", type = "File", label = new { en = "Evidence" }, required = true, validation = new { fileTypes = new[] { "pdf" }, maxFiles = 2 } }),
            FieldElement(new { key = "sign", type = "Signature", label = new { en = "Signature" }, required = true }));
        await BindAsync(admin, form);

        async Task<Guid> UploadAsync(HttpClient client, string field, string name, byte[] content)
        {
            using var response = await client.PostAsync(new Uri("/api/v1/form-uploads", UriKind.Relative), Upload(field, name, content), Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
            return Guid.Parse((string)(await response.JsonAsync())["id"]!);
        }

        var pdf = await UploadAsync(requester, "evidence", "receipt.pdf", Pdf);
        var png = await UploadAsync(requester, "evidence", "photo.png", Png);
        var drawing = await UploadAsync(requester, "sign", "signature.png", Png);
        var someoneElses = await UploadAsync(other, "evidence", "theirs.pdf", Pdf);
        Assert.Equal(HttpStatusCode.NotFound, (await requester.SendAsync(HttpMethod.Get, $"/api/v1/form-uploads/{someoneElses}")).StatusCode);

        using var refused = await ReportAsync(requester, new { title = "Refund", formAnswers = new { evidence = new[] { png, someoneElses }, sign = new { attachmentId = pdf } } });
        Assert.Equal(["evidence:file-type", "evidence:file-unknown", "sign:file-unknown"], FormErrors(await refused.JsonAsync()));

        using var created = await ReportAsync(requester, new { title = "Refund", formAnswers = new { evidence = new[] { pdf }, sign = new { attachmentId = drawing } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var incident = Guid.Parse((string)(await created.JsonAsync())["id"]!);
        var attachments = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/attachments");
        Assert.Equal(["receipt.pdf", "signature.png"], attachments["items"]!.AsArray().Select(a => (string)a!["fileName"]!).Order(StringComparer.Ordinal));
        var view = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/form");
        Assert.Equal("receipt.pdf", (string?)view["references"]!["files"]![pdf.ToString()]!["fileName"]);

        // A typed signature needs the confirmation; files already sent cannot be sent again.
        using var reused = await ReportAsync(requester, new { title = "Again", formAnswers = new { evidence = new[] { pdf }, sign = new { typedName = "Sara Ali", confirmed = false } } });
        Assert.Equal(["sign:signature-incomplete"], FormErrors(await reused.JsonAsync()));
        using var reusedFile = await ReportAsync(requester, new { title = "Again", formAnswers = new { evidence = new[] { pdf }, sign = new { typedName = "Sara Ali", confirmed = true } } });
        Assert.Equal(["evidence:file-unknown"], FormErrors(await reusedFile.JsonAsync()));

        // Unsent uploads are removed after a day.
        await database.ScalarAsync<int>("UPDATE ticket_attachments SET uploaded_at = now() - interval '2 days' WHERE id IN (@a, @b) RETURNING 1", ("a", png), ("b", someoneElses));
        var job = new FormUploadCleanupJob(_factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<FormUploadCleanupJob>.Instance, TimeProvider.System);
        Assert.Equal(2, await job.RunOnceAsync(Ct));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM ticket_attachments WHERE id IN (@a, @b)", ("a", png), ("b", someoneElses)));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM ticket_attachment_contents WHERE attachment_id IN (@a, @b)", ("a", png), ("b", someoneElses)));
    }

    [Fact]
    public async Task Users_bulk_import_takes_departments_managers_locations_and_phones()
    {
        var tenant = await database.CreateTenantAsync("fb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var boss = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/departments", new { name = new { en = "Finance", ar = "المالية" } }, HttpStatusCode.Created);
        var ajman = await admin.LocationAsync("Ajman");
        await admin.LocationAsync("Main building", ajman);

        var csv = $"email,department,manager,location,employeeId,phone\n{person.Email},المالية,{boss.Email},Ajman > Main building,E-7,+971 4 123 4567\n";
        using var content = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "users.csv" } };
        using var import = await admin.PostAsync(new Uri("/api/v1/bulk/users/import?dryRun=false", UriKind.Relative), content, Ct);
        Assert.True(import.IsSuccessStatusCode, await import.Content.ReadAsStringAsync(Ct));

        var user = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/users/{person.Id}");
        Assert.Equal(("Finance", "Test user " + boss.Subject[..8], "Ajman › Main building", "E-7", "+97141234567"),
            ((string?)user["department"]!["name"]!["en"], (string?)user["manager"]!["displayName"], (string?)user["location"]!["path"]!["en"],
             (string?)user["employeeId"], (string?)user["phone"]));

        var bad = $"email,department\n{person.Email},Nowhere\n";
        using var badContent = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(bad)), "file", "users.csv" } };
        using var refused = await admin.PostAsync(new Uri("/api/v1/bulk/users/import?dryRun=false", UriKind.Relative), badContent, Ct);
        Assert.Contains("department-unknown", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Form_tables_are_isolated_between_tenants()
    {
        var a = await database.CreateTenantAsync("fia");
        var b = await database.CreateTenantAsync("fib");
        using var adminA = await _factory.ForTenant(a).SignedInAsync(database, a, BuiltInRoles.TenantAdmin);
        using var adminB = await _factory.ForTenant(b).SignedInAsync(database, b, BuiltInRoles.TenantAdmin);
        var form = await PublishedFormAsync(adminA, "only-a", FieldElement(Text("x", "X")));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.SendAsync(HttpMethod.Get, $"/api/v1/forms/{form}")).StatusCode);
        Assert.Empty((await adminB.SendOkAsync(HttpMethod.Get, "/api/v1/forms"))["items"]!.AsArray());

        await using var db = TestData.AppContext(database, b);
        Assert.Equal(0, await db.Forms.CountAsync(Ct));
        Assert.Equal(0, await db.FormVersions.CountAsync(Ct));
    }
}
