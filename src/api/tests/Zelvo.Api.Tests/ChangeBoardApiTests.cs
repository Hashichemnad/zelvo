using System.Net;
using System.Text.Json.Nodes;
using Zelvo.Data.Changes;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Notifications;

namespace Zelvo.Api.Tests;

/// <summary>The change advisory board and the emergency board (task H4, module 17), against PostgreSQL.</summary>
public sealed class ChangeBoardApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static readonly DateTimeOffset Start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(6).AddHours(20);

    private static object Medium => new Dictionary<string, string>
    {
        ["reach"] = "department", ["experience"] = "sometimes", ["downtime"] = "window", ["backout"] = "easy", ["testing"] = "partial",
    };

    private static async Task<Guid> SubmittedAsync(HttpClient agent, string title, string kind = "Normal")
    {
        var change = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/changes", new
        {
            kind, title, plannedStart = Start, plannedEnd = Start.AddHours(2), implementationPlan = "Do it.", backoutPlan = "Undo it.",
        }, HttpStatusCode.Created);
        if (kind == "Normal")
        {
            await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{change.Id()}/risk", new { answers = Medium });
        }

        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{change.Id()}/submit");
        return change.Id();
    }

    [Fact]
    public async Task The_board_meets_members_vote_and_the_chair_decides_through_the_approval()
    {
        var tenant = await database.CreateTenantAsync("cab");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var chairUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var chair = _factory.ForTenant(tenant).As(chairUser);
        var memberUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var member = _factory.ForTenant(tenant).As(memberUser);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);

        // Board members hold changes.approve; agents do not.
        var boards = await admin.ArrayAsync("/api/v1/change-boards");
        Assert.Equal(["Advisory", "Emergency"], boards.Select(b => (string)b!["kind"]!).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/change-boards/advisory",
            new { name = new { en = "CAB" }, chairId = chairUser.Id, memberIds = new[] { agentUser.Id } })).StatusCode);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/change-boards/advisory",
            new { name = new { en = "CAB", ar = "المجلس" }, chairId = chairUser.Id, memberIds = new[] { chairUser.Id, memberUser.Id } });

        // Medium risk waits for the board; its chair is the approver.
        var first = await SubmittedAsync(agent, "Patch the database");
        var second = await SubmittedAsync(agent, "Move the file server");
        var waiting = await agent.ArrayAsync("/api/v1/cab-meetings/waiting");
        Assert.Equal(2, waiting.Count);
        Assert.Equal("Advisory", (string)waiting[0]!["approval"]!["board"]!);

        // The change manager plans the meeting: the agenda is built and members are invited with a calendar file.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/cab-meetings", new { title = new { en = "x" }, startsAt = Start, endsAt = Start.AddHours(1) })).StatusCode);
        var meeting = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cab-meetings", new
        {
            title = new { en = "Weekly board", ar = "الاجتماع الأسبوعي" }, startsAt = Start.AddDays(-1), endsAt = Start.AddDays(-1).AddHours(1), location = "Room 3",
        }, HttpStatusCode.Created);
        var meetingId = meeting.Id();
        Assert.Equal(2, meeting["agenda"]!.AsArray().Count);
        Assert.Equal((string)meeting["chair"]!["id"]!, chairUser.Id.ToString());
        Assert.Empty(await agent.ArrayAsync("/api/v1/cab-meetings/waiting"));
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Contains("cab.invited", (await member.InboxAsync()).Events());

        var items = meeting["agenda"]!.AsArray();
        var firstItem = items.Single(i => (string)i!["changeId"]! == first.ToString())!["id"]!.ToString();
        var secondItem = items.Single(i => (string)i!["changeId"]! == second.ToString())!["id"]!.ToString();

        // Members vote (advice); only members vote and only the chair decides.
        await member.SendOkAsync(HttpMethod.Put, $"/api/v1/cab-meetings/{meetingId}/agenda/{firstItem}/vote", new { choice = "Reject", comment = "Risky at month end." });
        await chair.SendOkAsync(HttpMethod.Put, $"/api/v1/cab-meetings/{meetingId}/agenda/{firstItem}/vote", new { choice = "Approve" });
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Put, $"/api/v1/cab-meetings/{meetingId}/agenda/{firstItem}/vote", new { choice = "Approve" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.SendAsync(HttpMethod.Post, $"/api/v1/cab-meetings/{meetingId}/agenda/{firstItem}/decision", new { decision = "Approved" })).StatusCode);

        // The chair approves the first change through C3: it is scheduled. Votes and minutes stay with the change.
        var decided = await chair.SendOkAsync(HttpMethod.Post, $"/api/v1/cab-meetings/{meetingId}/agenda/{firstItem}/decision",
            new { decision = "Approved", minutes = "Approved despite one vote against; run after month end." });
        var item = decided["agenda"]!.AsArray().Single(i => (string)i!["id"]! == firstItem)!;
        Assert.Equal("Approved", (string)item["decision"]!);
        Assert.Equal(1, (int)item["tally"]!["Approve"]!);
        Assert.Equal(1, (int)item["tally"]!["Reject"]!);
        Assert.Equal("Scheduled", (string)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{first}"))["stage"]!);
        var history = await agent.ArrayAsync($"/api/v1/changes/{first}/board");
        Assert.Equal("Risky at month end.", (string)history.Single()!["item"]!["votes"]!.AsArray().Single(v => (string)v!["choice"]! == "Reject")!["comment"]!);
        Assert.Contains("month end", (string)history.Single()!["item"]!["minutes"]!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await member.SendAsync(HttpMethod.Put, $"/api/v1/cab-meetings/{meetingId}/agenda/{firstItem}/vote", new { choice = "Abstain" })).StatusCode);

        // Rejecting needs a reason; the second change is deferred when the meeting is held, and waits again.
        Assert.Equal(HttpStatusCode.BadRequest, (await chair.SendAsync(HttpMethod.Post, $"/api/v1/cab-meetings/{meetingId}/agenda/{secondItem}/decision", new { decision = "Rejected" })).StatusCode);
        await chair.SendOkAsync(HttpMethod.Put, $"/api/v1/cab-meetings/{meetingId}/minutes", new { minutes = "Two changes reviewed." });
        var held = await chair.SendOkAsync(HttpMethod.Post, $"/api/v1/cab-meetings/{meetingId}/complete");
        Assert.Equal("Held", (string)held["state"]!);
        Assert.Equal("Deferred", (string)held["agenda"]!.AsArray().Single(i => (string)i!["id"]! == secondItem)!["decision"]!);
        Assert.Equal(second.ToString(), (string)(await agent.ArrayAsync("/api/v1/cab-meetings/waiting")).Single()!["id"]!);

        // A second meeting takes it up; a rejection closes the change with the reason.
        var next = await chair.SendOkAsync(HttpMethod.Post, "/api/v1/cab-meetings", new { title = new { en = "Extra board" }, startsAt = Start, endsAt = Start.AddHours(1) }, HttpStatusCode.Created);
        var nextItem = next["agenda"]!.AsArray().Single()!["id"]!.ToString();
        await chair.SendOkAsync(HttpMethod.Post, $"/api/v1/cab-meetings/{next.Id()}/agenda/{nextItem}/decision", new { decision = "Rejected", comment = "Plan a test first." });
        var rejected = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{second}");
        Assert.Equal("Rejected", (string)rejected["closure"]!);
        Assert.Equal(2, (await agent.ArrayAsync($"/api/v1/changes/{second}/board")).Count);

        // Calling a meeting off sends the cancellation.
        var third = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cab-meetings", new { title = new { en = "Spare" }, startsAt = Start, endsAt = Start.AddHours(1) }, HttpStatusCode.Created);
        Assert.Equal("Cancelled", (string)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/cab-meetings/{third.Id()}/cancel"))["state"]!);
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Contains("cab.cancelled", (await member.InboxAsync()).Events());
    }

    [Fact]
    public async Task The_emergency_board_decides_without_a_meeting_within_its_hours()
    {
        var tenant = await database.CreateTenantAsync("cab");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var memberUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var member = _factory.ForTenant(tenant).As(memberUser);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/change-boards/emergency",
            new { name = new { en = "ECAB", ar = "المجلس الطارئ" }, memberIds = new[] { memberUser.Id }, decideWithinHours = 2 });
        var id = await SubmittedAsync(agent, "Patch the VPN now", "Emergency");
        var change = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{id}");
        Assert.Equal("Emergency", (string)change["approval"]!["board"]!);
        var decideBy = DateTimeOffset.Parse((string)change["approval"]!["decideBy"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(decideBy - DateTimeOffset.Parse((string)change["approval"]!["createdAt"]!, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(1.9), TimeSpan.FromHours(2.1));
        // Not on the advisory board's agenda; the member decides in My approvals.
        Assert.Empty(await agent.ArrayAsync("/api/v1/cab-meetings/waiting"));
        var pending = (await member.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending"))["items"]!.AsArray().Single()!["id"]!.ToString();
        await member.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{pending}/decision", new { decision = "Approve" });
        Assert.Equal("Scheduled", (string)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{id}"))["stage"]!);
    }

    [Fact]
    public void Calendar_files_escape_text_fold_long_lines_and_cancel_with_the_same_uid()
    {
        var meeting = new CabMeeting
        {
            Title = new LocalizedText("Board; weekly, with \"notes\"", "الاجتماع الأسبوعي للمجلس الاستشاري للتغيير"),
            StartsAt = new DateTimeOffset(2026, 12, 3, 9, 0, 0, TimeSpan.FromHours(4)),
            EndsAt = new DateTimeOffset(2026, 12, 3, 10, 0, 0, TimeSpan.FromHours(4)),
            Location = "Room 3",
            ChairId = Guid.NewGuid(),
            Sequence = 2,
        };
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var ics = MeetingInvites.Build(meeting, ["CHG-000001 Patch, then restart"], "member@example.org", "en", "REQUEST", "zelvo@example.org", now);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics, StringComparison.Ordinal);
        Assert.Contains($"UID:{meeting.Id:D}@zelvo\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("SEQUENCE:2\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20261203T050000Z\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Board\\; weekly\\, with \"notes\"", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:Agenda:\\nCHG-000001 Patch\\, then restart", ics, StringComparison.Ordinal);
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));

        var arabic = MeetingInvites.Build(meeting, [], "member@example.org", "ar", "CANCEL", "zelvo@example.org", now);
        Assert.Contains("METHOD:CANCEL", arabic, StringComparison.Ordinal);
        Assert.Contains("STATUS:CANCELLED", arabic, StringComparison.Ordinal);
        Assert.All(arabic.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        // Unfolded, the Arabic title is whole.
        Assert.Contains("SUMMARY:الاجتماع الأسبوعي للمجلس الاستشاري للتغيير", arabic.Replace("\r\n ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
