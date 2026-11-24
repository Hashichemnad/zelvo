using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Zelvo.Channels;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Email-to-ticket, threading, replies from the mailbox and Settings → Mailboxes (module 6, task E2) against PostgreSQL.</summary>
public sealed class EmailChannelTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly FakeMailboxClients _mail = new();
    private readonly CapturingEmailSender _notifications = new();
    private readonly ApiFactory _factory;

    public EmailChannelTests(DatabaseFixture database)
    {
        _database = database;
        _factory = ApiFactory.ForDatabase(database).WithMasterKey().WithMailboxes(_mail).WithEmailSender(_notifications);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task An_email_becomes_an_incident_and_the_whole_conversation_stays_in_one_thread_from_the_mailbox()
    {
        var tenant = await _database.CreateTenantAsync("e2mail");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var group = await admin.GroupAsync("Service desk", agentUser);
        var mailboxId = await admin.ImapMailboxAsync("support@e2mail.example.org", new { groupId = group });

        // The credential never comes back, and setting it is audited.
        var mailbox = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/mailboxes/{mailboxId}");
        Assert.True((bool)mailbox["credentialSet"]!);
        Assert.DoesNotContain("pw-", mailbox.ToJsonString(), StringComparison.Ordinal);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "channels.mailbox.credential-changed", Ct));
            Assert.DoesNotContain(await db.AuditEvents.Where(e => e.EntityType == "mailbox").Select(e => e.Changes).ToListAsync(Ct),
                c => c != null && c.Contains("pw-", StringComparison.Ordinal));
        }

        // 1. Someone without an account writes in: a requester account and an incident in the mailbox's group.
        var first = ChannelApi.Email("layla@citizens.example.org", "support@e2mail.example.org", "Printer jammed on floor 2", "The printer shows a paper jam.",
            fromName: "Layla Ahmed");
        var firstId = _mail.Deliver("support@e2mail.example.org", first);
        Assert.Equal(1, await _factory.RunMailAsync(tenant));
        Assert.True(_mail.IsRead("support@e2mail.example.org", firstId));
        Ticket ticket;
        await using (var db = TestData.AppContext(_database, tenant))
        {
            ticket = await db.Tickets.Include(t => t.Requester).SingleAsync(t => t.Channel == TicketChannel.Email, Ct);
            Assert.Equal(("Printer jammed on floor 2", "The printer shows a paper jam.", group, mailboxId), (ticket.Title, ticket.Description, ticket.GroupId, ticket.MailboxId));
            Assert.Equal(("layla@citizens.example.org", "Layla Ahmed"), (ticket.Requester!.Email, ticket.Requester.DisplayName));
            Assert.StartsWith(ContactAccounts.SubjectPrefix, ticket.Requester.Subject, StringComparison.Ordinal);
            var log = await db.InboundEmails.SingleAsync(Ct);
            Assert.Equal((InboundEmailOutcome.Created, ticket.Id), (log.Outcome, log.TicketId));
        }

        // 2. "Received" goes out from the mailbox, not the installation's mail server, threaded on the email.
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.DoesNotContain(_notifications.Sent, m => m.To == "layla@citizens.example.org");
        await _factory.RunMailAsync(tenant);
        var received = Assert.Single(_mail.SentTo("layla@citizens.example.org"));
        Assert.Equal("support@e2mail.example.org", received.From.Mailboxes.Single().Address);
        Assert.Contains($"[{ticket.Number}]", received.Subject, StringComparison.Ordinal);
        Assert.Equal(first.MessageId, received.InReplyTo);
        Assert.Equal(ticket.Id, MessageIds.TicketOf(received.MessageId));
        Assert.Equal("auto-generated", received.Headers["Auto-Submitted"]);

        // 3. The requester answers the acknowledgement: added to the conversation without the quoted text.
        var answer = ChannelApi.Email("Layla@Citizens.example.org", "support@e2mail.example.org", $"Re: {received.Subject}",
            "It is the HP printer near the lift.\n\nOn Mon, 23 Nov 2026 at 10:00, Support <support@e2mail.example.org> wrote:\n> Dear Layla,\n> we received your incident.",
            inReplyTo: received.MessageId);
        _mail.Deliver("support@e2mail.example.org", answer);
        await _factory.ReadMailboxesAsync(tenant);
        var messages = (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{ticket.Id}/comments"))["items"]!.AsArray();
        var added = Assert.Single(messages);
        Assert.Equal((false, "Email", "Public", "It is the HP printer near the lift."),
            ((bool)added!["byStaff"]!, (string?)added["source"], (string?)added["visibility"], (string?)added["body"]));

        // 4. The agent replies in Zelvo: the email goes from the mailbox with the thread's headers, and the
        //    first public reply counts as the response.
        var reply = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{ticket.Id}/comments", new { body = "Thanks, a technician is on the way." }, HttpStatusCode.Created);
        Assert.Equal(("Web", "Pending"), ((string?)reply["source"], (string?)reply["emailDelivery"]));
        await _factory.DeliverNotificationsAsync(tenant);
        await _factory.RunMailAsync(tenant);
        var replyEmail = _mail.SentTo("layla@citizens.example.org").Last();
        Assert.Contains("Thanks, a technician is on the way.", replyEmail.TextBody, StringComparison.Ordinal);
        Assert.Equal(answer.MessageId, replyEmail.InReplyTo);
        Assert.Contains(first.MessageId, replyEmail.References);
        Assert.Null(replyEmail.Headers["Auto-Submitted"]); // A person wrote it.
        var thread = (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{ticket.Id}/comments"))["items"]!.AsArray();
        Assert.Equal("Sent", (string?)thread[^1]!["emailDelivery"]);
        Assert.NotNull((await agent.GetIncidentAsync(ticket.Id))["respondedAt"]);

        // 5. A reply that only names the number in the subject also finds the ticket.
        _mail.Deliver("support@e2mail.example.org", ChannelApi.Email("layla@citizens.example.org", "support@e2mail.example.org", $"About [{ticket.Number}]", "It works now."));
        await _factory.ReadMailboxesAsync(tenant);
        Assert.Equal(2, (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{ticket.Id}/comments"))["items"]!.AsArray().Count(m => !(bool)m!["byStaff"]!));

        // 6. Closed is final: a later reply becomes a follow-up incident.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{ticket.Id}/assignee", new { assigneeId = agentUser.Id });
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(ticket.Id, "Resolved", notes: "Cleared the jam.")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(ticket.Id, "Closed")).StatusCode);
        _mail.Deliver("support@e2mail.example.org", ChannelApi.Email("layla@citizens.example.org", "support@e2mail.example.org", $"Re: {received.Subject}",
            "It jammed again.", inReplyTo: replyEmail.MessageId));
        await _factory.ReadMailboxesAsync(tenant);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var followUp = await db.Tickets.SingleAsync(t => t.FollowUpOfId == ticket.Id, Ct);
            Assert.Equal((ticket.RequesterId, TicketChannel.Email, "It jammed again.", mailboxId), (followUp.RequesterId, followUp.Channel, followUp.Description, followUp.MailboxId));
            Assert.Equal(InboundEmailOutcome.FollowUp, (await db.InboundEmails.OrderByDescending(e => e.Id).FirstAsync(Ct)).Outcome);
        }

        // The mailbox log masks addresses.
        var entries = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/mailboxes/{mailboxId}/log"))["items"]!.AsArray();
        Assert.All(entries, e => Assert.Equal("l***@citizens.example.org", (string?)e!["from"]));
        var outgoing = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/mailboxes/{mailboxId}/outgoing"))["items"]!.AsArray();
        Assert.Contains(outgoing, e => (bool)e!["reply"]! && (string?)e["status"] == "Sent");
    }

    [Fact]
    public async Task Automatic_mail_loops_spoofing_and_senders_the_mailbox_does_not_accept_never_become_tickets()
    {
        var tenant = await _database.CreateTenantAsync("e2rules");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var known = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var other = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        const string Box = "help@e2rules.example.org";
        var mailboxId = await admin.ImapMailboxAsync(Box, new { newSenders = "AllowedDomains", allowedDomains = new[] { "entity.example.gov" } });
        await admin.ImapMailboxAsync("other@e2rules.example.org");

        var auto = ChannelApi.Email("someone@entity.example.gov", Box, "Out of office", "I am away.");
        auto.Headers.Add("Auto-Submitted", "auto-replied");
        var bulk = ChannelApi.Email("news@entity.example.gov", Box, "Newsletter", "News.");
        bulk.Headers.Add("Precedence", "bulk");
        var loop = ChannelApi.Email("other@e2rules.example.org", Box, "From our other mailbox", "Loop.");
        var spoofed = ChannelApi.Email("boss@entity.example.gov", Box, "Urgent transfer", "Please pay.");
        spoofed.Headers.Add("Authentication-Results", "mx.example.org; spf=fail smtp.mailfrom=entity.example.gov; dmarc=fail header.from=entity.example.gov");
        var stranger = ChannelApi.Email("stranger@elsewhere.example.com", Box, "Hello", "Can you help?");
        var allowedDomain = ChannelApi.Email("new.staff@entity.example.gov", Box, "VPN access", "I need VPN access.");
        var knownUser = ChannelApi.Email(known.Email, Box, "Laptop slow", "My laptop is slow.");
        foreach (var message in new[] { auto, bulk, loop, spoofed, stranger, allowedDomain, knownUser })
        {
            _mail.Deliver(Box, message);
        }

        await _factory.RunMailAsync(tenant);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var log = await db.InboundEmails.Where(e => e.MailboxId == mailboxId).ToDictionaryAsync(e => e.Subject!, e => (e.Outcome, e.Reason), Ct);
            Assert.Equal((InboundEmailOutcome.Ignored, "auto-reply"), log["Out of office"]);
            Assert.Equal((InboundEmailOutcome.Ignored, "bulk"), log["Newsletter"]);
            Assert.Equal((InboundEmailOutcome.Ignored, "own-mail"), log["From our other mailbox"]);
            Assert.Equal((InboundEmailOutcome.Rejected, "dmarc-failed"), log["Urgent transfer"]);
            Assert.Equal((InboundEmailOutcome.Rejected, "sender-not-allowed"), log["Hello"]);
            Assert.Equal(InboundEmailOutcome.Created, log["VPN access"].Outcome);
            Assert.Equal(InboundEmailOutcome.Created, log["Laptop slow"].Outcome);
            Assert.Equal(2, await db.Tickets.CountAsync(Ct));
            Assert.False(await db.Users.AnyAsync(u => u.Email == "stranger@elsewhere.example.com", Ct));
            Assert.Equal(known.Id, (await db.Tickets.SingleAsync(t => t.Title == "Laptop slow", Ct)).RequesterId);
        }

        // Nothing was answered: rejected senders learn nothing.
        await _factory.DeliverNotificationsAsync(tenant);
        await _factory.RunMailAsync(tenant);
        Assert.Empty(_mail.SentTo("stranger@elsewhere.example.com"));
        Assert.Empty(_mail.SentTo("boss@entity.example.gov"));

        // Someone else naming the ticket's number gets a ticket of their own, never a place in someone else's.
        string number;
        await using (var db = TestData.AppContext(_database, tenant))
        {
            number = (await db.Tickets.SingleAsync(t => t.Title == "Laptop slow", Ct)).Number;
        }

        _mail.Deliver(Box, ChannelApi.Email(other.Email, Box, $"Re: [{number}] Laptop slow", "Mine too."));
        // The same email sent to two mailboxes becomes one ticket.
        var twice = ChannelApi.Email(known.Email, Box, "Monitor flickers", "It flickers.");
        _mail.Deliver(Box, twice);
        _mail.Deliver("other@e2rules.example.org", twice);
        await _factory.ReadMailboxesAsync(tenant);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            Assert.Equal(other.Id, (await db.Tickets.SingleAsync(t => t.Title == "Mine too." || t.Title == "Laptop slow" && t.RequesterId == other.Id, Ct)).RequesterId);
            Assert.Empty(await db.TicketComments.ToListAsync(Ct));
            Assert.Equal(1, await db.Tickets.CountAsync(t => t.Title == "Monitor flickers", Ct));
            Assert.Contains(await db.InboundEmails.Select(e => e.Reason).ToListAsync(Ct), r => r == "duplicate");
        }
    }

    [Fact]
    public async Task Mailbox_settings_keep_credentials_safe_and_are_for_administrators_only()
    {
        var tenant = await _database.CreateTenantAsync("e2box");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);
        var id = await admin.ImapMailboxAsync("desk@e2box.example.org");

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/mailboxes")).StatusCode);

        // A new server needs the credential again, so nobody can send the stored one elsewhere.
        var moved = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/mailboxes/{id}", new { imapHost = "imap.attacker.example.net" });
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Contains("credential", (await moved.ProblemAsync()).Errors!.Keys);
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/mailboxes/{id}", new { imapHost = "imap2.mail.example.org", credential = ChannelApi.Credential() });

        // Servers on this host or internal networks are refused unless the operator allows them.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/mailboxes/{id}", new { smtpHost = "127.0.0.1", credential = ChannelApi.Credential() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/mailboxes/{id}", new { smtpHost = "localhost", credential = ChannelApi.Credential() })).StatusCode);

        // Provider and sign-in must fit; addresses are unique per tenant.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/mailboxes",
            new { name = "x", address = "x@e2box.example.org", provider = "Gmail", auth = "Password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/mailboxes",
            new { name = "Dup", address = "DESK@e2box.example.org", provider = "Imap", imapHost = "imap.mail.example.org", imapPort = 993, smtpHost = "smtp.mail.example.org", smtpPort = 587, username = "u" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/mailboxes",
            new { name = "Gmail", address = "g@e2box.example.org", provider = "Gmail", auth = "ServiceAccount", credential = "{\"not\":\"a key\"}" })).StatusCode);

        // A failing mailbox shows why, and is read less often.
        _mail.Failure = new MailboxException("sign-in-failed", "The IMAP server refused the user name or password.");
        await _factory.RunMailAsync(tenant);
        var failing = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/mailboxes/{id}");
        Assert.False((bool)failing["lastCheckSucceeded"]!);
        Assert.StartsWith("sign-in-failed", (string?)failing["lastError"], StringComparison.Ordinal);
        Assert.True(DateTimeOffset.Parse((string)failing["nextCheckAt"]!, System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddSeconds(45));
        var test = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/mailboxes/{id}/test");
        Assert.Equal((false, "sign-in-failed"), ((bool)test["succeeded"]!, (string?)test["code"]));
        _mail.Failure = null;
        Assert.True((bool)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/mailboxes/{id}/test"))["succeeded"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/mailboxes/{id}")).StatusCode);
    }

    [Fact]
    public async Task People_who_wrote_by_email_take_their_account_over_at_their_first_verified_sign_in()
    {
        var tenant = await _database.CreateTenantAsync("e2claim");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        await admin.ImapMailboxAsync("support@e2claim.example.org");
        _mail.Deliver("support@e2claim.example.org", ChannelApi.Email("omar@people.example.org", "support@e2claim.example.org", "Badge not working", "My badge stopped."));
        await _factory.RunMailAsync(tenant);

        var subject = Guid.NewGuid().ToString();
        using var unverified = _factory.ForTenant(tenant);
        unverified.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(tenant, subject, mfa: false, email: "omar@people.example.org"));
        // Without a verified email, the sign-in gets an account of its own and none of the tickets.
        var other = Guid.NewGuid().ToString();
        using var notVerified = _factory.ForTenant(tenant);
        notVerified.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(tenant, other, mfa: false, email: "omar@people.example.org"));
        Assert.Empty((await notVerified.SendOkAsync(HttpMethod.Get, "/api/v1/incidents"))["items"]!.AsArray());

        using var verified = _factory.ForTenant(tenant);
        verified.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(tenant, subject, mfa: false, email: "omar@people.example.org",
            extraClaims: new Dictionary<string, object> { ["email_verified"] = true }));
        var mine = (await verified.SendOkAsync(HttpMethod.Get, "/api/v1/incidents"))["items"]!.AsArray();
        Assert.Equal("Badge not working", (string?)Assert.Single(mine)!["title"]);
    }

    [Fact]
    public async Task Replies_of_portal_incidents_reach_the_requester_as_notifications_and_a_requester_answer_resumes_a_waiting_incident()
    {
        var tenant = await _database.CreateTenantAsync("e2portal");
        var requesterUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var stranger = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Requester);
        var id = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Phone line dead", requesterId = requesterUser.Id, assigneeId = agentUser.Id }));
        await agent.MoveAsync(id, "InProgress");
        await agent.MoveAsync(id, "OnHold", reason: "awaiting-requester");

        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "Which extension is it?" }, HttpStatusCode.Created);
        var answer = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "Extension 4412." }, HttpStatusCode.Created);
        Assert.Null(answer["emailDelivery"]); // Requesters never see delivery details.

        // The requester's answer moves the incident waiting for them back to work.
        Assert.Equal("InProgress", (string?)(await agent.GetIncidentAsync(id))["state"]);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Get, $"/api/v1/incidents/{id}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "x" })).StatusCode);

        await _factory.DeliverNotificationsAsync(tenant);
        // B4's approved event: the requester hears of the staff reply; a requester's answer notifies nobody (the workspace shows it live).
        Assert.Contains((await requester.InboxAsync()).Events(), e => e == Zelvo.Notifications.NotificationEvents.IncidentReplied);
        Assert.DoesNotContain((await agent.InboxAsync()).Events(), e => e == Zelvo.Notifications.NotificationEvents.IncidentReplied);
        Assert.Contains(_notifications.To(requesterUser), m => m.Text.Contains("Which extension is it?", StringComparison.Ordinal));
        Assert.Empty(_mail.Sent);

        await agent.MoveAsync(id, "Resolved", notes: "Line repaired.");
        await agent.MoveAsync(id, "Closed");
        Assert.Equal("ticket-closed", (await (await requester.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "Thanks" })).ProblemAsync()).Code);
    }
}

