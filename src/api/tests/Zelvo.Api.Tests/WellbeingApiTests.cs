using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Api.Privacy;
using Zelvo.Api.Wellbeing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Agent wellbeing (module 21, task K4) against PostgreSQL.</summary>
public sealed class WellbeingApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithEmailSender(new CapturingEmailSender());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private Task<int> RunAsync(Tenant tenant) =>
        _factory.Services.GetRequiredService<WellbeingJobs>().RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct);

    [Fact]
    public async Task Team_leads_see_their_agents_by_name_others_see_totals_and_agents_see_their_own_figures()
    {
        var tenant = await database.CreateTenantAsync("k4wb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var busyUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var calmUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var leadUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        var executiveUser = await database.CreateUserAsync(tenant, BuiltInRoles.ExecutiveAuditor);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        await database.ExecuteAsOwnerAsync("UPDATE users SET display_name = 'Zayed Busy' WHERE id = @u", ("u", busyUser.Id));
        await database.ExecuteAsOwnerAsync("UPDATE users SET display_name = 'Amna Calm' WHERE id = @u", ("u", calmUser.Id));
        await database.ExecuteAsOwnerAsync("UPDATE users SET display_name = 'Hessa Lead' WHERE id = @u", ("u", leadUser.Id));
        using var busy = _factory.ForTenant(tenant).As(busyUser);
        using var calm = _factory.ForTenant(tenant).As(calmUser);
        using var lead = _factory.ForTenant(tenant).As(leadUser);
        using var executive = _factory.ForTenant(tenant).As(executiveUser);

        // The desk works one hour a week on a day that is not today, so today's work is after hours.
        var dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
        var otherDay = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, dubai).AddDays(3).DayOfWeek;
        var calendar = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/calendars", new
        {
            name = new { en = "One hour a week", ar = "ساعة في الأسبوع" }, timeZone = "Asia/Dubai", isDefault = true,
            periods = new[] { new { day = otherDay.ToString(), start = "09:00", end = "10:00" } },
        }, HttpStatusCode.Created)).Id();
        var desk = await admin.GroupAsync("Service desk", busyUser, calmUser);
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{desk}/members/{leadUser.Id}", new { isLead = true });
        _ = calendar;

        // Off by default: nothing is recorded or shown.
        using (var off = await lead.SendAsync(HttpMethod.Get, "/api/v1/wellbeing/groups"))
        {
            Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        }

        Assert.False((bool)(await busy.SendOkAsync(HttpMethod.Get, "/api/v1/me/wellbeing"))["enabled"]!);

        // Turning it on needs the confirmation that staff were told; every agent is notified.
        using (var unconfirmed = await admin.SendAsync(HttpMethod.Put, "/api/v1/wellbeing/settings", new { enabled = true }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        }

        using (var notAdmin = await lead.SendAsync(HttpMethod.Put, "/api/v1/wellbeing/settings", new { enabled = true, staffInformed = true }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        }

        var on = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/wellbeing/settings", new
        {
            enabled = true, staffInformed = true, defaults = new { maxWeightedLoad = 20, maxNewPerDay = 12, maxAfterHoursHours = 5, maxDifficultPercent = 50 },
        });
        Assert.True((bool)on["enabled"]!);
        await using (var db = TestData.AppContext(database, tenant))
        {
            var told = await db.Notifications.AsNoTracking().Where(n => n.EventType == "wellbeing.enabled").Select(n => n.RecipientId).ToListAsync(Ct);
            Assert.Equal(new[] { busyUser.Id, calmUser.Id, leadUser.Id }.Order(), told.Order());
        }

        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "wellbeing.enabled");

        // Five P1 incidents for one agent (5 × 5 = 25 weighted, over 20), one P4 for the other; the busy agent replies after hours.
        var tickets = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            tickets.Add(IncidentApi.IdOf(await lead.CreateAsync(new
            {
                title = $"Outage {i}", requesterId = requesterUser.Id, impact = 1, urgency = 1, groupId = desk, assigneeId = busyUser.Id,
            })));
        }

        IncidentApi.IdOf(await lead.CreateAsync(new { title = "Mouse", requesterId = requesterUser.Id, impact = 3, urgency = 2, groupId = desk, assigneeId = calmUser.Id }));
        await busy.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{tickets[0]}/comments", new { body = "Looking into it now.", visibility = "Public" }, HttpStatusCode.Created);

        Assert.True(await RunAsync(tenant) >= 3);

        // The team lead sees each agent, by name only (alphabetical, never by their figures).
        var detail = await lead.SendOkAsync(HttpMethod.Get, $"/api/v1/wellbeing/groups/{desk}?days=7");
        Assert.True((bool)detail["led"]!);
        var agents = detail["agents"]!.AsArray();
        Assert.Equal(["Amna Calm", "Hessa Lead", "Zayed Busy"], agents.Select(a => (string)a!["name"]!));
        var busyRow = agents[2]!;
        Assert.Equal(5, (int)busyRow["figures"]!["openAssigned"]!);
        Assert.Equal(25, (int)busyRow["figures"]!["weightedLoad"]!);
        Assert.True((int)busyRow["figures"]!["afterHoursHours"]! >= 1);
        Assert.Contains(busyRow["alerts"]!.AsArray(), a => (string)a!["kind"]! == "Workload" && (int)a["value"]! == 25 && (int)a["threshold"]! == 20);
        Assert.Empty(agents[0]!["alerts"]!.AsArray());
        Assert.Equal(1, (int)detail["agentsWithAlerts"]!);
        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, a => a.Action == "wellbeing.viewed" && a.ActorId == leadUser.Id.ToString());
        Assert.Equal(3, audit.Count(a => a.Action == "wellbeing.viewed"));

        // Executives see group totals only, and cannot move work.
        var totals = await executive.SendOkAsync(HttpMethod.Get, $"/api/v1/wellbeing/groups/{desk}");
        Assert.False((bool)totals["led"]!);
        Assert.Null(totals["agents"]);
        Assert.Equal(3, (int)totals["totals"]!["agents"]!);
        Assert.Equal(27, (int)totals["totals"]!["weightedLoad"]!);
        Assert.Equal(3, (await database.AuditAsync(tenant)).Count(a => a.Action == "wellbeing.viewed"));
        using (var refused = await executive.SendAsync(HttpMethod.Get, $"/api/v1/wellbeing/groups/{desk}/agents/{busyUser.Id}/suggestions"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // Agents do not see the dashboard.
        using (var agentRefused = await busy.SendAsync(HttpMethod.Get, "/api/v1/wellbeing/groups"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, agentRefused.StatusCode);
        }

        // Suggested reassignment: the calm agent can take the unstarted tickets; the team lead decides.
        var suggestions = (await lead.SendOkAsync(HttpMethod.Get, $"/api/v1/wellbeing/groups/{desk}/agents/{busyUser.Id}/suggestions"))["items"]!.AsArray();
        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, s =>
        {
            var candidates = s!["candidates"]!.AsArray().Select(c => Guid.Parse((string)c!["id"]!)).ToList();
            Assert.Contains(calmUser.Id, candidates);
            Assert.DoesNotContain(busyUser.Id, candidates);
            // Lightest work first: the team lead has none, the calm agent one P4.
            Assert.Equal([leadUser.Id, calmUser.Id], candidates);
        });
        var moved = Guid.Parse((string)suggestions.Last()!["ticketId"]!);
        Assert.Equal(busyUser.Id, Guid.Parse((string)(await lead.GetIncidentAsync(moved))["assignee"]!["id"]!));
        await lead.SendOkAsync(HttpMethod.Post, "/api/v1/wellbeing/reassign", new { groupId = desk, ticketId = moved, toUserId = calmUser.Id });
        Assert.Equal(calmUser.Id, Guid.Parse((string)(await lead.GetIncidentAsync(moved))["assignee"]!["id"]!));
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "wellbeing.reassigned");
        using (var notMember = await lead.SendAsync(HttpMethod.Post, "/api/v1/wellbeing/reassign", new { groupId = desk, ticketId = tickets[0], toUserId = executiveUser.Id }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notMember.StatusCode);
        }

        // Each agent sees their own figures and the alerts about them, nobody else's.
        var mine = await busy.SendOkAsync(HttpMethod.Get, "/api/v1/me/wellbeing");
        Assert.True((bool)mine["enabled"]!);
        Assert.Equal(busyUser.Id, Guid.Parse((string)mine["figures"]!["id"]!));
        Assert.Contains(mine["figures"]!["alerts"]!.AsArray(), a => (string)a!["kind"]! == "Workload");
        Assert.Equal("Service desk", (string?)mine["groups"]![0]!["name"]!["en"]);

        // Team leads set their group's thresholds; the work no longer passes them.
        await lead.SendOkAsync(HttpMethod.Put, $"/api/v1/wellbeing/groups/{desk}/thresholds", new { maxWeightedLoad = 100 });
        await RunAsync(tenant);
        Assert.Equal(0, (int)(await lead.SendOkAsync(HttpMethod.Get, $"/api/v1/wellbeing/groups/{desk}"))["agentsWithAlerts"]!);
        using (var otherLead = await busy.SendAsync(HttpMethod.Put, $"/api/v1/wellbeing/groups/{desk}/thresholds", new { maxWeightedLoad = 1 }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, otherLead.StatusCode);
        }

        // The figures are part of the person's copy of their data (F5).
        await using (var scope = _factory.ScopeFor(tenant))
        {
            using var zip = new MemoryStream();
            await scope.ServiceProvider.GetRequiredService<PersonalDataExport>().WriteAsync(busyUser.Id, zip, Ct);
            zip.Position = 0;
            using var archive = new ZipArchive(zip);
            await using var entry = archive.GetEntry("personal-data.json")!.Open();
            var data = (await JsonNode.ParseAsync(entry, cancellationToken: Ct))!;
            // One P1 was moved to the calm agent before the last run.
            Assert.Equal(20, (int)data["wellbeing"]!["days"]!.AsArray().Last()!["weightedLoad"]!);
            Assert.NotEmpty(data["wellbeing"]!["alerts"]!.AsArray());
        }

        // Off: what was recorded goes.
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/wellbeing/settings", new { enabled = false });
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.False(await db.WellbeingDays.AnyAsync(Ct));
            Assert.False(await db.WellbeingAlerts.AnyAsync(Ct));
        }

        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "wellbeing.disabled");
    }
}
