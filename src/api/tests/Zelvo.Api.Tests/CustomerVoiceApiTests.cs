using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Api.Security.RateLimiting;
using Zelvo.Data.Identity;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>
/// Complaints, inquiries and suggestions, anonymous submission, the happiness meter and the
/// customer voice report (module 3, task C6; GOV-2, GOV-3) against PostgreSQL.
/// </summary>
public sealed class CustomerVoiceApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static string Of(Guid id, string? path = null) => $"/api/v1/customer-voice/{id}{(path is null ? "" : "/" + path)}";

    private static async Task<JsonObject> SendAsync(HttpClient client, string type, string title = "Something", object? more = null)
    {
        var body = new JsonObject { ["type"] = type, ["title"] = title, ["description"] = "Details" };
        if (more is not null)
        {
            foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(more)!.AsObject())
            {
                body[key] = value?.DeepClone();
            }
        }

        return await client.SendOkAsync(HttpMethod.Post, "/api/v1/customer-voice", body, HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_complaint_needs_a_root_cause_and_closes_only_when_the_customer_confirms_the_resolution()
    {
        var tenant = await database.CreateTenantAsync("cv");
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(person);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var stranger = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var complaint = await SendAsync(requester, "Complaint", "Long wait at the service centre");
        var id = complaint.Id();
        Assert.StartsWith("CMP-", (string?)complaint["number"]);
        Assert.Equal((2, 3), ((int?)complaint["severity"], (int?)complaint["priority"]));
        Assert.Null(complaint["assignee"]);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Get, Of(id))).StatusCode);
        Assert.Empty((await stranger.SendOkAsync(HttpMethod.Get, "/api/v1/customer-voice"))["items"]!.AsArray());

        // Severity sets the SLA priority; resolving needs a root cause and a summary.
        var severe = await agent.SendOkAsync(HttpMethod.Patch, Of(id), new { severity = 1 });
        Assert.Equal(2, (int?)severe["priority"]);
        using var noCause = await agent.SendAsync(HttpMethod.Post, Of(id, "transitions"), new { state = "Resolved", resolutionNotes = "Fixed." });
        Assert.Equal(HttpStatusCode.BadRequest, noCause.StatusCode);
        using var unknownCause = await agent.SendAsync(HttpMethod.Patch, Of(id), new { rootCauseKey = "made-up" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownCause.StatusCode);
        await agent.SendOkAsync(HttpMethod.Patch, Of(id), new { rootCauseKey = "service-delay" });
        var resolved = await agent.SendOkAsync(HttpMethod.Post, Of(id, "transitions"), new { state = "Resolved", resolutionNotes = "We added staff at peak hours." });
        Assert.Equal(("Resolved", "Pending"), ((string?)resolved["state"], (string?)resolved["confirmation"]));
        Assert.DoesNotContain("Closed", resolved["nextStates"]!.AsArray().Select(s => (string?)s));
        using var staffClose = await agent.SendAsync(HttpMethod.Post, Of(id, "transitions"), new { state = "Closed" });
        Assert.Equal("confirmation-pending", (await staffClose.ProblemAsync()).Code);

        // The customer rejects with a reason (reopened, the reason in the conversation), then accepts.
        Assert.True((bool)(await requester.SendOkAsync(HttpMethod.Get, Of(id)))["canConfirm"]!);
        using var noReason = await requester.SendAsync(HttpMethod.Post, Of(id, "confirmation"), new { accept = false });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var rejected = await requester.SendOkAsync(HttpMethod.Post, Of(id, "confirmation"), new { accept = false, comment = "I still waited an hour today." });
        Assert.Equal(("Reopened", "Rejected"), ((string?)rejected["state"], (string?)rejected["confirmation"]));
        var comments = (await requester.SendOkAsync(HttpMethod.Get, Of(id, "comments")))["items"]!.AsArray();
        Assert.Equal("I still waited an hour today.", (string?)comments.Single()!["body"]);
        await agent.SendOkAsync(HttpMethod.Post, Of(id, "transitions"), new { state = "Resolved", resolutionNotes = "Appointments are now kept within 10 minutes." });
        var closed = await requester.SendOkAsync(HttpMethod.Post, Of(id, "confirmation"), new { accept = true });
        Assert.Equal(("Closed", "Confirmed"), ((string?)closed["state"], (string?)closed["confirmation"]));
        Assert.True((bool)closed["canRate"]!);

        // The happiness meter: once per ticket, only by its requester.
        using var notMine = await stranger.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/happiness", new { score = 3 });
        Assert.Equal(HttpStatusCode.NotFound, notMine.StatusCode);
        using var outOfScale = await requester.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/happiness", new { score = 4 });
        Assert.Equal(HttpStatusCode.BadRequest, outOfScale.StatusCode);
        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/happiness", new { score = 3, comment = "Thank you" }, HttpStatusCode.Created);
        using var again = await requester.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/happiness", new { score = 1 });
        Assert.Equal("already-rated", (await again.ProblemAsync()).Code);
        var rating = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/tickets/{id}/happiness");
        Assert.Equal(3, (int?)rating["rating"]!["score"]);

        // Notifications to the requester: received, resolved (asking to confirm).
        await _factory.DeliverNotificationsAsync(tenant);
        var inbox = await requester.InboxAsync();
        Assert.Contains(NotificationEventsUnderTest.VoiceReceived, inbox.Events());
        Assert.Contains(NotificationEventsUnderTest.ComplaintResolved, inbox.Events());
        Assert.StartsWith("/my-voice/", (string?)inbox.Of(NotificationEventsUnderTest.ComplaintResolved)["linkPath"]);
    }

    [Fact]
    public async Task A_resolved_complaint_the_customer_does_not_answer_closes_as_not_confirmed()
    {
        var tenant = await database.CreateTenantAsync("cv");
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var id = (await SendAsync(requester, "Complaint")).Id();
        await agent.SendOkAsync(HttpMethod.Patch, Of(id), new { rootCauseKey = "process" });
        await agent.SendOkAsync(HttpMethod.Post, Of(id, "transitions"), new { state = "Resolved", resolutionNotes = "Procedure explained." });

        await database.ExecuteAsOwnerAsync("UPDATE tickets SET resolved_at = now() - interval '6 days' WHERE id = @id", ("id", id));
        await using var worker = IncidentApi.Worker(database);
        Assert.Equal(1, await worker.GetRequiredService<TicketAutoCloseJob>().CloseTenantAsync(tenant.Id, tenant.Slug, Ct));

        var closed = await agent.SendOkAsync(HttpMethod.Get, Of(id));
        Assert.Equal(("Closed", "NotConfirmed"), ((string?)closed["state"], (string?)closed["confirmation"]));
    }

    [Fact]
    public async Task Inquiries_are_answered_or_converted_and_suggestions_are_resolved_by_their_review()
    {
        var tenant = await database.CreateTenantAsync("cv");
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        // An inquiry answered and reopened by its requester.
        var inquiry = (await SendAsync(requester, "Inquiry", "Opening hours?")).Id();
        await agent.SendOkAsync(HttpMethod.Post, Of(inquiry, "comments"), new { body = "We open at 7:30.", visibility = "Public" }, HttpStatusCode.Created);
        await agent.SendOkAsync(HttpMethod.Post, Of(inquiry, "comments"), new { body = "Asked twice already", visibility = "Internal" }, HttpStatusCode.Created);
        Assert.Single((await requester.SendOkAsync(HttpMethod.Get, Of(inquiry, "comments")))["items"]!.AsArray());
        Assert.Equal(2, (await agent.SendOkAsync(HttpMethod.Get, Of(inquiry, "comments")))["items"]!.AsArray().Count);
        await agent.SendOkAsync(HttpMethod.Post, Of(inquiry, "transitions"), new { state = "Resolved", resolutionNotes = "We open at 7:30, Monday to Friday." });
        var reopened = await requester.SendOkAsync(HttpMethod.Post, Of(inquiry, "transitions"), new { state = "Reopened" });
        Assert.Equal("Reopened", (string?)reopened["state"]);

        // Converted to a complaint: the inquiry resolves with a link, the complaint is new.
        using var noService = await agent.SendAsync(HttpMethod.Post, Of(inquiry, "conversion"), new { to = "ServiceRequest" });
        Assert.Equal(HttpStatusCode.BadRequest, noService.StatusCode);
        var converted = await agent.SendOkAsync(HttpMethod.Post, Of(inquiry, "conversion"), new { to = "Complaint", severity = 1 });
        Assert.Equal("Resolved", (string?)converted["state"]);
        var complaintId = Guid.Parse((string)converted["convertedTo"]!["id"]!);
        var complaint = await requester.SendOkAsync(HttpMethod.Get, Of(complaintId));
        Assert.Equal(("Complaint", 1, inquiry.ToString()), ((string?)complaint["type"], (int?)complaint["severity"], (string?)complaint["convertedFrom"]!["id"]));

        // Suggestions: no plain resolve; Implemented and Declined need feedback and resolve it.
        var suggestion = await SendAsync(requester, "Suggestion", "Evening hours");
        var sid = suggestion.Id();
        Assert.Equal("UnderReview", (string?)suggestion["review"]);
        using var plain = await agent.SendAsync(HttpMethod.Post, Of(sid, "transitions"), new { state = "Resolved", resolutionNotes = "Done" });
        Assert.Equal("review-required", (await plain.ProblemAsync()).Code);
        var accepted = await agent.SendOkAsync(HttpMethod.Post, Of(sid, "review"), new { review = "Accepted", feedback = "Good idea." });
        Assert.Equal(("Accepted", "New"), ((string?)accepted["review"], (string?)accepted["state"]));
        using var noFeedback = await agent.SendAsync(HttpMethod.Post, Of(sid, "review"), new { review = "Implemented" });
        Assert.Equal(HttpStatusCode.BadRequest, noFeedback.StatusCode);
        var implemented = await agent.SendOkAsync(HttpMethod.Post, Of(sid, "review"), new { review = "Implemented", feedback = "Open until 8 pm from today." });
        Assert.Equal(("Implemented", "Resolved", "Open until 8 pm from today."),
            ((string?)implemented["review"], (string?)implemented["state"], (string?)implemented["resolutionNotes"]));
        using var requesterReview = await requester.SendAsync(HttpMethod.Post, Of(sid, "review"), new { review = "Declined", feedback = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, requesterReview.StatusCode);

        await _factory.DeliverNotificationsAsync(tenant);
        var events = (await requester.InboxAsync()).Events().ToList();
        Assert.Contains(NotificationEventsUnderTest.SuggestionReviewed, events);
        Assert.Contains(NotificationEventsUnderTest.InquiryAnswered, events);
        Assert.Contains(NotificationEventsUnderTest.VoiceReplied, events);

        var mine = (await requester.SendOkAsync(HttpMethod.Get, "/api/v1/customer-voice?type=Suggestion"))["items"]!.AsArray();
        Assert.Equal(sid.ToString(), (string?)mine.Single()!["id"]);
    }

    [Fact]
    public async Task Visitors_send_anonymously_only_when_the_tenant_allows_it_and_follow_with_the_tracking_code()
    {
        var tenant = await database.CreateTenantAsync("cv");
        using var visitor = _factory.ForTenant(tenant);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var submission = new { type = "Complaint", title = "Rude reply at the counter", description = "Counter 4, this morning." };

        using var off = await visitor.SendAsync(HttpMethod.Post, "/api/v1/public/customer-voice", submission);
        Assert.Equal("anonymous-disabled", (await off.ProblemAsync()).Code);
        Assert.False((bool)(await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/settings/customer-voice"))["anonymousEnabled"]!);
        using var requesterChange = await agent.SendAsync(HttpMethod.Put, "/api/v1/settings/customer-voice", new { anonymousEnabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, requesterChange.StatusCode);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/customer-voice", new { anonymousEnabled = true });

        var sent = await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice", submission, HttpStatusCode.Created);
        var number = (string)sent["number"]!;
        var code = (string)sent["trackingCode"]!;
        Assert.Equal(12, code.Length);

        using var wrong = await visitor.SendAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track", new { number, code = "ABCDEFGHJKMN" });
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        var lower = $"{code[..4].ToLowerInvariant()}-{code[4..8]}-{code[8..]}";
        var tracked = await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track", new { number, code = lower });
        Assert.Equal(("New", true), ((string?)tracked["record"]!["state"], (bool)tracked["record"]!["anonymous"]!));
        Assert.Null(tracked["record"]!["requester"]);

        // Staff see it without a requester; the anonymous account is no person in Settings → Users.
        var staffView = (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/customer-voice?number={number}"))["items"]!.AsArray().Single()!;
        var id = Guid.Parse((string)staffView["id"]!);
        Assert.Null(staffView["requester"]);
        await agent.SendOkAsync(HttpMethod.Post, Of(id, "comments"), new { body = "Sorry about that. Which time was it?" }, HttpStatusCode.Created);
        await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track/comments", new { number, code, body = "Around 9 am." }, HttpStatusCode.Created);
        var conversation = (await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track", new { number, code }))["comments"]!.AsArray();
        Assert.Equal(["Sorry about that. Which time was it?", "Around 9 am."], conversation.Select(c => (string?)c!["body"]));
        Assert.Null(conversation[1]!["author"]);
        var users = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/users?pageSize=100"))["items"]!.AsArray();
        await using (var db = TestData.AppContext(database, tenant))
        {
            var anonymous = await db.Users.SingleAsync(u => u.Subject == AnonymousAccounts.Subject, Ct);
            Assert.Equal(UserStatus.Disabled, anonymous.Status);
            Assert.DoesNotContain(users, u => (string?)u!["id"] == anonymous.Id.ToString());
            Assert.DoesNotContain(code, await db.CustomerVoiceRecords.Select(v => v.TrackingCodeHash).SingleAsync(Ct), StringComparison.Ordinal);
        }

        // Confirmation and rating with the code.
        await agent.SendOkAsync(HttpMethod.Patch, Of(id), new { rootCauseKey = "staff-conduct" });
        await agent.SendOkAsync(HttpMethod.Post, Of(id, "transitions"), new { state = "Resolved", resolutionNotes = "We spoke with the staff member." });
        var confirmed = await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track/confirmation", new { number, code, accept = true });
        Assert.Equal("Closed", (string?)confirmed["record"]!["state"]);
        Assert.True((bool)confirmed["record"]!["canRate"]!);
        await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track/happiness", new { number, code, score = 2 }, HttpStatusCode.Created);

        // Anonymous inquiries cannot become service requests.
        var inquiry = await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice", new { type = "Inquiry", title = "Parking?" }, HttpStatusCode.Created);
        var inquiryId = Guid.Parse((string)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/customer-voice?number={(string)inquiry["number"]!}"))["items"]![0]!["id"]!);
        using var toRequest = await agent.SendAsync(HttpMethod.Post, Of(inquiryId, "conversion"), new { to = "ServiceRequest", serviceId = Guid.NewGuid() });
        Assert.Equal("anonymous-request", (await toRequest.ProblemAsync()).Code);
    }

    [Fact]
    public async Task Settings_check_severities_root_causes_and_the_happiness_scale_and_portal_ratings_are_once_a_day()
    {
        var tenant = await database.CreateTenantAsync("cv");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var visitor = _factory.ForTenant(tenant);

        var defaults = await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/settings/customer-voice");
        Assert.Equal((3, 3), ((int)defaults["happiness"]!["points"]!, defaults["happiness"]!["labels"]!.AsArray().Count));
        using var twoSeverities = await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/customer-voice",
            new { severities = new[] { new { name = new { en = "High" }, priority = 1 }, new { name = new { en = "Low" }, priority = 4 } } });
        Assert.Equal(HttpStatusCode.BadRequest, twoSeverities.StatusCode);
        var five = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/customer-voice", new { happiness = new { points = 5 } });
        Assert.Equal(5, five["happiness"]!["labels"]!.AsArray().Count);

        // A root cause in use cannot be removed.
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var id = (await SendAsync(requester, "Complaint")).Id();
        await agent.SendOkAsync(HttpMethod.Patch, Of(id), new { rootCauseKey = "system" });
        using var removed = await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/customer-voice",
            new { rootCauses = new[] { new { key = "other", name = new { en = "Other" }, active = true } } });
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);

        // Portal ratings: visitors once per device and day, people once a day.
        var device = Guid.NewGuid();
        await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/happiness/portal", new { score = 5, deviceId = device }, HttpStatusCode.Created);
        using var twice = await visitor.SendAsync(HttpMethod.Post, "/api/v1/happiness/portal", new { score = 4, deviceId = device });
        Assert.Equal("already-rated", (await twice.ProblemAsync()).Code);
        using var noDevice = await visitor.SendAsync(HttpMethod.Post, "/api/v1/happiness/portal", new { score = 4 });
        Assert.Equal(HttpStatusCode.BadRequest, noDevice.StatusCode);
        await requester.SendOkAsync(HttpMethod.Post, "/api/v1/happiness/portal", new { score = 1 }, HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_report_counts_records_per_type_and_happiness_and_needs_voice_report()
    {
        var tenant = await database.CreateTenantAsync("cv");
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var executive = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.ExecutiveAuditor);
        var complaint = (await SendAsync(requester, "Complaint")).Id();
        await SendAsync(requester, "Inquiry");
        await SendAsync(requester, "Suggestion");
        await agent.SendOkAsync(HttpMethod.Patch, Of(complaint), new { rootCauseKey = "system" });
        await agent.SendOkAsync(HttpMethod.Post, Of(complaint, "transitions"), new { state = "Resolved", resolutionNotes = "Fixed" });
        await requester.SendOkAsync(HttpMethod.Post, Of(complaint, "confirmation"), new { accept = true });
        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/tickets/{complaint}/happiness", new { score = 3 }, HttpStatusCode.Created);
        await requester.SendOkAsync(HttpMethod.Post, "/api/v1/happiness/portal", new { score = 2 }, HttpStatusCode.Created);

        using var forbidden = await requester.SendAsync(HttpMethod.Get, "/api/v1/customer-voice/report");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var report = await executive.SendOkAsync(HttpMethod.Get, "/api/v1/customer-voice/report");
        var types = report["types"]!.AsArray().ToDictionary(t => (string)t!["type"]!, t => (int)t!["total"]!);
        Assert.Equal(new Dictionary<string, int> { ["Complaint"] = 1, ["Inquiry"] = 1, ["Suggestion"] = 1 }, types);
        Assert.Equal(2, (int)report["happiness"]!["ratings"]!);
        Assert.Equal(75.0, (double)report["happiness"]!["happinessPercent"]!);
        Assert.Equal("system", (string?)report["rootCauses"]![0]!["key"]);
        Assert.Equal("Confirmed", (string?)report["confirmations"]![0]!["key"]);
        Assert.Equal(13, report["months"]!.AsArray().Count);
        using var csv = await executive.SendAsync(HttpMethod.Get, "/api/v1/customer-voice/report/csv");
        Assert.StartsWith("month,complaints", (await csv.Content.ReadAsStringAsync(Ct)).TrimStart('﻿'), StringComparison.Ordinal);
        using var tooLong = await executive.SendAsync(HttpMethod.Get, "/api/v1/customer-voice/report?from=2020-01-01&to=2026-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Anonymous_submission_and_tracking_have_their_own_rate_limits()
    {
        await using var factory = _factory.WithServices(services =>
        {
            services.RemoveAll<IRateLimitStore>();
            services.AddSingleton<IRateLimitStore, CountingRateLimitStore>();
        });
        var tenant = await database.CreateTenantAsync("cv");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/customer-voice", new { anonymousEnabled = true });
        using var visitor = factory.ForTenant(tenant);

        string? number = null;
        for (var i = 0; i < Voice.VoicePublicEndpoints.SubmissionsPerHour; i++)
        {
            number = (string?)(await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice", new { type = "Suggestion", title = $"Idea {i}" }, HttpStatusCode.Created))["number"];
        }

        using var tooMany = await visitor.SendAsync(HttpMethod.Post, "/api/v1/public/customer-voice", new { type = "Suggestion", title = "One more" });
        Assert.Equal(HttpStatusCode.TooManyRequests, tooMany.StatusCode);

        // Guessing codes for a number stops after the limit, with a wrong code answering 404 until then.
        for (var i = 0; i < Voice.VoicePublicEndpoints.TrackingPerTenMinutes; i++)
        {
            using var wrong = await visitor.SendAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track", new { number, code = "ABCDEFGHJKMN" });
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }

        using var stopped = await visitor.SendAsync(HttpMethod.Post, "/api/v1/public/customer-voice/track", new { number, code = "ABCDEFGHJKMN" });
        Assert.Equal(HttpStatusCode.TooManyRequests, stopped.StatusCode);
        Assert.True(stopped.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Requesters_see_the_team_that_has_their_record_but_never_the_agent()
    {
        var tenant = await database.CreateTenantAsync("cv");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var desk = await admin.GroupAsync("Service desk", agent);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = desk, method = "GroupQueue" });
        var id = (await SendAsync(requester, "Complaint")).Id();
        await admin.SendOkAsync(HttpMethod.Put, Of(id, "assignment"), new { groupId = desk, assigneeId = agent.Id });

        var seen = await requester.SendOkAsync(HttpMethod.Get, Of(id));
        Assert.Equal(desk.ToString(), (string?)seen["group"]!["id"]);
        Assert.Null(seen["assignee"]);
        var ticket = (await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]![0]!;
        Assert.Equal(desk.ToString(), (string?)ticket["team"]!["id"]);
        Assert.DoesNotContain(agent.Id.ToString(), ticket.ToJsonString(), StringComparison.Ordinal);
    }

    private sealed class CountingRateLimitStore : IRateLimitStore
    {
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

        public Task<RateLimitCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<RateLimitCount?>(new RateLimitCount(_counts.AddOrUpdate(key, 1, (_, c) => c + 1), window));
    }
}

/// <summary>Event keys checked by these tests.</summary>
internal static class NotificationEventsUnderTest
{
    public const string VoiceReceived = Zelvo.Notifications.NotificationEvents.VoiceReceived;
    public const string VoiceReplied = Zelvo.Notifications.NotificationEvents.VoiceReplied;
    public const string ComplaintResolved = Zelvo.Notifications.NotificationEvents.ComplaintResolved;
    public const string InquiryAnswered = Zelvo.Notifications.NotificationEvents.InquiryAnswered;
    public const string SuggestionReviewed = Zelvo.Notifications.NotificationEvents.SuggestionReviewed;
}