/// <summary>Reading emails (task E2), without a database.</summary>
public sealed class EmailParsingTests
{
    [Theory]
    [InlineData("Thanks, fixed.\n\nOn Mon, 23 Nov 2026 at 10:00, Support <s@x.example> wrote:\n> old", "Thanks, fixed.")]
    [InlineData("Thanks, fixed.\n\n-----Original Message-----\nFrom: Support\nSent: Monday", "Thanks, fixed.")]
    [InlineData("Thanks, fixed.\n\nFrom: Support <s@x.example>\nSent: Monday, 23 November 2026 10:00\nTo: me", "Thanks, fixed.")]
    [InlineData("شكرًا، تم الحل.\n\nفي الاثنين، ٢٣ نوفمبر ٢٠٢٦ كتب الدعم الفني <s@x.example>:\n> قديم", "شكرًا، تم الحل.")]
    [InlineData("شكرًا.\n\nمن: الدعم الفني\nتاريخ الإرسال: الاثنين", "شكرًا.")]
    [InlineData("Line one\n> a quote in the middle\nLine three", "Line one\n> a quote in the middle\nLine three")]
    [InlineData("> only a quote", "> only a quote")]
    public void Only_the_new_text_of_a_reply_is_kept(string text, string expected) => Assert.Equal(expected, EmailParsing.StripQuoted(text));

