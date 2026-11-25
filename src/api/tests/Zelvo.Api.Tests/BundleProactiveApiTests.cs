using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Api.Catalog;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Bundles and proactive services (task C5, module 2) against PostgreSQL.</summary>
public sealed class BundleProactiveApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static JsonObject Text(string en) => new() { ["en"] = en, ["ar"] = en + " (ar)" };

    private static async Task<Guid> ServiceAsync(HttpClient author, HttpClient reviewer, string code, JsonObject card)
    {
        var id = (await author.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code }, HttpStatusCode.Created)).Id();
        var draft = await author.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{id}/draft", new { card });
        Assert.Empty(draft["problems"]!.AsArray());
        await author.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/submit");
        await reviewer.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/approve");
        return id;
    }

    private static async Task<Guid> FormAsync(HttpClient admin, string key, params (string Key, string Type, bool Required)[] fields)
    {
        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key, name = new { en = key },
            schema = new { elements = fields.Select(f => new { type = "Field", field = new { key = f.Key, type = f.Type, label = new { en = f.Key, ar = f.Key }, required = f.Required } }).ToArray() },
        }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{id}/publish");
        return id;
    }

    private static async Task<Guid> GroupAsync(HttpClient admin, string name, params TestUser[] members)
    {
        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = Text(name) }, HttpStatusCode.Created)).Id();
        foreach (var member in members)
        {
            await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{id}/members/{member.Id}", new { isLead = false });
        }

        return id;
    }

    private Task<int> RunJobsAsync(Tenant tenant, DateTimeOffset? now = null) =>
        _factory.Services.GetRequiredService<CatalogJobs>().RunTenantAsync(tenant.Id, tenant.Slug, now ?? DateTimeOffset.UtcNow, Ct);

    private static JsonObject Part(JsonObject bundle, string code) =>
        bundle["parts"]!.AsArray().Select(p => p!.AsObject()).Single(p => (string?)p["service"]!["code"] == code);

    [Fact]
    public async Task A_bundle_asks_one_combined_form_makes_a_part_per_service_and_completes_with_its_parts()
    {
        var tenant = await database.CreateTenantAsync("bu");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var group = await GroupAsync(admin, "Service desk", agentUser);

        // The laptop and the email account both ask for the start date: it is answered once.
        var laptopForm = await FormAsync(admin, "laptop", ("startDate", "Date", true), ("model", "Text", true));
        var emailForm = await FormAsync(admin, "mailbox", ("startDate", "Date", true), ("alias", "Text", false));
        var fulfilment = new JsonObject { ["groupId"] = group.ToString() };
        var laptop = await ServiceAsync(admin, publisher, "IT-LAPTOP", new JsonObject
        {
            ["name"] = Text("Laptop"), ["channels"] = new JsonArray("Portal"), ["formId"] = laptopForm.ToString(), ["fulfilment"] = fulfilment.DeepClone(),
        });
        var email = await ServiceAsync(admin, publisher, "IT-EMAIL", new JsonObject
        {
            ["name"] = Text("Email account"), ["channels"] = new JsonArray("Portal"), ["formId"] = emailForm.ToString(), ["fulfilment"] = fulfilment.DeepClone(),
        });
        var badge = await ServiceAsync(admin, publisher, "FAC-BADGE", new JsonObject
        {
            ["name"] = Text("Access card"), ["channels"] = new JsonArray("Portal"), ["fulfilment"] = fulfilment.DeepClone(),
        });
        var parking = await ServiceAsync(admin, publisher, "FAC-PARK", new JsonObject
        {
            ["name"] = Text("Parking"), ["channels"] = new JsonArray("Portal"), ["fulfilment"] = fulfilment.DeepClone(),
        });

        // A bundle has no fee, group or approvals of its own, and holds no other bundle.
        var badId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "HR-BAD" }, HttpStatusCode.Created)).Id();
        var bad = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{badId}/draft", new
        {
            card = new JsonObject
            {
                ["name"] = Text("Bad"), ["channels"] = new JsonArray("Portal"), ["kind"] = "Bundle",
                ["fee"] = new JsonObject { ["paid"] = true, ["amountAed"] = 10 },
                ["fulfilment"] = fulfilment.DeepClone(),
                ["bundle"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["serviceId"] = laptop.ToString(), ["parallelWithPrevious"] = true }, new JsonObject { ["serviceId"] = laptop.ToString() }) },
            },
        });
        var codes = bad["problems"]!.AsArray().Select(p => (string)p!["code"]!).ToList();
        Assert.Contains("bundle-has-fee", codes);
        Assert.Contains("bundle-has-fulfilment", codes);
        Assert.Contains("first-item-parallel", codes);
        Assert.Contains("service-duplicate", codes);

        // Laptop and email together, then the access card; parking is optional and not chosen.
        var bundle = await ServiceAsync(admin, publisher, "HR-JOIN", new JsonObject
        {
            ["name"] = Text("New employee onboarding"), ["channels"] = new JsonArray("Portal"), ["kind"] = "Bundle",
            ["bundle"] = new JsonObject
            {
                ["items"] = new JsonArray(
                    new JsonObject { ["serviceId"] = laptop.ToString() },
                    new JsonObject { ["serviceId"] = email.ToString(), ["parallelWithPrevious"] = true },
                    new JsonObject { ["serviceId"] = badge.ToString() },
                    new JsonObject { ["serviceId"] = parking.ToString(), ["required"] = false, ["parallelWithPrevious"] = true }),
            },
        });

        var page = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/catalog/published/{bundle}");
        Assert.Equal("Bundle", (string?)page["kind"]);
        Assert.Equal([0, 0, 1, 1], page["bundleItems"]!.AsArray().Select(i => (int)i!["stage"]!));
        var combined = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/catalog/published/{bundle}/bundle-form");
        var parts = combined["parts"]!.AsArray();
        Assert.Equal(4, parts.Count);
        string VersionOf(Guid service) => (string)parts.Single(p => (string?)p!["serviceId"] == service.ToString())!["form"]!["form"]!["versionId"]!;

        // A field no form has is refused.
        using (var unknown = await requester.SendAsync(HttpMethod.Post, "/api/v1/service-requests", new
        {
            serviceId = bundle, formAnswers = new { startDate = "2027-01-10", model = "14 inch", shoeSize = 42 },
            items = new object[] { new { serviceId = laptop, formVersionId = VersionOf(laptop) }, new { serviceId = email, formVersionId = VersionOf(email) } },
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        var created = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new
        {
            serviceId = bundle,
            formAnswers = new { startDate = "2027-01-10", model = "14 inch" },
            items = new object[] { new { serviceId = laptop, formVersionId = VersionOf(laptop) }, new { serviceId = email, formVersionId = VersionOf(email) } },
        }, HttpStatusCode.Created);
        Assert.True((bool)created["isBundle"]!);
        Assert.Equal(3, created["parts"]!.AsArray().Count);
        Assert.False((bool)Part(created, "IT-LAPTOP")["held"]!);
        Assert.False((bool)Part(created, "IT-EMAIL")["held"]!);
        Assert.True((bool)Part(created, "FAC-BADGE")["held"]!);
        var bundleId = created.Id();

        await using (var db = TestData.AppContext(database, tenant))
        {
            // Each part keeps its own form's answers: the shared start date in both.
            var laptopPart = Guid.Parse((string)Part(created, "IT-LAPTOP")["id"]!);
            var emailPart = Guid.Parse((string)Part(created, "IT-EMAIL")["id"]!);
            var answers = await db.TicketFormResponses.AsNoTracking().Where(f => f.TicketId == laptopPart || f.TicketId == emailPart).ToListAsync(Ct);
            Assert.All(answers, a => Assert.Contains("2027-01-10", a.Answers, StringComparison.Ordinal));
            Assert.DoesNotContain(answers, a => a.TicketId == emailPart && a.Answers.Contains("model", StringComparison.Ordinal));
            // One "received" for the bundle, none for its parts; the held part has no running clock.
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "request.received" && n.RecipientId == requesterUser.Id, Ct));
            var badgePart = Guid.Parse((string)Part(created, "FAC-BADGE")["id"]!);
            Assert.All(await db.TicketSlas.AsNoTracking().Where(s => s.TicketId == badgePart).ToListAsync(Ct), c => Assert.Equal(Zelvo.Data.Sla.SlaClockState.Paused, c.State));
            Assert.False(await db.TicketSlas.AnyAsync(s => s.TicketId == bundleId, Ct));
        }

        // A held part and the bundle itself are not changed by hand.
        using (var held = await agent.SendAsync(HttpMethod.Post, $"/api/v1/service-requests/{(string)Part(created, "FAC-BADGE")["id"]!}/transitions", new { state = "Resolved", resolutionNotes = "x" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        }

        using (var managed = await agent.SendAsync(HttpMethod.Post, $"/api/v1/service-requests/{bundleId}/transitions", new { state = "Resolved", resolutionNotes = "x" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, managed.StatusCode);
        }

        // The first stage done: the access card starts.
        foreach (var code in new[] { "IT-LAPTOP", "IT-EMAIL" })
        {
            await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{(string)Part(created, code)["id"]!}/transitions", new { state = "Resolved", resolutionNotes = "Done." });
        }

        var tracked = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{bundleId}");
        Assert.False((bool)Part(tracked, "FAC-BADGE")["held"]!);
        Assert.Equal("New", (string?)Part(tracked, "FAC-BADGE")["state"]);
        Assert.Equal("InProgress", (string?)tracked["state"]);

        // My requests can leave the parts out.
        var mine = await requester.SendOkAsync(HttpMethod.Get, "/api/v1/service-requests?mine=true&parts=false");
        Assert.Equal([bundleId.ToString()], mine["items"]!.AsArray().Select(i => (string?)i!["id"]));

        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{(string)Part(tracked, "FAC-BADGE")["id"]!}/transitions", new { state = "Resolved", resolutionNotes = "Card printed." });
        var done = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{bundleId}");
        Assert.Equal("Resolved", (string?)done["state"]);
        Assert.Contains((string)Part(done, "FAC-BADGE")["number"]!, (string?)done["resolutionNotes"], StringComparison.Ordinal);
        await using var check = TestData.AppContext(database, tenant);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.EventType == "request.resolved" && n.TicketId == bundleId, Ct));
    }

    [Fact]
    public async Task An_event_from_another_system_starts_a_proactive_request_once()
    {
        var tenant = await database.CreateTenantAsync("pe");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var group = await GroupAsync(admin, "HR desk");
        var form = await FormAsync(admin, "joiner", ("startDate", "Date", true));
        var service = await ServiceAsync(admin, publisher, "HR-WELCOME", new JsonObject
        {
            ["name"] = Text("Welcome pack"), ["channels"] = new JsonArray("Portal"), ["formId"] = form.ToString(),
            ["fulfilment"] = new JsonObject { ["groupId"] = group.ToString() },
            ["proactive"] = new JsonObject
            {
                ["enabled"] = true, ["mode"] = "AutoStart",
                ["triggers"] = new JsonArray(new JsonObject { ["key"] = "newJoiner", ["kind"] = "Event" }),
            },
        });

        var (key, _) = await admin.CreateApiKeyAsync(BuiltInRoles.TenantAdmin, "proactive.trigger");
        using var hr = _factory.CreateClient();
        hr.DefaultRequestHeaders.Add("X-Zelvo-Tenant", tenant.Slug);
        hr.DefaultRequestHeaders.Add("X-Api-Key", key);
        var body = new { service = "hr-welcome", trigger = "newJoiner", personEmail = person.Email!.ToUpperInvariant(), reference = "HR-4711", answers = new { startDate = "2027-02-01", salary = 1 } };
        var started = await hr.SendOkAsync(HttpMethod.Post, "/api/v1/proactive/events", body, HttpStatusCode.Created);
        Assert.Equal("Started", (string?)started["state"]);
        var ticketId = Guid.Parse((string)started["request"]!["id"]!);
        var again = await hr.SendOkAsync(HttpMethod.Post, "/api/v1/proactive/events", body);
        Assert.Equal((string?)started["id"], (string?)again["id"]);

        await using var db = TestData.AppContext(database, tenant);
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, Ct);
        Assert.Equal((TicketChannel.Proactive, person.Id, group), (ticket.Channel, ticket.RequesterId, ticket.GroupId!.Value));
        Assert.Equal(1, await db.Tickets.CountAsync(t => t.RequesterId == person.Id, Ct));
        var answers = await db.TicketFormResponses.AsNoTracking().SingleAsync(f => f.TicketId == ticketId, Ct);
        Assert.Contains("2027-02-01", answers.Answers, StringComparison.Ordinal);
        Assert.Equal(service, (await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.TicketId == ticketId, Ct)).ServiceId);

        // Without the permission, or for a trigger the service does not have, nothing happens.
        var (plain, _) = await admin.CreateApiKeyAsync(BuiltInRoles.TenantAdmin, "requests.read");
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Zelvo-Tenant", tenant.Slug);
        other.DefaultRequestHeaders.Add("X-Api-Key", plain);
        using (var denied = await other.PostAsJsonAsync(new Uri("/api/v1/proactive/events", UriKind.Relative), body, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var wrong = await hr.PostAsJsonAsync(new Uri("/api/v1/proactive/events", UriKind.Relative), new { service = "HR-WELCOME", trigger = "nope", personId = person.Id, reference = "x" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }
    }

    [Fact]
    public async Task A_date_on_an_earlier_request_offers_the_service_which_the_person_confirms_or_declines()
    {
        var tenant = await database.CreateTenantAsync("pd");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var personUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var person = _factory.ForTenant(tenant).As(personUser);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var group = await GroupAsync(admin, "Licensing", agentUser);
        var licenceForm = await FormAsync(admin, "licence", ("licenceNumber", "Text", true), ("expiryDate", "Date", true));
        var licence = await ServiceAsync(admin, publisher, "LIC-ISSUE", new JsonObject
        {
            ["name"] = Text("Trade licence"), ["channels"] = new JsonArray("Portal"), ["formId"] = licenceForm.ToString(),
            ["fulfilment"] = new JsonObject { ["groupId"] = group.ToString() },
        });

        // A date field that does not exist is a problem.
        var renewId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "LIC-RENEW" }, HttpStatusCode.Created)).Id();
        JsonObject RenewCard(string field) => new()
        {
            ["name"] = Text("Licence renewal"), ["channels"] = new JsonArray("Portal"), ["formId"] = licenceForm.ToString(),
            ["fulfilment"] = new JsonObject { ["groupId"] = group.ToString() },
            ["proactive"] = new JsonObject
            {
                ["enabled"] = true, ["mode"] = "Offer", ["offerDays"] = 10,
                ["triggers"] = new JsonArray(new JsonObject
                {
                    ["key"] = "beforeExpiry", ["kind"] = "Date", ["sourceServiceId"] = licence.ToString(), ["sourceFieldKey"] = field, ["offsetDays"] = -30,
                }),
            },
        };
        var wrongField = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{renewId}/draft", new { card = RenewCard("licenceNumber") });
        Assert.Contains("date-field-unknown", wrongField["problems"]!.AsArray().Select(p => (string?)p!["code"]));
        var draft = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{renewId}/draft", new { card = RenewCard("expiryDate") });
        Assert.Empty(draft["problems"]!.AsArray());
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{renewId}/submit");
        await publisher.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{renewId}/approve");

        // The person's licence expires in 20 days: the renewal (30 days before) is due now.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var versionId = (string?)(await person.SendOkAsync(HttpMethod.Get, $"/api/v1/catalog/published/{licence}/request-form"))["form"]!["versionId"];
        var issued = (await person.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new
        {
            serviceId = licence, formVersionId = versionId, formAnswers = new { licenceNumber = "TL-1001", expiryDate = today.AddDays(20).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
        }, HttpStatusCode.Created)).Id();
        Assert.Equal(0, await RunJobsAsync(tenant));
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{issued}/transitions", new { state = "Resolved", resolutionNotes = "Issued." });

        Assert.True(await RunJobsAsync(tenant) > 0);
        var offers = await person.ArrayAsync("/api/v1/me/offers");
        var offer = offers.Single()!.AsObject();
        Assert.Equal("LIC-RENEW", (string?)offer["service"]!["code"]);
        var full = await person.SendOkAsync(HttpMethod.Get, $"/api/v1/me/offers/{(string)offer["id"]!}");
        Assert.Equal("TL-1001", (string?)full["answers"]!["licenceNumber"]);
        Assert.Equal(0, await RunJobsAsync(tenant));

        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "proactive.offered" && n.RecipientId == personUser.Id, Ct));
        }

        // Someone else cannot see or accept it.
        using var stranger = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using (var hidden = await stranger.SendAsync(HttpMethod.Get, $"/api/v1/me/offers/{(string)offer["id"]!}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        var renewVersion = (string?)(await person.SendOkAsync(HttpMethod.Get, $"/api/v1/catalog/published/{renewId}/request-form"))["form"]!["versionId"];
        var accepted = await person.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new
        {
            serviceId = renewId, offerId = (string)offer["id"]!, formVersionId = renewVersion,
            formAnswers = new { licenceNumber = "TL-1001", expiryDate = today.AddYears(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
        }, HttpStatusCode.Created);
        Assert.Empty(await person.ArrayAsync("/api/v1/me/offers"));
        var runs = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/proactive/runs?serviceId={renewId}");
        var run = runs["items"]!.AsArray().Single()!;
        Assert.Equal(("Accepted", (string?)accepted["number"]), ((string?)run["state"], (string?)run["request"]!["number"]));

        // An offer that is declined, and one that expires.
        await using (var db = TestData.AppContext(database, tenant))
        {
            db.ProactiveRuns.AddRange(
                new ProactiveRun { ServiceId = renewId, TriggerKey = "beforeExpiry", Kind = ProactiveTriggerKind.Date, PersonId = personUser.Id, Occurrence = "a", DueOn = today, State = ProactiveRunState.Offered, OfferedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(3) },
                new ProactiveRun { ServiceId = renewId, TriggerKey = "beforeExpiry", Kind = ProactiveTriggerKind.Date, PersonId = personUser.Id, Occurrence = "b", DueOn = today, State = ProactiveRunState.Offered, OfferedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync(Ct);
        }

        var open = await person.ArrayAsync("/api/v1/me/offers");
        Assert.Equal(2, open.Count);
        var declined = await person.SendOkAsync(HttpMethod.Post, $"/api/v1/me/offers/{(string)open[1]!["id"]!}/decline");
        Assert.Equal("Declined", (string?)declined["state"]);
        await RunJobsAsync(tenant, DateTimeOffset.UtcNow.AddDays(2));
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.Equal(ProactiveRunState.Expired, (await db.ProactiveRuns.AsNoTracking().SingleAsync(r => r.Occurrence == "b", Ct)).State);
        }
    }

    [Fact]
    public async Task A_schedule_offers_the_service_to_the_people_it_names_once_per_occurrence()
    {
        var tenant = await database.CreateTenantAsync("ps");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var a = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var b = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var outsider = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var drivers = await GroupAsync(admin, "Drivers", a, b);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await ServiceAsync(admin, publisher, "HSE-CHECK", new JsonObject
        {
            ["name"] = Text("Yearly medical check"), ["channels"] = new JsonArray("Portal"),
            ["proactive"] = new JsonObject
            {
                ["enabled"] = true, ["mode"] = "AutoStart",
                ["triggers"] = new JsonArray(new JsonObject
                {
                    ["key"] = "yearly", ["kind"] = "Schedule", ["startOn"] = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["every"] = 1, ["unit"] = "Year", ["people"] = new JsonObject { ["groupIds"] = new JsonArray(drivers.ToString()) },
                }),
            },
        });

        await RunJobsAsync(tenant);
        await RunJobsAsync(tenant);
        await using var db = TestData.AppContext(database, tenant);
        var runs = await db.ProactiveRuns.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(ProactiveRunState.Started, r.State));
        Assert.DoesNotContain(runs, r => r.PersonId == outsider.Id);
        Assert.Equal(2, await db.Tickets.CountAsync(t => t.Channel == TicketChannel.Proactive, Ct));
    }

    [Theory]
    [InlineData("2026-01-31", "Month", 1, "2026-03-15", "2026-02-28")]
    [InlineData("2026-01-10", "Week", 2, "2026-01-30", "2026-01-24")]
    [InlineData("2025-06-01", "Year", 1, "2026-05-31", "2025-06-01")]
    [InlineData("2026-02-01", "Day", 1, "2026-01-31", null)]
    public void Schedules_find_their_latest_occurrence(string start, string unit, int every, string today, string? expected)
    {
        var trigger = new ProactiveTrigger { Kind = ProactiveTriggerKind.Schedule, StartOn = DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), Every = every, Unit = Enum.Parse<ScheduleUnit>(unit) };
        var found = ProactiveEngine.LatestOccurrence(trigger, DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, found?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }
}
