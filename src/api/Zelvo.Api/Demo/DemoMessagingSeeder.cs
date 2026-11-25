using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zelvo.Channels.Messaging;
using Zelvo.Data;
using Zelvo.Data.Channels;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task E3: a WhatsApp number, an SMS gateway and a Teams app, all turned off (demo installations
/// reach no real provider), and a sample incident that came in by WhatsApp: the requester's
/// message, a voice note with its transcript, the agent's reply that went back on WhatsApp, and
/// the channel log. Numbers are fictional, like everything in the demo.
/// </summary>
internal sealed class DemoMessagingSeeder : IDemoSeeder
{
    public string Key => "e3-messaging";

    public int Order => 250;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        if (await db.MessagingChannels.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var organisation = context.Organisation;
        var arabic = organisation.DefaultLanguage == LocalizedText.Arabic;
        var users = (await db.Users.Where(u => u.Email != null).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var requester = users[organisation.UsernameOf(organisation.User("requester")!)];
        var agent = users[organisation.UsernameOf(organisation.User("agent")!)];
        var group = await db.SupportGroups.AsNoTracking().Where(g => g.Members.Any(m => m.UserId == agent.Id)).OrderBy(g => g.CreatedAt)
            .Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var whatsApp = new MessagingChannel
        {
            Name = arabic ? "واتساب خدمة المتعاملين" : "Customer care WhatsApp",
            Kind = MessagingKind.WhatsApp,
            Provider = MessagingProvider.WhatsAppCloud,
            AccountId = "100000000000001",
            Sender = "+97180000001",
            GroupId = group,
            Settings = new MessagingSettings { WhatsApp = new WhatsAppSettings { DefaultTemplate = "ticket_update" } }.Write(),
            Active = false,
        };
        db.MessagingChannels.Add(whatsApp);
        db.MessagingChannels.Add(new MessagingChannel
        {
            Name = arabic ? "الرسائل النصية" : "Text messages",
            Kind = MessagingKind.Sms,
            Provider = MessagingProvider.GenericHttp,
            Sender = organisation.Slug.Contains("municipality", StringComparison.Ordinal) ? "MUNICIPAL" : "COMPANY",
            GroupId = group,
            Settings = new MessagingSettings { Gateway = new GenericSmsSettings { Url = $"https://sms.{organisation.EmailDomain}/api/send" } }.Write(),
            Active = false,
        });
        db.MessagingChannels.Add(new MessagingChannel
        {
            Name = "Microsoft Teams",
            Kind = MessagingKind.Teams,
            Provider = MessagingProvider.TeamsBot,
            AccountId = Guid.NewGuid().ToString(),
            DirectoryId = Guid.NewGuid().ToString(),
            NewSenders = MessagingSenders.KnownUsers,
            Active = false,
        });

        // The sample requester has a (fictional) mobile number and wrote in by WhatsApp.
        requester.Phone ??= "+971500000001";
        var contact = new MessagingContact { ChannelId = whatsApp.Id, Address = requester.Phone, UserId = requester.Id, LastInboundAt = now.AddHours(-3), CreatedAt = now.AddHours(-3) };
        db.MessagingContacts.Add(contact);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Classified like the other sample incidents of the requester.
        var like = await db.Tickets.AsNoTracking().Where(t => t.RequesterId == requester.Id && t.CategoryId != null && t.LocationId != null)
            .OrderBy(t => t.Sequence).Select(t => new { t.CategoryId, t.LocationId }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var text = arabic ? "الطابعة في الطابق الثاني لا تطبع منذ الصباح" : "The printer on the second floor has not printed since this morning";
        var prefixes = await TicketSettingKeys.ReadAsync<IReadOnlyDictionary<TicketType, string>>(db, TicketSettingKeys.NumberPrefixes,
            TicketNumbers.DefaultPrefixes, TicketNumbers.AreValid, cancellationToken).ConfigureAwait(false);
        var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken).ConfigureAwait(false);
        var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, TicketType.Incident, cancellationToken).ConfigureAwait(false);
        var at = now.AddHours(-3);
        var ticket = new Ticket
        {
            Type = TicketType.Incident,
            Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Incident), sequence),
            Sequence = sequence,
            Title = text,
            Description = text,
            Channel = TicketChannel.WhatsApp,
            RequesterId = requester.Id,
            CreatedById = requester.Id,
            GroupId = group,
            CategoryId = like?.CategoryId,
            LocationId = like?.LocationId,
            AssigneeId = agent.Id,
            State = TicketState.InProgress,
            MessagingContactId = contact.Id,
            CreatedAt = at,
            UpdatedAt = at,
        };
        ticket.Incident = new Incident { TicketId = ticket.Id, Impact = 2, Urgency = 2, Priority = matrix.PriorityOf(2, 2) };
        db.Tickets.Add(ticket);
        contact.TicketId = ticket.Id;
        db.ChannelMessages.Add(new ChannelMessage
        {
            ChannelId = whatsApp.Id, ContactId = contact.Id, Direction = MessageDirection.In, Kind = ChannelMessageKind.Text, ProviderMessageId = "wamid.demo.1",
            Text = text, Status = ChannelMessageStatus.Received, TicketId = ticket.Id, CreatedAt = now.AddHours(-3),
        });

        // A voice note, transcribed.
        var voice = new TicketComment
        {
            TicketId = ticket.Id, Visibility = CommentVisibility.Public, Body = string.Empty, Language = organisation.DefaultLanguage, AuthorId = requester.Id,
            Source = CommentSource.WhatsApp, MessagingChannelId = whatsApp.Id, CreatedAt = now.AddHours(-2.5),
        };
        db.TicketComments.Add(voice);
        byte[] recording = [.. "OggS"u8.ToArray(), .. new byte[60]];
        var attachment = new TicketAttachment
        {
            TicketId = ticket.Id, CommentId = voice.Id, FileName = "voice-note.ogg", ContentType = "audio/ogg", Size = recording.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(recording)), ScanStatus = AttachmentScanStatus.Clean, UploadedById = requester.Id,
        };
        db.TicketAttachments.Add(attachment);
        new PostgresAttachmentStore(db).Add(attachment.Id, recording);
        db.VoiceNotes.Add(new VoiceNote
        {
            CommentId = voice.Id, TicketId = ticket.Id, AttachmentId = attachment.Id, Status = TranscriptStatus.Done, Language = organisation.DefaultLanguage,
            Transcript = arabic ? "تظهر رسالة انحشار الورق، وقد حاولت إعادة تشغيل الطابعة دون فائدة." : "It shows a paper jam, and I already tried switching it off and on.",
            CreatedAt = voice.CreatedAt, TranscribedAt = voice.CreatedAt.AddMinutes(1),
        });

        // The agent's reply went back on WhatsApp and was read.
        var reply = new TicketComment
        {
            TicketId = ticket.Id, Visibility = CommentVisibility.Public, Language = organisation.DefaultLanguage, AuthorId = agent.Id, ByStaff = true,
            Body = arabic ? "شكرًا لتواصلكم. سيصل أحد الفنيين خلال ساعة." : "Thank you for letting us know. A technician will be with you within the hour.",
            CreatedAt = now.AddHours(-2),
        };
        db.TicketComments.Add(reply);
        ticket.RespondedAt ??= reply.CreatedAt;
        db.ChannelMessages.Add(new ChannelMessage
        {
            ChannelId = whatsApp.Id, ContactId = contact.Id, Direction = MessageDirection.Out, Kind = ChannelMessageKind.Text, Address = contact.Address,
            Text = $"{ticket.Number}: {reply.Body}", Language = organisation.DefaultLanguage, Status = ChannelMessageStatus.Read, ProviderMessageId = "wamid.demo.2",
            TicketId = ticket.Id, CommentId = reply.Id, Attempts = 1, CreatedAt = reply.CreatedAt, SentAt = reply.CreatedAt,
        });
        await new Tickets.Sla.SlaTracker(db, context.Get<Data.Auditing.IAuditLog>()).UpdateAsync([ticket], now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // In the API, saving the incident queued "received" already; migrate has no notification interceptor.
        if (!await db.Notifications.AnyAsync(n => n.TicketId == ticket.Id && n.EventType == Zelvo.Notifications.NotificationEvents.IncidentReceived, cancellationToken).ConfigureAwait(false))
        {
            db.Notifications.Add(new Data.Notifications.Notification
            {
                EventType = Zelvo.Notifications.NotificationEvents.IncidentReceived, RecipientId = requester.Id, TicketId = ticket.Id, LinkPath = $"/my-incidents/{ticket.Id}",
                CreatedAt = at,
                Data = Zelvo.Notifications.NotificationData.Serialize(new Dictionary<string, Zelvo.Notifications.PlaceholderValue>(StringComparer.Ordinal)
                {
                    [Zelvo.Notifications.Placeholders.TicketNumber] = Zelvo.Notifications.PlaceholderValue.OfText(ticket.Number),
                    [Zelvo.Notifications.Placeholders.TicketTitle] = Zelvo.Notifications.PlaceholderValue.OfText(ticket.Title),
                    [Zelvo.Notifications.Placeholders.TicketCreatedAt] = Zelvo.Notifications.PlaceholderValue.OfDate(ticket.CreatedAt),
                }),
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