    [Fact]
    public void Html_emails_become_text_without_scripts_styles_or_markup()
    {
        var text = EmailParsing.HtmlToText("<html><head><style>p{color:red}</style><title>T</title></head><body><p>Hello&nbsp;<b>team</b></p><script>alert(1)</script><div>Line&amp;two</div></body></html>");
        Assert.Equal("Hello team\n\nLine&two", text.Trim());
    }

    [Fact]
    public void Threading_ids_titles_and_language_come_from_the_headers_and_text()
    {
        var ticket = Guid.CreateVersion7();
        var ours = MessageIds.Create(ticket, "Support@Entity.example.gov");
        Assert.EndsWith("@entity.example.gov", ours, StringComparison.Ordinal);
        Assert.Equal(ticket, MessageIds.TicketOf(ours));
        Assert.Null(MessageIds.TicketOf("zelvo.tnot-a-guid.1@x"));

        var message = ChannelApi.Email("A@B.example", "s@x.example", "RE: Fwd: [INC-000042] الطابعة لا تعمل", "الطابعة في الطابق الأول لا تطبع", inReplyTo: ours);
        message.References.Add("older@x.example");
        var parsed = EmailParsing.Parse(message);
        Assert.Equal("a@b.example", parsed.FromAddress);
        Assert.Equal([ours, "older@x.example"], parsed.ThreadIds);
        Assert.Equal("ar", parsed.Language);
        Assert.Equal("الطابعة لا تعمل", EmailIntake.TitleOf(parsed, null));
        Assert.Null(parsed.AutoReason);
        Assert.False(parsed.DmarcFailed);
    }

    [Fact]
    public void Inline_signature_images_are_left_out_and_attached_emails_kept()
    {
        var builder = new BodyBuilder { TextBody = "See attached." };
        builder.Attachments.Add("report.pdf", "%PDF-1.7 test"u8.ToArray(), new ContentType("application", "pdf"));
        var logo = builder.LinkedResources.Add("logo.png", [0x89, 0x50, 0x4E, 0x47], new ContentType("image", "png"));
        logo.ContentId = "logo@x";
        var message = ChannelApi.Email("a@b.example", "s@x.example", "Report", "x");
        message.Body = builder.ToMessageBody();
        var parsed = EmailParsing.Parse(message);
        Assert.Equal("report.pdf", Assert.Single(parsed.Files).FileName);
    }
}
