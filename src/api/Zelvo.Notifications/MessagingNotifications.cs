using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Channels.Messaging;
using Zelvo.Data;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Notifications;

namespace Zelvo.Notifications;

/// <summary>
/// Notifications by WhatsApp, SMS and Microsoft Teams (module 10 and module 6, task E3; product
/// owner decisions 2026-11-25), queued in <c>channel_messages</c> when a notification is rendered:
/// <list type="bullet">
/// <item>A requester's notifications about a ticket that came in through WhatsApp, SMS or Teams go
/// back through that conversation (staff replies always, as they are the conversation itself).</item>
/// <item>Otherwise each person chooses under Account → Notifications: WhatsApp and SMS to the phone
/// of their profile (opt-in), Teams once they have used the tenant's Teams app (on by default).</item>
/// <item>WhatsApp more than 24 hours after the person last wrote uses the Meta-approved template the
/// administrator named for the event; without one the message is logged as not sent.</item>
/// <item>Approvers get a card with Approve and Reject in Teams, which decides there; WhatsApp and
/// SMS carry the two links of the email, which open Zelvo where the approver signs in.</item>
/// </list>
/// </summary>
internal sealed class MessagingNotifications(ZelvoDbContext db)
{
    private static readonly TimeSpan WhatsAppWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Task K1: the channels a workflow rule's notification names (<c>WhatsApp,Sms</c>), or
    /// <see langword="null"/> when the rule predates K1 and people's own choices apply.
    /// </summary>
    internal static IReadOnlySet<MessagingKind>? RuleChannels(string? value) =>
        value is null
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => Enum.TryParse<MessagingKind>(v, out var kind) ? (MessagingKind?)kind : null).OfType<MessagingKind>().ToHashSet();

    /// <summary>
    /// Queues the channel messages of <paramref name="notification"/> for <paramref name="recipient"/>.
    /// With <paramref name="only"/> (a workflow rule's channels, task K1) the rule decides which
    /// channels are used instead of the event's setting; each person's own opt-in for WhatsApp, SMS
    /// and Teams still applies, as it is their phone and their consent.
    /// </summary>
    public async Task QueueAsync(
        Notification notification, UserAccount recipient, NotificationEventDefinition definition, EventSetting setting, RenderedNotification rendered,
        string language, NotificationLinks links, DateTimeOffset now, CancellationToken cancellationToken, IReadOnlySet<MessagingKind>? only = null)
    {
        if (only is { Count: 0 })
        {
            return;
        }

        var channels = await db.MessagingChannels.AsNoTracking().Where(c => c.Active && (only == null || only.Contains(c.Kind)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (channels.Count == 0)
        {
            return;
        }

        var reply = definition.Type is NotificationEvents.IncidentReplied or NotificationEvents.VoiceReplied && notification.CommentId is not null;
        // The ticket's own conversation, when the recipient is the person in it.
        if (definition.Audience == NotificationAudience.Requester && notification.TicketId is { } ticketId)
        {
            var ticket = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => new { t.Number, t.MessagingContactId }).FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var contact = ticket?.MessagingContactId is { } contactId
                ? await db.MessagingContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId && c.UserId == recipient.Id, cancellationToken).ConfigureAwait(false)
                : null;
            if (contact is not null && channels.FirstOrDefault(c => c.Id == contact.ChannelId) is { } own)
            {
                if (reply)
                {
                    var body = await db.TicketComments.AsNoTracking().Where(c => c.Id == notification.CommentId).Select(c => c.Body).FirstAsync(cancellationToken).ConfigureAwait(false);
                    Queue(notification, own, contact, contact.Address, $"{ticket!.Number}: {body}", language, now, definition.Type);
                }
                else if (setting.Enabled && setting.Messaging)
                {
                    Queue(notification, own, contact, contact.Address, Text(rendered, links, language, definition.Type), language, now, definition.Type);
                }

                return;
            }
        }

        if (!setting.Enabled || (!setting.Messaging && only is null))
        {
            return;
        }

        var text = Text(rendered, links, language, definition.Type);
        if (recipient.NotifyTeams && channels.Where(c => c.Kind == MessagingKind.Teams).Select(c => c.Id).ToList() is { Count: > 0 } teamsChannels)
        {
            var contact = await db.MessagingContacts.AsNoTracking()
                .Where(c => teamsChannels.Contains(c.ChannelId) && c.UserId == recipient.Id && c.TeamsConversationId != null)
                .OrderByDescending(c => c.LastInboundAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (contact is not null)
            {
                var channel = channels.Single(c => c.Id == contact.ChannelId);
                if (definition.Type == NotificationEvents.ApprovalRequested && DecisionOf(notification.LinkPath) is { } decisionId)
                {
                    Add(notification, channel, contact, contact.Address, ChannelMessageKind.Card, text,
                        ApprovalCard(rendered, links, language, decisionId).ToJsonString(), language, now, decisionId);
                }
                else
                {
                    Queue(notification, channel, contact, contact.Address, text, language, now, definition.Type);
                }
            }
        }

        if (recipient.Phone is not { Length: > 0 } phone)
        {
            return;
        }

        if (recipient.NotifyWhatsApp && channels.FirstOrDefault(c => c.Kind == MessagingKind.WhatsApp) is { } whatsApp)
        {
            var contact = await db.MessagingContacts.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelId == whatsApp.Id && c.Address == phone, cancellationToken).ConfigureAwait(false);
            Queue(notification, whatsApp, contact, phone, text, language, now, definition.Type);
        }

        if (recipient.NotifySms && channels.FirstOrDefault(c => c.Kind == MessagingKind.Sms) is { } sms)
        {
            Queue(notification, sms, null, phone, text, language, now, definition.Type);
        }
    }

    // Text, or for WhatsApp outside its window the event's template.
    private void Queue(Notification notification, MessagingChannel channel, MessagingContact? contact, string address, string text, string language, DateTimeOffset now, string eventType)
    {
        if (channel.Kind == MessagingKind.WhatsApp && (contact?.LastInboundAt is not { } last || now - last > WhatsAppWindow))
        {
            var settings = MessagingSettings.Read(channel.Settings).WhatsApp ?? new WhatsAppSettings();
            if (settings.TemplateFor(eventType) is not { } template)
            {
                var skipped = Add(notification, channel, contact, address, ChannelMessageKind.Template, text, null, language, now, null);
                skipped.Status = ChannelMessageStatus.Failed;
                skipped.Reason = "no-template";
                skipped.NextAttemptAt = null;
                return;
            }

            Add(notification, channel, contact, address, ChannelMessageKind.Template, text,
                new JsonObject { ["name"] = template, ["language"] = settings.CodeFor(language) }.ToJsonString(), language, now, null);
            return;
        }

        Add(notification, channel, contact, address, ChannelMessageKind.Text, text, null, language, now, null);
    }

    private ChannelMessage Add(
        Notification notification, MessagingChannel channel, MessagingContact? contact, string address, ChannelMessageKind kind, string text, string? payload, string language,
        DateTimeOffset now, Guid? decisionId)
    {
        var message = new ChannelMessage
        {
            TenantId = notification.TenantId,
            ChannelId = channel.Id,
            ContactId = contact?.Id,
            Direction = MessageDirection.Out,
            Kind = kind,
            Address = address,
            Text = text.Length <= ChannelMessage.TextMaxLength ? text : text[..ChannelMessage.TextMaxLength],
            Payload = payload,
            Language = language,
            Status = ChannelMessageStatus.Pending,
            NextAttemptAt = now,
            TicketId = notification.TicketId,
            CommentId = notification.CommentId,
            NotificationId = notification.Id,
            ApprovalDecisionId = decisionId,
            CreatedAt = now,
        };
        db.ChannelMessages.Add(message);
        return message;
    }

    /// <summary>The notification as a short message: subject, in-app text and the link (approvals: both links).</summary>
    internal static string Text(RenderedNotification rendered, NotificationLinks links, string language, string eventType)
    {
        var lines = new List<string> { rendered.Subject };
        if (!string.Equals(rendered.InApp, rendered.Subject, StringComparison.Ordinal))
        {
            lines.Add(rendered.InApp);
        }

        var terms = NotificationTexts.For(language).Terms;
        if (eventType == NotificationEvents.ApprovalRequested && links.Action is { } approve && links.Secondary is { } reject)
        {
            lines.Add($"{terms.GetValueOrDefault("approval.approve")}: {approve.AbsoluteUri}");
            lines.Add($"{terms.GetValueOrDefault("approval.reject")}: {reject.AbsoluteUri}");
        }
        else if (links.Action is { } link)
        {
            lines.Add(link.AbsoluteUri);
        }

        return string.Join('\n', lines);
    }

    /// <summary>The Teams card of an approval: the request, a reason box, Approve, Reject and a link to the approval.</summary>
    internal static JsonObject ApprovalCard(RenderedNotification rendered, NotificationLinks links, string language, Guid decisionId)
    {
        var terms = NotificationTexts.For(language).Terms;
        var actions = new JsonArray
        {
            new JsonObject { ["type"] = "Action.Submit", ["title"] = terms.GetValueOrDefault("approval.approve"), ["style"] = "positive", ["data"] = Data("approve") },
            new JsonObject { ["type"] = "Action.Submit", ["title"] = terms.GetValueOrDefault("approval.reject"), ["style"] = "destructive", ["data"] = Data("reject") },
        };
        if (links.Action is { } open)
        {
            // The link without the choice: it opens the approval.
            actions.Add(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = terms.GetValueOrDefault("approval.open"), ["url"] = open.GetLeftPart(UriPartial.Path) });
        }

        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.4",
            ["rtl"] = language == "ar",
            ["body"] = new JsonArray
            {
                new JsonObject { ["type"] = "TextBlock", ["text"] = rendered.Subject, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
                new JsonObject { ["type"] = "TextBlock", ["text"] = rendered.InApp, ["wrap"] = true },
                new JsonObject { ["type"] = "Input.Text", ["id"] = "reason", ["label"] = terms.GetValueOrDefault("approval.reason"), ["isMultiline"] = true, ["maxLength"] = 1000 },
            },
            ["actions"] = actions,
        };

        JsonObject Data(string decision) => new() { ["zelvo"] = "approval", ["decision"] = decision, ["decisionId"] = decisionId.ToString() };
    }

    /// <summary>The approval decision of a link path <c>/approvals/{id}</c>.</summary>
    internal static Guid? DecisionOf(string? linkPath) =>
        linkPath is not null && linkPath.StartsWith("/approvals/", StringComparison.Ordinal) && Guid.TryParse(linkPath["/approvals/".Length..], out var id) ? id : null;
}
