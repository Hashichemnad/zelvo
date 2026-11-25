using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Reports;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Voice;
using Zelvo.Notifications;
using Zelvo.Tickets.Exports;
using Zelvo.Tickets.Reports;

namespace Zelvo.Api.Tests;

/// <summary>Dashboards, exports and scheduled reports (module 10, task F2) against PostgreSQL.</summary>
public sealed class ReportApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly CapturingEmailSender _email = new();
    private ApiFactory? _factory;

    private ApiFactory Factory => _factory ??= ApiFactory.ForDatabase(database).WithEmailSender(_email);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>A tenant with two groups, people in each persona, and tickets at known times.</summary>
    private sealed record World(
        Tenant Tenant, TestUser Executive, TestUser Lead, TestUser Agent, TestUser Requester, TestUser Admin, Guid GroupA, Guid GroupB,
        Guid Resolved, Guid OpenB, Guid Complaint, Guid Old);

    private async Task<World> WorldAsync()
    {
        var tenant = await database.CreateTenantAsync("rep");
        var executive = await database.CreateUserAsync(tenant, BuiltInRoles.ExecutiveAuditor);
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var admin = await database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        await using var db = TestData.AppContext(database, tenant);
        var a = new SupportGroup { Name = new("Service desk", "مكتب الخدمة") };
        a.Members.Add(new SupportGroupMember { GroupId = a.Id, UserId = lead.Id, IsLead = true });
        a.Members.Add(new SupportGroupMember { GroupId = a.Id, UserId = agent.Id });
        var b = new SupportGroup { Name = new("Network team", "فريق الشبكات") };
        db.SupportGroups.AddRange(a, b);

        var now = DateTimeOffset.UtcNow;
        var sequence = 0;
        Ticket T(TicketType type, Guid group, DateTimeOffset created, Guid? assignee = null, TicketState state = TicketState.New, DateTimeOffset? resolved = null)
        {
            sequence++;
            var ticket = new Ticket
            {
                Type = type, Number = $"T-{sequence:000000}", Sequence = sequence, Title = $"Ticket {sequence}", RequesterId = requester.Id, GroupId = group,
                AssigneeId = assignee, State = state, CreatedAt = created, ResolvedAt = resolved, RespondedAt = resolved is null ? null : created.AddHours(1),
                Channel = TicketChannel.Portal,
            };
            if (type == TicketType.Incident)
            {
                ticket.Incident = new Incident { TicketId = ticket.Id, Impact = 2, Urgency = 2, Priority = 3 };
            }
            else
            {
                ticket.Voice = new CustomerVoiceRecord { TicketId = ticket.Id, Priority = 2, Severity = 1 };
            }

            db.Tickets.Add(ticket);
            return ticket;
        }

        var t1 = T(TicketType.Incident, a.Id, now.AddDays(-10), agent.Id, TicketState.Resolved, now.AddDays(-9));
        var t2 = T(TicketType.Incident, b.Id, now.AddDays(-5));
        var t3 = T(TicketType.Complaint, a.Id, now.AddDays(-3));
        var t4 = T(TicketType.Incident, a.Id, now.AddDays(-40));
        await db.SaveChangesAsync(Ct);

        // Clocks: t1 met its response target and breached resolution; t2's response clock runs.
        db.TicketSlas.AddRange(
            new TicketSla { TicketId = t1.Id, Metric = SlaMetric.Response, TargetMinutes = 60, State = SlaClockState.Stopped, CountedUntil = now, StoppedAt = now.AddDays(-10) },
            new TicketSla { TicketId = t1.Id, Metric = SlaMetric.Resolution, TargetMinutes = 240, State = SlaClockState.Stopped, CountedUntil = now, StoppedAt = now.AddDays(-9), BreachedAt = now.AddDays(-9.5) },
            new TicketSla { TicketId = t2.Id, Metric = SlaMetric.Response, TargetMinutes = 60, State = SlaClockState.Running, CountedUntil = now });
        // A happy rating of t1 and an unhappy general portal rating.
        db.HappinessRatings.AddRange(
            new HappinessRating { TicketId = t1.Id, RecordType = TicketType.Incident, Channel = TicketChannel.Portal, UserId = requester.Id, Score = 3, Points = 3, CreatedAt = now.AddDays(-8) },
            new HappinessRating { UserId = requester.Id, Score = 1, Points = 3, CreatedAt = now.AddDays(-2) });
        await db.SaveChangesAsync(Ct);
        return new World(tenant, executive, lead, agent, requester, admin, a.Id, b.Id, t1.Id, t2.Id, t3.Id, t4.Id);
    }

    [Fact]
    public async Task Dashboards_count_what_each_person_may_see_and_every_view_is_audited()
    {
        var w = await WorldAsync();
        using var executive = Factory.ForTenant(w.Tenant).As(w.Executive);
        using var lead = Factory.ForTenant(w.Tenant).As(w.Lead);
        using var agent = Factory.ForTenant(w.Tenant).As(w.Agent);
        using var requester = Factory.ForTenant(w.Tenant).As(w.Requester);

        // The executive sees the whole tenant (the 40-day-old ticket is outside the default 30 days but still open).
        var all = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/overview");
        Assert.Equal((3, 1, 3), ((int)all["created"]!, (int)all["resolved"]!, (int)all["open"]!));
        Assert.Equal(60.0, (double)all["medianResponseMinutes"]!);
        Assert.Equal(95.0, (double)all["targets"]!["slaTargetPercent"]!);
        Assert.Equal("All", (string?)(await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/scope"))["mode"]);

        // The team lead sees the group they lead; the agent only their own tickets; requesters nothing.
        var led = await lead.SendOkAsync(HttpMethod.Get, "/api/v1/reports/overview");
        Assert.Equal((2, 1, 2), ((int)led["created"]!, (int)led["resolved"]!, (int)led["open"]!));
        var scope = await lead.SendOkAsync(HttpMethod.Get, "/api/v1/reports/scope");
        Assert.Equal(("Groups", w.GroupA.ToString()), ((string?)scope["mode"], (string?)scope["groups"]![0]!["id"]));
        var own = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/reports/overview");
        Assert.Equal((1, 1, 0), ((int)own["created"]!, (int)own["resolved"]!, (int)own["open"]!));
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Get, "/api/v1/reports/overview")).StatusCode);
        // A lead asking for another group gets nothing from it.
        var other = await lead.SendOkAsync(HttpMethod.Get, $"/api/v1/reports/overview?groups={w.GroupB}");
        Assert.Equal(0, (int)other["created"]!);

        var volumes = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/volumes");
        Assert.Equal(30, volumes["series"]!.AsArray().Count);
        Assert.Equal("Day", (string?)volumes["interval"]);
        Assert.Equal(3, volumes["series"]!.AsArray().Sum(p => (int)p!["created"]!));
        var byType = volumes["byType"]!.AsArray().ToDictionary(k => (string)k!["key"]!, k => (int)k!["count"]!);
        Assert.Equal((2, 1), (byType["Incident"], byType["Complaint"]));
        Assert.Contains(volumes["byGroup"]!.AsArray(), g => (string?)g!["name"]!["en"] == "Service desk" && (int)g["count"]! == 2);
        var complaints = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/volumes?types=Complaint&from=2000-01-01&to=2001-12-31");
        Assert.Equal(0, (int)complaints["created"]!);
        Assert.Equal("Month", (string?)complaints["interval"]);

        var backlog = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/backlog");
        Assert.Equal((3, 3), ((int)backlog["open"]!, (int)backlog["unassigned"]!));
        Assert.Equal(1, backlog["byAge"]!.AsArray().Single(a => (string?)a!["key"] == "gt30d")!["count"]!.GetValue<int>());
        // Ten days ago t1 was open, t2 not yet created: t1 and t4.
        var trend = backlog["trend"]!.AsArray();
        Assert.Equal(3, (int)trend[^1]!["open"]!);

        Assert.Equal(HttpStatusCode.BadRequest, (await executive.SendAsync(HttpMethod.Get, "/api/v1/reports/volumes?from=2026-01-10&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await executive.SendAsync(HttpMethod.Get, "/api/v1/reports/volumes?types=Nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await executive.SendAsync(HttpMethod.Get, "/api/v1/reports/volumes?from=2020-01-01&to=2026-01-01")).StatusCode);

        var audit = await database.AuditAsync(w.Tenant);
        Assert.Contains(audit, e => e.Action == "report.viewed" && (e.Metadata ?? "").Contains("all", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == "report.viewed" && (e.Metadata ?? "").Contains("groups", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sla_agent_and_happiness_dashboards_follow_the_clocks_and_ratings()
    {
        var w = await WorldAsync();
        using var executive = Factory.ForTenant(w.Tenant).As(w.Executive);
        using var lead = Factory.ForTenant(w.Tenant).As(w.Lead);
        using var agent = Factory.ForTenant(w.Tenant).As(w.Agent);

        var sla = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/sla");
        Assert.Equal((1, 0, 1), ((int)sla["response"]!["met"]!, (int)sla["response"]!["breached"]!, (int)sla["response"]!["running"]!));
        Assert.Equal(100.0, (double)sla["response"]!["percent"]!);
        Assert.Equal(0.0, (double)sla["resolution"]!["percent"]!);
        Assert.Contains(sla["byPriority"]!.AsArray(), p => (string?)p!["key"] == "3");

        var agents = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/agents");
        var row = agents["agents"]!.AsArray().Single(a => (string?)a!["id"] == w.Agent.Id.ToString())!;
        Assert.Equal((1, 1, 0), ((int)row["assigned"]!, (int)row["resolved"]!, (int)row["openNow"]!));
        Assert.Equal((60.0, 50.0, 100.0), ((double)row["medianResponseMinutes"]!, (double)row["slaPercent"]!, (double)row["happinessPercent"]!));
        // The lead sees the members of their group (also those with no tickets: the lead themselves).
        var ledAgents = (await lead.SendOkAsync(HttpMethod.Get, "/api/v1/reports/agents"))["agents"]!.AsArray().Select(a => (string)a!["id"]!).ToHashSet();
        Assert.Equal(new[] { w.Agent.Id.ToString(), w.Lead.Id.ToString() }.Order(), ledAgents.Order());
        var mine = (await agent.SendOkAsync(HttpMethod.Get, "/api/v1/reports/agents"))["agents"]!.AsArray();
        Assert.Equal(w.Agent.Id.ToString(), (string?)mine.Single()!["id"]);

        // Executives also see general portal ratings; team leads only their groups' tickets.
        var happiness = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/happiness");
        Assert.Equal((2, 50.0), ((int)happiness["ratings"]!, (double)happiness["happinessPercent"]!));
        Assert.Contains(happiness["byType"]!.AsArray(), t => (string?)t!["key"] == "Portal");
        var ledHappiness = await lead.SendOkAsync(HttpMethod.Get, "/api/v1/reports/happiness");
        Assert.Equal((1, 100.0), ((int)ledHappiness["ratings"]!, (double)ledHappiness["happinessPercent"]!));
        Assert.Equal(75.0, (double)ledHappiness["targetPercent"]!);
    }

    [Fact]
    public async Task Tables_export_as_csv_and_excel_and_pdfs_are_built_in_the_readers_language()
    {
        var w = await WorldAsync();
        using var executive = Factory.ForTenant(w.Tenant).As(w.Executive);
        using var csv = await executive.GetAsync(new Uri("/api/v1/reports/agents/table", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var text = await csv.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("agent,assigned,resolved", text.TrimStart('﻿'), StringComparison.Ordinal);
        using var xlsx = await executive.GetAsync(new Uri("/api/v1/reports/volumes/table?format=xlsx", UriKind.Relative), Ct);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await executive.SendAsync(HttpMethod.Get, "/api/v1/reports/nothing/table")).StatusCode);

        var export = await executive.SendOkAsync(HttpMethod.Post, "/api/v1/reports/sla/exports?language=ar", null, HttpStatusCode.Accepted);
        Assert.Equal(("Report", "ar"), ((string?)export["kind"], (string?)export["language"]));
        Assert.Null(export["entityId"]);

        // The worker's PDF job builds the document with the asker's access, in Arabic, right to left.
        var renderer = new RecordingRenderer();
        var job = new DocumentExportJob(Factory.Services.GetRequiredService<IServiceScopeFactory>(), renderer, new PdfOptions(),
            NullLogger<DocumentExportJob>.Instance, TimeProvider.System);
        Assert.Equal(1, await job.RunOnceAsync(Ct));
        var html = renderer.Html.Single();
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("الالتزام باتفاقيات مستوى الخدمة", html, StringComparison.Ordinal);
        Assert.Contains("بتقنية زيلفو", html, StringComparison.Ordinal);
        Assert.Contains("مكتب الخدمة", html, StringComparison.Ordinal);
        var ready = await executive.SendOkAsync(HttpMethod.Get, $"/api/v1/exports/{export["id"]}");
        Assert.Equal("Ready", (string?)ready["status"]);
        Assert.Contains(await database.AuditAsync(w.Tenant), e => e.Action == "export.requested");
    }

    [Fact]
    public async Task Scheduled_reports_go_to_people_who_may_see_reports_built_with_their_own_access()
    {
        var w = await WorldAsync();
        using var admin = Factory.ForTenant(w.Tenant).As(w.Admin);
        using var lead = Factory.ForTenant(w.Tenant).As(w.Lead);
        using var agent = Factory.ForTenant(w.Tenant).As(w.Agent);

        var body = new { name = "Weekly SLA", report = "Sla", frequency = "Weekly", dayOfWeek = 1, time = "07:30" };
        var own = await lead.SendOkAsync(HttpMethod.Post, "/api/v1/reports/schedules", body, HttpStatusCode.Created);
        Assert.Equal(w.Lead.Id.ToString(), (string?)own["recipients"]![0]!["id"]);
        Assert.NotNull(own["nextRunAt"]);
        using var forOthers = await lead.SendAsync(HttpMethod.Post, "/api/v1/reports/schedules", new { name = "x", report = "Sla", frequency = "Daily", time = "07:00", recipientIds = new[] { w.Executive.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, forOthers.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/reports/schedules", body)).StatusCode);
        using var badDay = await lead.SendAsync(HttpMethod.Post, "/api/v1/reports/schedules", new { name = "x", report = "Sla", frequency = "Monthly", dayOfMonth = 31, time = "07:00" });
        Assert.Equal(HttpStatusCode.BadRequest, badDay.StatusCode);

        // Administrators add other people, who must be able to see reports.
        using var requesterRecipient = await admin.SendAsync(HttpMethod.Post, "/api/v1/reports/schedules",
            new { name = "x", report = "Overview", frequency = "Daily", time = "06:00", recipientIds = new[] { w.Requester.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, requesterRecipient.StatusCode);
        var candidates = (await admin.GetFromJsonAsync<JsonArray>("/api/v1/reports/schedules/recipients", Ct))!.Select(c => (string)c!["id"]!).ToList();
        Assert.Contains(w.Executive.Id.ToString(), candidates);
        Assert.DoesNotContain(w.Requester.Id.ToString(), candidates);
        var daily = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/reports/schedules",
            new { name = "Daily overview", report = "Overview", frequency = "Daily", time = "06:00", recipientIds = new[] { w.Executive.Id, w.Lead.Id } }, HttpStatusCode.Created);
        Assert.Equal(2, daily["recipients"]!.AsArray().Count);
        Assert.Equal(2, (await lead.GetFromJsonAsync<JsonArray>("/api/v1/reports/schedules", Ct))!.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await lead.SendAsync(HttpMethod.Put, $"/api/v1/reports/schedules/{daily["id"]}", body)).StatusCode);

        // The schedule falls due: one email per recipient with the PDF attached.
        await database.ExecuteAsOwnerAsync("UPDATE report_schedules SET next_run_at = now() - interval '1 minute' WHERE id = @id", ("id", Guid.Parse((string)daily["id"]!)));
        var renderer = new RecordingRenderer();
        var job = Job(renderer);
        Assert.Equal(2, await job.RunOnceAsync(Ct));
        var mail = _email.To(w.Executive).Single();
        Assert.Equal(("application/pdf", true), (mail.Attachments.Single().ContentType, mail.Attachments.Single().FileName.StartsWith("overview-", StringComparison.Ordinal)));
        Assert.Contains("Daily overview", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("/reports?from=", mail.Html, StringComparison.Ordinal);
        Assert.Single(_email.To(w.Lead));
        // Each recipient's report is built with their access: the lead's names only their group.
        Assert.Contains(renderer.Html, h => h.Contains("The groups you lead", StringComparison.Ordinal));
        var after = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/reports/schedules/{daily["id"]}");
        Assert.NotNull(after["lastRunAt"]);
        Assert.True(DateTimeOffset.Parse((string)after["nextRunAt"]!, System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow);
        Assert.Equal(0, await job.RunOnceAsync(Ct));
        Assert.Contains(await database.AuditAsync(w.Tenant), e => e.Action == "report.emailed");

        // A recipient who can no longer see reports is skipped.
        await database.ExecuteAsOwnerAsync(
            "DELETE FROM user_roles WHERE user_id = @id", ("id", w.Lead.Id));
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/reports/schedules/{daily["id"]}/send-now", null, HttpStatusCode.Accepted);
        Assert.Equal(2, await job.RunOnceAsync(Ct));
        var log = (await admin.GetFromJsonAsync<JsonArray>($"/api/v1/reports/schedules/{daily["id"]}/deliveries", Ct))!;
        Assert.Contains(log, d => (string?)d!["recipientId"] == w.Lead.Id.ToString() && (string?)d["status"] == "Skipped");
        Assert.Equal(3, log.Count(d => (string?)d!["status"] == "Sent"));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/reports/schedules/{daily["id"]}")).StatusCode);
        await using var db = TestData.AppContext(database, w.Tenant);
        Assert.False(await db.ReportDeliveries.AnyAsync(d => d.ScheduleId == Guid.Parse((string)daily["id"]!), Ct));
    }

    [Fact]
    public async Task Report_targets_are_a_setting_for_reports_managers()
    {
        var w = await WorldAsync();
        using var admin = Factory.ForTenant(w.Tenant).As(w.Admin);
        using var executive = Factory.ForTenant(w.Tenant).As(w.Executive);
        Assert.Equal(HttpStatusCode.Forbidden, (await executive.SendAsync(HttpMethod.Put, "/api/v1/settings/reports", new { slaTargetPercent = 90 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/reports", new { slaTargetPercent = 0 })).StatusCode);
        var changed = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/reports", new { slaTargetPercent = 90, happinessTargetPercent = 80 });
        Assert.Equal(90.0, (double)changed["slaTargetPercent"]!);
        Assert.Equal(90.0, (double)(await executive.SendOkAsync(HttpMethod.Get, "/api/v1/reports/sla"))["targetPercent"]!);
    }

    private ReportScheduleJob Job(IPdfRenderer renderer) => new(
        Factory.Services.GetRequiredService<IServiceScopeFactory>(), Factory.Services.GetRequiredService<NotificationOptions>(), renderer, new PdfOptions(),
        NullLogger<ReportScheduleJob>.Instance, TimeProvider.System);

    private sealed class RecordingRenderer : IPdfRenderer
    {
        public List<string> Html { get; } = [];

        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken)
        {
            lock (Html)
            {
                Html.Add(html);
            }

            return Task.FromResult("%PDF-1.7 test"u8.ToArray());
        }
    }
}

/// <summary>Schedule times and periods (task F2).</summary>
public sealed class ReportScheduleTimeTests
{
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");

    [Fact]
    public void Runs_fall_on_the_chosen_day_and_time_in_the_tenants_time_zone()
    {
        // Wednesday 2026-11-25 10:00 in Dubai (06:00 UTC).
        var now = new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 3, 30, 0, TimeSpan.Zero), ReportScheduleTimes.NextRun(ReportFrequency.Daily, null, null, 450, Dubai, now));
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero), ReportScheduleTimes.NextRun(ReportFrequency.Daily, null, null, 720, Dubai, now));
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 3, 30, 0, TimeSpan.Zero), ReportScheduleTimes.NextRun(ReportFrequency.Weekly, 1, null, 450, Dubai, now));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 3, 30, 0, TimeSpan.Zero), ReportScheduleTimes.NextRun(ReportFrequency.Monthly, null, 1, 450, Dubai, now));
        Assert.Null(ReportScheduleTimes.NextRun(ReportFrequency.Monthly, null, 30, 450, Dubai, now));
        Assert.Null(ReportScheduleTimes.NextRun(ReportFrequency.Weekly, 0, null, 450, Dubai, now));
    }

    [Fact]
    public void Each_run_covers_the_day_week_or_month_before()
    {
        var run = new DateTimeOffset(2026, 12, 1, 3, 30, 0, TimeSpan.Zero);
        Assert.Equal((new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 30)), ReportScheduleTimes.Period(ReportFrequency.Daily, run, Dubai));
        Assert.Equal((new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 30)), ReportScheduleTimes.Period(ReportFrequency.Weekly, run, Dubai));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), ReportScheduleTimes.Period(ReportFrequency.Monthly, run, Dubai));
    }

    [Fact]
    public void Filters_pick_an_interval_that_suits_the_period_and_refuse_too_long_ones()
    {
        Assert.Equal(ReportInterval.Day, new ReportFilter(new(2026, 1, 1), new(2026, 2, 1), [], [], []).EffectiveInterval);
        Assert.Equal(ReportInterval.Week, new ReportFilter(new(2026, 1, 1), new(2026, 9, 1), [], [], []).EffectiveInterval);
        Assert.Equal(ReportInterval.Month, new ReportFilter(new(2025, 1, 1), new(2026, 9, 1), [], [], []).EffectiveInterval);
        Assert.NotNull(new ReportFilter(new(2023, 1, 1), new(2026, 9, 1), [], [], []).Problem());
        Assert.NotNull(new ReportFilter(new(2024, 1, 1), new(2025, 12, 1), [], [], [], ReportInterval.Day).Problem());
        var weeks = new ReportFilter(new(2026, 11, 25), new(2026, 12, 9), [], [], [], ReportInterval.Week).Buckets();
        Assert.Equal(new DateOnly(2026, 11, 23), weeks[0]);
        Assert.Equal(3, weeks.Count);
    }

    [Fact]
    public void Report_texts_exist_in_both_languages()
    {
        foreach (var key in new[] { "kinds.Sla", "emailIntro", "emailWhy", "types.Complaint", "ages.gt30d", "channels.AiAgent", "states.OnHold" })
        {
            Assert.True(ReportTexts.Has(key), key);
        }

        Assert.Equal("بتقنية زيلفو", ReportTexts.Get("ar", "poweredBy"));
    }
}
