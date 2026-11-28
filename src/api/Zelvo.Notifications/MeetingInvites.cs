using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Changes;
using Zelvo.Data.Localization;

namespace Zelvo.Notifications;

/// <summary>
/// Calendar files of change advisory board meetings (task H4): an iCalendar (RFC 5545) event the
/// member's calendar adds, updates (same UID, higher SEQUENCE) or removes (METHOD:CANCEL),
/// attached to the invitation emails. The title is in the recipient's language; the description
/// lists the changes on the agenda.
/// </summary>
public static class MeetingInvites
{
    /// <summary>Path of a meeting in Zelvo (also how a notification names its meeting).</summary>
    public static string MeetingPath(Guid meetingId) => $"/changes/board/{meetingId}";

    /// <summary>The meeting a notification's link points to.</summary>
    public static Guid? MeetingOf(string? linkPath) =>
        linkPath is not null && linkPath.StartsWith("/changes/board/", StringComparison.Ordinal) && Guid.TryParse(linkPath["/changes/board/".Length..], out var id) ? id : null;

    /// <summary>The calendar file for <paramref name="attendee"/>, or null when the meeting is gone.</summary>
    public static async Task<EmailAttachment?> AttachmentAsync(
        ZelvoDbContext db, Guid meetingId, string attendee, string language, bool cancel, string organiser, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var meeting = await db.CabMeetings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken).ConfigureAwait(false);
        if (meeting is null)
        {
            return null;
        }

        var numbers = await db.CabAgendaItems.AsNoTracking().Where(i => i.MeetingId == meetingId).OrderBy(i => i.Position)
            .Join(db.Tickets.AsNoTracking(), i => i.ChangeId, t => t.Id, (i, t) => t.Number + " " + t.Title).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        var method = cancel || meeting.State == CabMeetingState.Cancelled ? "CANCEL" : "REQUEST";
        var ics = Build(meeting, numbers, attendee, language, method, organiser, now);
        return new EmailAttachment("meeting.ics", $"text/calendar; method={method}; charset=utf-8", Encoding.UTF8.GetBytes(ics));
    }

    /// <summary>The iCalendar text of <paramref name="meeting"/>.</summary>
    public static string Build(CabMeeting meeting, IReadOnlyList<string> agenda, string attendee, string language, string method, string organiser, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(agenda);
        var title = meeting.Title.GetOrFallback(language) ?? "Change advisory board";
        var heading = language == LocalizedText.Arabic ? "جدول الأعمال:" : "Agenda:";
        var description = agenda.Count == 0 ? string.Empty : $"{heading}\n{string.Join('\n', agenda)}";
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Zelvo//Change advisory board//EN",
            "CALSCALE:GREGORIAN",
            $"METHOD:{method}",
            "BEGIN:VEVENT",
            $"UID:{meeting.Id:D}@zelvo",
            $"SEQUENCE:{meeting.Sequence.ToString(CultureInfo.InvariantCulture)}",
            $"DTSTAMP:{Utc(now)}",
            $"DTSTART:{Utc(meeting.StartsAt)}",
            $"DTEND:{Utc(meeting.EndsAt)}",
            $"SUMMARY:{Escape(title)}",
        };
        if (description.Length > 0)
        {
            lines.Add($"DESCRIPTION:{Escape(description)}");
        }

        if (meeting.Location is { Length: > 0 } location)
        {
            lines.Add($"LOCATION:{Escape(location)}");
        }

        lines.Add($"ORGANIZER:mailto:{organiser}");
        lines.Add($"ATTENDEE;ROLE=REQ-PARTICIPANT;RSVP=FALSE:mailto:{attendee}");
        lines.Add($"STATUS:{(method == "CANCEL" ? "CANCELLED" : "CONFIRMED")}");
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");
        return string.Concat(lines.Select(l => Fold(l) + "\r\n"));
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>TEXT values escape backslashes, semicolons, commas and line breaks (RFC 5545 3.3.11).</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>Lines longer than 75 octets are folded (RFC 5545 3.1), never inside a UTF-8 character.</summary>
    public static string Fold(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var result = new StringBuilder();
        var octets = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                result.Append("\r\n ");
                octets = 0;
                limit = 74;
            }

            result.Append(rune.ToString());
            octets += size;
        }

        return result.ToString();
    }
}

/// <summary>
/// Board meeting notifications (task H4): members (and the chair) are invited when a meeting is
/// planned and again when it changes, with a calendar file; they hear when it is called off, and
/// members taken off a meeting get its cancellation. The person who planned it is invited too, so
/// that it reaches their calendar.
/// </summary>
public sealed class MeetingNotificationSource : INotificationSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var meetings = db.ChangeTracker.Entries<CabMeeting>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        if (meetings.Count == 0)
        {
            return [];
        }

        var result = new List<NotificationRequest>();
        foreach (var entry in meetings)
        {
            var meeting = entry.Entity;
            var added = db.ChangeTracker.Entries<CabMeetingMember>().Where(e => e.State == EntityState.Added && e.Entity.MeetingId == meeting.Id).Select(e => e.Entity.UserId).ToList();
            var removed = db.ChangeTracker.Entries<CabMeetingMember>().Where(e => e.State == EntityState.Deleted && e.Entity.MeetingId == meeting.Id).Select(e => e.Entity.UserId).ToList();
            var saved = entry.State == EntityState.Added ? [] : await db.CabMeetingMembers.AsNoTracking().Where(m => m.MeetingId == meeting.Id).Select(m => m.UserId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var members = saved.Concat(added).Except(removed).Append(meeting.ChairId).Distinct().ToList();
            var items = db.ChangeTracker.Entries<CabAgendaItem>().Count(e => e.State == EntityState.Added && e.Entity.MeetingId == meeting.Id)
                + (entry.State == EntityState.Added ? 0 : await db.CabAgendaItems.CountAsync(i => i.MeetingId == meeting.Id, cancellationToken).ConfigureAwait(false));
            var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
            {
                [Placeholders.MeetingTitle] = new PlaceholderValue { En = meeting.Title.En ?? meeting.Title.Ar, Ar = meeting.Title.Ar ?? meeting.Title.En },
                [Placeholders.MeetingStart] = PlaceholderValue.OfDate(meeting.StartsAt),
                [Placeholders.MeetingEnd] = PlaceholderValue.OfDate(meeting.EndsAt),
                [Placeholders.MeetingLocation] = PlaceholderValue.OfText(meeting.Location),
                [Placeholders.MeetingItems] = new PlaceholderValue { Number = items },
            };
            var path = MeetingInvites.MeetingPath(meeting.Id);
            var cancelled = meeting.State == CabMeetingState.Cancelled
                && (entry.State == EntityState.Added || entry.Property(m => m.State).OriginalValue != CabMeetingState.Cancelled);
            if (cancelled)
            {
                result.Add(new(NotificationEvents.CabCancelled, members.Concat(removed).Distinct().ToList(), values, path));
                continue;
            }

            if (meeting.State != CabMeetingState.Planned)
            {
                continue;
            }

            if (entry.State == EntityState.Added || entry.Property(m => m.Sequence).IsModified)
            {
                result.Add(new(NotificationEvents.CabInvited, members, values, path));
            }

            if (removed.Count > 0)
            {
                result.Add(new(NotificationEvents.CabCancelled, removed, values, path));
            }
        }

        _ = actorId;
        return result;
    }
}
