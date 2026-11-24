using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Channels;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task E2: a support mailbox (IMAP, turned off: demo installations read no real mail server), the
/// sample incident that came in by email with its conversation (comments, task B4) and mailbox log, a chat widget on
/// the portal for the main group, and an ended chat. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoChannelsSeeder : IDemoSeeder
{
    public string Key => "e2-channels";

    public int Order => 140;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        if (await db.Mailboxes.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var organisation = context.Organisation;
        var arabic = organisation.DefaultLanguage == LocalizedText.Arabic;
        var users = (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Email, u.Id }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        Guid UserId(string persona) => users[organisation.UsernameOf(organisation.User(persona)!)];
        var requester = UserId("requester");
        var agent = UserId("agent");
        var group = await db.SupportGroups.AsNoTracking().Where(g => g.Members.Any(m => m.UserId == agent)).OrderBy(g => g.CreatedAt)
            .Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var mailbox = new Mailbox
        {
            Name = arabic ? "الدعم الفني" : "IT support",
            Address = $"support@{organisation.EmailDomain}",
            Provider = MailboxProvider.Imap,
            Auth = MailboxAuth.Password,
            ImapHost = $"mail.{organisation.EmailDomain}",
            ImapPort = 993,
            SmtpHost = $"mail.{organisation.EmailDomain}",
            SmtpPort = 587,
            Username = $"support@{organisation.EmailDomain}",
            NewSenders = NewSenderPolicy.AllowedDomains,
            AllowedDomains = [organisation.EmailDomain],
            GroupId = group,
            // Demo installations have no mail server to read.
            Status = MailboxStatus.Disabled,
        };
        db.Mailboxes.Add(mailbox);

        var emailTicket = await db.Tickets.Where(t => t.Channel == TicketChannel.Email && t.RequesterId == requester).OrderBy(t => t.Sequence)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (emailTicket is not null)
        {
            emailTicket.MailboxId = mailbox.Id;
            var start = emailTicket.CreatedAt;
            var firstId = $"demo.{emailTicket.Id:N}.1@{organisation.EmailDomain}";
            db.InboundEmails.Add(new InboundEmail
            {
                MailboxId = mailbox.Id, ProviderMessageId = "1:1", MessageId = firstId, FromAddress = organisation.UsernameOf(organisation.User("requester")!),
                Subject = emailTicket.Title, Outcome = InboundEmailOutcome.Created, TicketId = emailTicket.Id, ReceivedAt = start, ProcessedAt = start,
            });
            // The agent's reply went out from the mailbox in the email's thread; the requester answered by email.
            var staffReplyId = MessageIds.Create(emailTicket.Id, mailbox.Address);
            var staffReply = new TicketComment
            {
                TicketId = emailTicket.Id, Visibility = CommentVisibility.Public, Language = organisation.DefaultLanguage, AuthorId = agent, ByStaff = true,
                MailboxId = mailbox.Id, EmailMessageId = staffReplyId, EmailInReplyTo = firstId, CreatedAt = start.AddHours(1),
                Body = arabic
                    ? "شكرًا لتواصلكم. هل يحدث البطء عند فتح جميع المعاملات أم معاملات محددة؟"
                    : "Thank you for reporting this. Is it slow for every transaction or only some?",
            };
            db.TicketComments.Add(staffReply);
            db.OutboundEmails.Add(new OutboundEmail
            {
                MailboxId = mailbox.Id, TicketId = emailTicket.Id, CommentId = staffReply.Id, ToAddress = organisation.UsernameOf(organisation.User("requester")!),
                Subject = $"{emailTicket.Number} [{emailTicket.Number}]", MessageId = staffReplyId, InReplyTo = firstId, Language = organisation.DefaultLanguage,
                HtmlBody = "<p></p>", TextBody = staffReply.Body, Status = OutboundEmailStatus.Sent, Attempts = 1, NextAttemptAt = null,
                CreatedAt = staffReply.CreatedAt, SentAt = staffReply.CreatedAt,
            });
            var replyAt = start.AddHours(2);
            var replyId = $"demo.{emailTicket.Id:N}.2@{organisation.EmailDomain}";
            var reply = new TicketComment
            {
                TicketId = emailTicket.Id, Visibility = CommentVisibility.Public, Language = organisation.DefaultLanguage, AuthorId = requester,
                Source = CommentSource.Email, MailboxId = mailbox.Id, EmailMessageId = replyId, EmailInReplyTo = staffReplyId, CreatedAt = replyAt,
                Body = arabic ? "يحدث ذلك مع جميع المعاملات، خاصةً في الصباح." : "Every transaction, especially in the morning.",
            };
            db.TicketComments.Add(reply);
            db.InboundEmails.Add(new InboundEmail
            {
                MailboxId = mailbox.Id, ProviderMessageId = "1:2", MessageId = replyId, FromAddress = organisation.UsernameOf(organisation.User("requester")!),
                Subject = $"Re: [{emailTicket.Number}] {emailTicket.Title}", Outcome = InboundEmailOutcome.Added, TicketId = emailTicket.Id, CommentId = reply.Id,
                ReceivedAt = replyAt, ProcessedAt = replyAt,
            });
            db.InboundEmails.Add(new InboundEmail
            {
                MailboxId = mailbox.Id, ProviderMessageId = "1:3", FromAddress = $"newsletter@offers.{organisation.EmailDomain}",
                Subject = arabic ? "عروض الأسبوع" : "This week's offers", Outcome = InboundEmailOutcome.Ignored, Reason = "bulk", ReceivedAt = replyAt, ProcessedAt = replyAt,
            });
        }

        if (group is not { } groupId)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var widget = new ChatWidget
        {
            Name = new LocalizedText("Chat with us", "تحدّث معنا"),
            Greeting = new LocalizedText("Hello, how can we help you today?", "مرحبًا بكم، كيف يمكننا مساعدتكم اليوم؟"),
            GroupId = groupId,
            OnPortal = true,
        };
        db.ChatWidgets.Add(widget);

        var chatTicket = await db.Tickets.Where(t => t.Channel == TicketChannel.Chat && t.RequesterId == requester).OrderBy(t => t.Sequence)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var chatStart = chatTicket?.CreatedAt ?? now.AddDays(-1);
        var requesterUser = organisation.User("requester")!;
        var session = new ChatSession
        {
            WidgetId = widget.Id, GroupId = groupId, VisitorUserId = requester, VisitorName = requesterUser.DisplayName,
            VisitorEmail = organisation.UsernameOf(requesterUser), Language = organisation.DefaultLanguage, State = ChatState.Ended, AgentId = agent,
            TicketId = chatTicket?.Id, StartedAt = chatStart, AcceptedAt = chatStart.AddMinutes(1), EndedAt = chatStart.AddMinutes(9),
            EndedBy = ChatEndedBy.Agent, LastVisitorActivityAt = chatStart.AddMinutes(8),
        };
        db.ChatSessions.Add(session);
        var lines = arabic
            ? new (ChatSender, string)[]
            {
                (ChatSender.Visitor, "مرحبًا، لا أستطيع فتح صفحة تتبع الطلبات."),
                (ChatSender.Agent, "مرحبًا بكم. هل تظهر رسالة خطأ؟"),
                (ChatSender.Visitor, "تظهر صفحة فارغة فقط."),
                (ChatSender.Agent, "يُرجى مسح ذاكرة المتصفح والمحاولة مرة أخرى."),
                (ChatSender.Visitor, "نجح ذلك، شكرًا لكم."),
            }
            : new (ChatSender, string)[]
            {
                (ChatSender.Visitor, "Hi, my VPN keeps disconnecting."),
                (ChatSender.Agent, "Hello. Which version of the VPN client do you have?"),
                (ChatSender.Visitor, "I'm not sure, it's the one from last year."),
                (ChatSender.Agent, "I'll log this so we can update it for you."),
            };
        var at = chatStart.AddMinutes(1);
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, AuthorId = agent, Body = string.Empty, Event = "accepted", CreatedAt = at });
        foreach (var (sender, text) in lines)
        {
            at = at.AddMinutes(1);
            db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = sender, AuthorId = sender == ChatSender.Agent ? agent : null, Body = text, CreatedAt = at });
            if (chatTicket is not null)
            {
                db.TicketComments.Add(new TicketComment
                {
                    TicketId = chatTicket.Id, Visibility = CommentVisibility.Public, Language = organisation.DefaultLanguage,
                    AuthorId = sender == ChatSender.Visitor ? requester : agent, ByStaff = sender == ChatSender.Agent, Source = CommentSource.Chat,
                    Body = text, ChatSessionId = session.Id, CreatedAt = at,
                });
            }
        }

        db.ChatMessages.Add(new ChatMessage
        {
            SessionId = session.Id, Sender = ChatSender.System, AuthorId = agent, Body = chatTicket?.Number ?? string.Empty,
            Event = chatTicket is null ? "ended" : "ticket", CreatedAt = at.AddMinutes(1),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
