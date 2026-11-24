using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Data.Identity;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Tests;

/// <summary>Requests to the SLA API.</summary>
internal static class SlaApi
{
    public static async Task<Guid> PolicyAsync(this HttpClient admin, object body) =>
        (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/policies", body, HttpStatusCode.Created)).Id();

    /// <summary>A catch-all policy: P3 responds in <paramref name="response"/> and resolves in <paramref name="resolution"/> minutes; 75 % alerts the assignee and the group's leads.</summary>
    public static Task<Guid> StandardPolicyAsync(this HttpClient admin, int response = 60, int resolution = 240, Guid? calendarId = null, Guid? customerGroupId = null, string name = "Standard") =>
        admin.PolicyAsync(new
        {
            name = new { en = name, ar = "قياسي" },
            calendarId,
            customerGroupId,
            targets = new object[]
            {
                new { priority = 1, responseMinutes = 15, resolutionMinutes = 120 },
                new { priority = 3, responseMinutes = response, resolutionMinutes = resolution },
            },
            escalations = new object[]
            {
                new { percent = 75, notifyAssignee = true, notifyGroupLeads = true },
                new { percent = 100, notifyGroupLeads = true },
            },
        });

    public static async Task<Guid> CalendarAsync(this HttpClient admin, string name, string template = "uae-government", bool? isDefault = null)
    {
        var templates = (await admin.GetFromJsonAsync<JsonArray>("/api/v1/sla/calendar-templates", TestContext.Current.CancellationToken))!;
        var periods = templates.Single(t => (string)t!["key"]! == template)!["periods"]!.DeepClone();
        return (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/calendars",
            new { name = new { en = name, ar = name + " ع" }, timeZone = "Asia/Dubai", isDefault, periods }, HttpStatusCode.Created)).Id();
    }

    public static JsonObject Clock(this JsonObject incident, string metric) =>
        incident["sla"]!.AsArray().Single(c => (string)c!["metric"]! == metric)!.AsObject();

    /// <summary>Runs the SLA timer job once against the API's services (so webhook events are queued), as if it were <paramref name="now"/>.</summary>
    public static Task<int> RunSlaTimersAsync(this ApiFactory factory, Tenant tenant, DateTimeOffset now) =>
        new SlaTimerJob(factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SlaTimerJob>.Instance, TimeProvider.System)
            .RunTenantAsync(tenant.Id, tenant.Slug, now, TestContext.Current.CancellationToken);
}

