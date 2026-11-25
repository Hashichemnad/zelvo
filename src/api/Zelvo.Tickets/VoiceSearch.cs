using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Tickets;

/// <summary>Permission keys of complaints, inquiries and suggestions (module 3, task C6). The API's permission catalog lists them.</summary>
public static class VoicePermissions
{
    /// <summary>See every complaint, inquiry and suggestion (requesters always see their own).</summary>
    public const string Read = "voice.read";

    /// <summary>Work on them: states, severity, root cause, reviews, conversions, replies and notes.</summary>
    public const string Edit = "voice.edit";

    /// <summary>Assign them to groups and agents.</summary>
    public const string Assign = "voice.assign";

    /// <summary>See happiness and customer-voice reporting (GOV-3).</summary>
    public const string Report = "voice.report";

    /// <summary>Settings → Customer voice: anonymous submission, severities, root causes, happiness meter.</summary>
    public const string Manage = "voice.manage";
}

/// <summary>Record types of customer voice (module 3).</summary>
public static class VoiceTypes
{
    /// <summary>Complaints, inquiries and suggestions.</summary>
    public static IReadOnlyList<TicketType> All { get; } = [TicketType.Complaint, TicketType.Inquiry, TicketType.Suggestion];

    /// <summary>Whether <paramref name="type"/> is a customer voice record type.</summary>
    public static bool Is(TicketType type) => type is TicketType.Complaint or TicketType.Inquiry or TicketType.Suggestion;

    /// <summary>Path segment of the record type in the API and web app (<c>complaints</c>).</summary>
    public static string PathOf(TicketType type) => type switch
    {
        TicketType.Complaint => "complaints",
        TicketType.Inquiry => "inquiries",
        TicketType.Suggestion => "suggestions",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

/// <summary>
/// Complaints, inquiries and suggestions in the search index (LANG-9, task C6), like incidents: the
/// requester (not for anonymous ones), the assignee and roles with <see cref="VoicePermissions.Read"/>.
/// </summary>
public static class VoiceSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "voice";

    /// <summary>Keys of the roles that see every customer voice record.</summary>
    public static async Task<IReadOnlyList<string>> StaffRolesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Roles.Where(r => r.Permissions.Any(p => p.Permission == VoicePermissions.Read))
            .Select(r => r.Key).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The search document of a complaint, inquiry or suggestion.</summary>
    public static SearchDocument Document(Ticket ticket, IReadOnlyList<string> staffRoles)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(staffRoles);
        var readers = new List<string>();
        if (ticket.Voice is not { Anonymous: true })
        {
            readers.Add(SearchReaders.User(ticket.RequesterId));
        }

        if (ticket.AssigneeId is { } assignee)
        {
            readers.Add(SearchReaders.User(assignee));
        }

        readers.AddRange(staffRoles.Select(SearchReaders.Role));
        var title = $"{ticket.Number} {ticket.Title}";
        return new SearchDocument
        {
            Type = Type,
            Id = ticket.Id.ToString(),
            Title = new LocalizedText(title, title),
            Text = ticket.Description is { Length: > 0 } description ? [description] : [],
            Keywords = [ticket.Number],
            Readers = [.. readers.Distinct(StringComparer.Ordinal)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = ticket.Number,
                ["state"] = ticket.State.ToString(),
                ["recordType"] = ticket.Type.ToString(),
            },
            Url = $"/customer-voice/{ticket.Id}",
            UpdatedAt = ticket.UpdatedAt,
        };
    }
}

/// <summary>Every complaint, inquiry and suggestion of the tenant, for rebuilding the search index.</summary>
public sealed class VoiceSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => VoiceSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var staff = await VoiceSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        var tickets = db.Tickets.AsNoTracking().Include(t => t.Voice)
            .Where(t => t.Type == TicketType.Complaint || t.Type == TicketType.Inquiry || t.Type == TicketType.Suggestion)
            .OrderBy(t => t.Id).AsAsyncEnumerable();
        await foreach (var ticket in tickets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return VoiceSearch.Document(ticket, staff);
        }
    }
}

/// <summary>
/// The tenant's anonymous account (task C6): the requester of complaints, inquiries and suggestions
/// visitors send without signing in. It is disabled, has no email and no roles, so nobody can sign
/// in as it and no notification reaches anyone through it.
/// </summary>
public static class AnonymousAccounts
{
    /// <summary>Subject of the anonymous account.</summary>
    public const string Subject = "system:anonymous";

    /// <summary>Whether <paramref name="subject"/> is a system account's (never listed with people).</summary>
    public static bool IsSystem(string? subject) => subject?.StartsWith("system:", StringComparison.Ordinal) == true;

    /// <summary>The anonymous account's id; staged for saving by the caller when it does not exist yet.</summary>
    public static async Task<Guid> GetOrCreateAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var id = await db.Users.Where(u => u.Subject == Subject).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (id is { } existing)
        {
            return existing;
        }

        var account = new Data.Identity.UserAccount { Subject = Subject, Status = Data.Identity.UserStatus.Disabled };
        db.Users.Add(account);
        return account.Id;
    }
}