/// <summary>SLA management (module 8, GOV-7; task B3; product owner decisions 2026-11-24).</summary>
public sealed class SlaApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithMasterKey();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Administrators_keep_calendars_with_UAE_weekends_and_holidays_and_staff_can_read_them()
    {
        var tenant = await database.CreateTenantAsync("cal");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var government = await admin.CalendarAsync("Government hours");
        var calendar = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/sla/calendars/{government}");
        Assert.True((bool)calendar["isDefault"]!); // The first calendar is the default.
        Assert.Equal(5, calendar["periods"]!.AsArray().Count);
        Assert.Equal(("Friday", "07:30", "12:00"), calendar["periods"]!.AsArray().Select(p => ((string)p!["day"]!, (string)p["start"]!, (string)p["end"]!)).Single(p => p.Item1 == "Friday"));

        var holiday = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/sla/calendars/{government}/holidays",
            new { startDate = "2026-12-02", endDate = "2026-12-03", name = new { en = "National Day", ar = "عيد الاتحاد" } }, HttpStatusCode.Created);
        Assert.Equal("2026-12-03", (string)holiday["holidays"]![0]!["endDate"]!);

        // A second default replaces the first; the default cannot be deleted.
        var allDay = await admin.CalendarAsync("Around the clock", "all-day", isDefault: true);
        Assert.False((bool)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/sla/calendars/{government}"))["isDefault"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/sla/calendars/{allDay}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/sla/calendars/{government}")).StatusCode);

        // Refused: unknown time zones, overlapping periods, a last day before the first.
        foreach (var bad in new object[]
                 {
                     new { name = new { en = "Mars" }, timeZone = "Mars/Olympus", periods = new[] { new { day = "Monday", start = "08:00", end = "16:00" } } },
                     new { name = new { en = "Overlap" }, timeZone = "Asia/Dubai", periods = new[] { new { day = "Monday", start = "08:00", end = "12:00" }, new { day = "Monday", start = "11:00", end = "16:00" } } },
                     new { name = new { en = "Backwards" }, timeZone = "Asia/Dubai", periods = new[] { new { day = "Monday", start = "16:00", end = "08:00" } } },
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/sla/calendars", bad)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/sla/calendars/{allDay}/holidays",
            new { startDate = "2026-12-03", endDate = "2026-12-02", name = new { en = "Backwards" } })).StatusCode);

        // Agents read; only sla.manage changes; requesters see nothing.
        Assert.Single((await agent.GetFromJsonAsync<JsonArray>("/api/v1/sla/calendars", Ct))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/sla/calendars/{allDay}/holidays",
            new { startDate = "2027-01-01", name = new { en = "New Year" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/sla/calendars", Ct)).StatusCode);
        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, e => e.Action == "business_calendar.created");
        Assert.Contains(audit, e => e.Action == "business_calendar_holiday.created");
        Assert.Contains(audit, e => e.Action == "business_calendar.deleted");
    }

    [Fact]
    public async Task An_incident_gets_response_and_resolution_clocks_that_follow_its_state()
    {
        var tenant = await database.CreateTenantAsync("clk");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var policy = await admin.StandardPolicyAsync();

        var reported = await requester.CreateAsync(new { title = "The scanner does not scan" });
        Assert.False(reported.ContainsKey("sla")); // Requesters do not see SLA clocks.
        var id = IncidentApi.IdOf(reported);

        var incident = await asAgent.GetIncidentAsync(id);
        var response = incident.Clock("Response");
        Assert.Equal(("Running", 60, policy.ToString()), ((string)response["status"]!, (int)response["targetMinutes"]!, (string)response["policy"]!["id"]!));
        Assert.Null(response["calendar"]); // No calendar yet: around the clock.
        var created = DateTimeOffset.Parse((string)incident["createdAt"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(DateTimeOffset.Parse((string)response["dueAt"]!, System.Globalization.CultureInfo.InvariantCulture), created.AddMinutes(59), created.AddMinutes(61));
        Assert.Equal(240, (int)incident.Clock("Resolution")["targetMinutes"]!);

        await asAgent.SendOkAsync(HttpMethod.Put, IncidentApi.Of(id, "assignee").ToString(), new { assigneeId = agent.Id });
        (await asAgent.MoveAsync(id, "InProgress")).EnsureSuccessStatusCode();
        incident = await asAgent.GetIncidentAsync(id);
        Assert.Equal(("Met", "Stopped"), ((string)incident.Clock("Response")["status"]!, (string)incident.Clock("Response")["state"]!));
        Assert.NotNull(incident["respondedAt"]);

        // Waiting for the requester pauses; waiting for a supplier does not.
        (await asAgent.MoveAsync(id, "OnHold", "awaiting-requester")).EnsureSuccessStatusCode();
        Assert.Equal("Paused", (string)(await asAgent.GetIncidentAsync(id)).Clock("Resolution")["status"]!);
        (await asAgent.MoveAsync(id, "InProgress")).EnsureSuccessStatusCode();
        (await asAgent.MoveAsync(id, "OnHold", "awaiting-supplier")).EnsureSuccessStatusCode();
        Assert.Equal("Running", (string)(await asAgent.GetIncidentAsync(id)).Clock("Resolution")["status"]!);

        (await asAgent.MoveAsync(id, "Resolved", notes: "Driver reinstalled")).EnsureSuccessStatusCode();
        Assert.Equal("Met", (string)(await asAgent.GetIncidentAsync(id)).Clock("Resolution")["status"]!);
        (await requester.MoveAsync(id, "Reopened")).EnsureSuccessStatusCode();
        incident = await asAgent.GetIncidentAsync(id);
        Assert.Equal("Running", (string)incident.Clock("Resolution")["status"]!);
        Assert.Equal("Met", (string)incident.Clock("Response")["status"]!);
        Assert.Contains(await database.AuditAsync(tenant), e => e.Action == "tickets.sla.policy");
    }

    [Fact]
    public async Task The_timer_records_each_threshold_once_alerts_the_right_people_and_sends_webhook_events()
    {
        var tenant = await database.CreateTenantAsync("esc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        var group = await admin.GroupAsync("Service desk", agent);
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{lead.Id}", new { isLead = true });
        await admin.StandardPolicyAsync(response: 60, resolution: 240);
        using var hook = await admin.PostAsJsonAsync(new Uri("/api/v1/integrations/webhooks", UriKind.Relative),
            new { name = "SLA", url = "https://hooks.example.ae/sla", events = new[] { "ticket.sla.threshold", "ticket.sla.breached" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, hook.StatusCode);

        var incident = await asAgent.CreateAsync(new { title = "Email is down", groupId = group, assigneeId = agent.Id });
        var id = IncidentApi.IdOf(incident);
        var start = DateTimeOffset.UtcNow;

        // 50 of 60 minutes: past 75 % of the response target.
        await _factory.RunSlaTimersAsync(tenant, start.AddMinutes(50));
        await using (var db = TestData.AppContext(database, tenant))
        {
            var alert = await db.SlaAlerts.SingleAsync(Ct);
            Assert.Equal((SlaMetric.Response, 75), (alert.Metric, alert.Percent));
            Assert.Equal(new[] { agent.Id, lead.Id }.Order(), alert.RecipientIds.Order());
            Assert.Null(alert.DeliveredAt); // Notifications (B5) deliver it.
            Assert.InRange(alert.ReachedAt, start.AddMinutes(44), start.AddMinutes(46));
        }

        var atRisk = await asAgent.SendOkAsync(HttpMethod.Get, "/api/v1/incidents?sla=atRisk");
        Assert.Equal(id.ToString(), (string)atRisk["items"]![0]!["id"]!);
        Assert.Equal("AtRisk", (string)(await asAgent.GetIncidentAsync(id)).Clock("Response")["status"]!);

        // Running again at the same time records nothing new.
        await _factory.RunSlaTimersAsync(tenant, start.AddMinutes(50));
        // 70 minutes: response breached; 180 minutes is 75 % of the resolution target.
        await _factory.RunSlaTimersAsync(tenant, start.AddMinutes(70));
        await _factory.RunSlaTimersAsync(tenant, start.AddMinutes(185));
        await using (var db = TestData.AppContext(database, tenant))
        {
            var alerts = await db.SlaAlerts.OrderBy(a => a.ReachedAt).Select(a => new { a.Metric, a.Percent }).ToListAsync(Ct);
            Assert.Equal([(SlaMetric.Response, 75), (SlaMetric.Response, 100), (SlaMetric.Resolution, 75)], alerts.Select(a => (a.Metric, a.Percent)));
            var response = await db.TicketSlas.SingleAsync(c => c.Metric == SlaMetric.Response, Ct);
            Assert.InRange(response.BreachedAt!.Value, start.AddMinutes(59), start.AddMinutes(61));
            Assert.Equal(new[] { "ticket.sla.breached", "ticket.sla.threshold", "ticket.sla.threshold", "ticket.sla.threshold" },
                (await db.WebhookDeliveries.Select(d => d.EventType).ToListAsync(Ct)).Order());
            Assert.DoesNotContain(await db.WebhookDeliveries.Select(d => d.Payload).ToListAsync(Ct), p => p.Contains('@', StringComparison.Ordinal));
        }

        var breached = await asAgent.SendOkAsync(HttpMethod.Get, "/api/v1/incidents?sla=breached&open=true");
        Assert.Equal(1, (int)breached["totalCount"]!);
        Assert.Equal("Breached", (string)(await asAgent.GetIncidentAsync(id)).Clock("Response")["status"]!);
        var audit = await database.AuditAsync(tenant);
        Assert.Equal(3, audit.Count(e => e.Action == "sla_alert.created" && e.ActorType == "System"));

        // A breached response still stops when work starts, and stays breached.
        (await asAgent.MoveAsync(id, "InProgress")).EnsureSuccessStatusCode();
        var clock = (await asAgent.GetIncidentAsync(id)).Clock("Response");
        Assert.Equal(("Breached", "Stopped"), ((string)clock["status"]!, (string)clock["state"]!));
    }

    [Fact]
    public async Task Customer_groups_by_domain_or_by_hand_get_their_own_policy_first()
    {
        var tenant = await database.CreateTenantAsync("vip");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var member = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asMember = _factory.ForTenant(tenant).As(member);
        var vip = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/customer-groups",
            new { name = new { en = "VIP", ar = "كبار العملاء" }, emailDomains = new[] { "@Ministry.GOV.ae" } }, HttpStatusCode.Created));
        Assert.Equal("ministry.gov.ae", (string)vip["emailDomains"]![0]!);
        var vipId = vip.Id();
        await admin.StandardPolicyAsync(response: 120, resolution: 480);
        var vipPolicy = await admin.StandardPolicyAsync(response: 10, resolution: 60, customerGroupId: vipId, name: "VIP");
        // Tried last until moved first.
        var standard = (await admin.GetFromJsonAsync<JsonArray>("/api/v1/sla/policies", Ct))![0]!.AsObject().Id();
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/sla/policies/order", new { ids = new[] { vipPolicy, standard } });

        var before = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/policies/test", new { priority = 3, requesterId = member.Id });
        Assert.Equal(standard.ToString(), (string)before["policy"]!["id"]!);
        Assert.Equal(120, (int)before["response"]!["minutes"]!);

        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/sla/customer-groups/{vipId}/members/{member.Id}", null);
        var after = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/policies/test", new { priority = 3, requesterId = member.Id });
        Assert.Equal(vipPolicy.ToString(), (string)after["policy"]!["id"]!);
        Assert.Equal("VIP", (string)after["customerGroups"]![0]!["name"]!["en"]!);

        var incident = await admin.GetIncidentAsync(IncidentApi.IdOf(await asMember.CreateAsync(new { title = "Portal login fails" })));
        Assert.Equal(10, (int)incident.Clock("Response")["targetMinutes"]!);

        // By domain: the test users' domain.
        var other = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/sla/customer-groups/{vipId}",
            new { name = new { en = "VIP", ar = "كبار العملاء" }, emailDomains = new[] { other.Email.Split('@')[1] } });
        var byDomain = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/policies/test", new { priority = 3, requesterId = other.Id });
        Assert.Equal(vipPolicy.ToString(), (string)byDomain["policy"]!["id"]!);

        // In use: the group cannot be deleted; bad domains are refused.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/sla/customer-groups/{vipId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, $"/api/v1/sla/customer-groups/{vipId}",
            new { name = new { en = "VIP" }, emailDomains = new[] { "not a domain" } })).StatusCode);
    }

    [Fact]
    public async Task A_priority_change_takes_the_new_target_and_keeps_the_elapsed_time()
    {
        var tenant = await database.CreateTenantAsync("pri");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.StandardPolicyAsync(response: 60, resolution: 240);
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Payroll export fails" }));
        await database.ExecuteAsOwnerAsync("UPDATE ticket_slas SET elapsed_seconds = 1200 WHERE ticket_id = @id", ("id", id));

        // Impact 1 × urgency 1 is P1: 15 minutes to respond, 20 already used.
        await admin.SendOkAsync(HttpMethod.Patch, IncidentApi.Of(id).ToString(), new { impact = 1, urgency = 1 });
        var clock = (await admin.GetIncidentAsync(id)).Clock("Response");
        Assert.Equal(15, (int)clock["targetMinutes"]!);
        Assert.True((long)clock["elapsedSeconds"]! >= 1200);
        Assert.Equal("Breached", (string)clock["status"]!);
    }

    [Fact]
    public async Task The_groups_calendar_comes_before_the_policys_and_the_tenant_default()
    {
        var tenant = await database.CreateTenantAsync("gcal");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var tenantDefault = await admin.CalendarAsync("Government", "uae-government");
        var night = await admin.CalendarAsync("Night shift", "all-day");
        var policyCalendar = await admin.CalendarAsync("Private sector", "uae-private");
        await admin.StandardPolicyAsync(calendarId: policyCalendar);
        var group = await admin.GroupAsync("Night desk", agent);

        var first = await admin.GetIncidentAsync(IncidentApi.IdOf(await admin.CreateAsync(new { title = "No group" })));
        Assert.Equal(policyCalendar.ToString(), (string)first.Clock("Response")["calendar"]!["id"]!);

        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/calendar", new { calendarId = night });
        var second = await admin.GetIncidentAsync(IncidentApi.IdOf(await admin.CreateAsync(new { title = "Night desk", groupId = group })));
        Assert.Equal(night.ToString(), (string)second.Clock("Response")["calendar"]!["id"]!);

        // A calendar a group uses cannot be deleted.
        var calendarInUse = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/sla/calendars/{night}");
        Assert.Equal(HttpStatusCode.Conflict, calendarInUse.StatusCode);
        Assert.NotEqual(Guid.Empty, tenantDefault);
    }

    [Fact]
    public async Task Policy_changes_reach_open_tickets_through_the_timer()
    {
        var tenant = await database.CreateTenantAsync("chg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Before any policy" }));
        Assert.Equal("None", (string)(await admin.GetIncidentAsync(id)).Clock("Response")["status"]!);

        await admin.StandardPolicyAsync(response: 30, resolution: 90);
        await _factory.RunSlaTimersAsync(tenant, DateTimeOffset.UtcNow.AddSeconds(5));
        var clock = (await admin.GetIncidentAsync(id)).Clock("Response");
        Assert.Equal(("Running", 30), ((string)clock["status"]!, (int)clock["targetMinutes"]!));
    }

    [Fact]
    public async Task Policies_are_validated()
    {
        var tenant = await database.CreateTenantAsync("polv");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        foreach (var bad in new object[]
                 {
                     new { name = new { en = "No targets" }, targets = Array.Empty<object>() },
                     new { name = new { en = "Priority 7" }, targets = new[] { new { priority = 7, responseMinutes = 10 } } },
                     new { name = new { en = "Response after resolution" }, targets = new[] { new { priority = 1, responseMinutes = 100, resolutionMinutes = 50 } } },
                     new { name = new { en = "Nobody to alert" }, targets = new[] { new { priority = 1, responseMinutes = 10 } }, escalations = new[] { new { percent = 75 } } },
                     new { name = new { en = "Threshold 500" }, targets = new[] { new { priority = 1, responseMinutes = 10 } }, escalations = new[] { new { percent = 500, notifyAssignee = true } } },
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/sla/policies", bad)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await lead.SendAsync(HttpMethod.Post, "/api/v1/sla/policies",
            new { name = new { en = "Lead" }, targets = new[] { new { priority = 1, responseMinutes = 10 } } })).StatusCode);
        var id = await admin.StandardPolicyAsync();
        var policy = (await lead.GetFromJsonAsync<JsonArray>("/api/v1/sla/policies", Ct))!.Single()!;
        Assert.Equal(2, policy["escalations"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/sla/policies/{id}")).StatusCode);
    }

    [Fact]
    public async Task On_hold_reasons_say_whether_they_pause_and_auto_close_can_count_working_days()
    {
        var tenant = await database.CreateTenantAsync("ahs");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var settings = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/settings/incidents");
        var reasons = settings["onHoldReasons"]!.AsArray();
        Assert.True((bool)reasons.Single(r => (string)r!["key"]! == "awaiting-requester")!["pausesSla"]!);
        Assert.False((bool)reasons.Single(r => (string)r!["key"]! == "awaiting-supplier")!["pausesSla"]!);
        Assert.False((bool)settings["autoCloseBusinessDays"]!);

        // Working days need a default calendar; the government week has no Saturday or Sunday.
        await admin.CalendarAsync("Government", "uae-government");
        var body = settings.DeepClone().AsObject();
        body["autoCloseBusinessDays"] = true;
        body["autoCloseDays"] = 2;
        foreach (var reason in body["onHoldReasons"]!.AsArray())
        {
            reason!["pausesSla"] = (string)reason["key"]! == "awaiting-supplier" || (bool)reason["pausesSla"]!;
        }

        body["priorityMatrix"] = new JsonObject
        {
            ["impacts"] = new JsonArray([.. settings["priorityMatrix"]!["impacts"]!.AsArray().Select(n => n!["name"]!.DeepClone())]),
            ["urgencies"] = new JsonArray([.. settings["priorityMatrix"]!["urgencies"]!.AsArray().Select(n => n!["name"]!.DeepClone())]),
            ["priorities"] = new JsonArray([.. settings["priorityMatrix"]!["priorities"]!.AsArray().Select(n => n!["name"]!.DeepClone())]),
            ["matrix"] = settings["priorityMatrix"]!["matrix"]!.DeepClone(),
        };
        body.Remove("updatedAt");
        var saved = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/incidents", body);
        Assert.True((bool)saved["autoCloseBusinessDays"]!);
        Assert.True((bool)saved["onHoldReasons"]!.AsArray().Single(r => (string)r!["key"]! == "awaiting-supplier")!["pausesSla"]!);

        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Resolved on a Thursday" }));
        (await admin.MoveAsync(id, "Resolved", notes: "Done")).EnsureSuccessStatusCode();
        // Thursday 10:00 Dubai, 26 Nov 2026: two working days later is Monday 30 Nov 10:00 (Friday counts, the weekend does not).
        await database.ExecuteAsOwnerAsync("UPDATE tickets SET resolved_at = '2026-11-26T06:00:00Z' WHERE id = @id", ("id", id));
        var job = new TicketAutoCloseJob(_factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TicketAutoCloseJob>.Instance,
            new FixedTime(new DateTimeOffset(2026, 11, 29, 20, 0, 0, TimeSpan.Zero)));
        Assert.Equal(0, await job.CloseTenantAsync(tenant.Id, tenant.Slug, Ct));
        job = new TicketAutoCloseJob(_factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TicketAutoCloseJob>.Instance,
            new FixedTime(new DateTimeOffset(2026, 11, 30, 6, 1, 0, TimeSpan.Zero)));
        Assert.Equal(1, await job.CloseTenantAsync(tenant.Id, tenant.Slug, Ct));
    }

    [Fact]
    public async Task Sla_tables_are_isolated_between_tenants()
    {
        var first = await database.CreateTenantAsync("slaiso");
        var second = await database.CreateTenantAsync("slaiso");
        using var admin = await _factory.ForTenant(first).SignedInAsync(database, first, BuiltInRoles.TenantAdmin);
        await admin.CalendarAsync("Government");
        await admin.StandardPolicyAsync();
        await admin.CreateAsync(new { title = "Tenant one" });
        await using var db = TestData.AppContext(database, second);
        Assert.False(await db.BusinessCalendars.AnyAsync(Ct));
        Assert.False(await db.BusinessCalendarPeriods.AnyAsync(Ct));
        Assert.False(await db.SlaPolicies.AnyAsync(Ct));
        Assert.False(await db.SlaPolicyTargets.AnyAsync(Ct));
        Assert.False(await db.TicketSlas.AnyAsync(Ct));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
