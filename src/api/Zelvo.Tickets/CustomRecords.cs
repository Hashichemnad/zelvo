using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Records;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Tickets;

/// <summary>
/// The state categories of the ticket core that a custom record type's states are mapped onto
/// (task K2; product owner decision 20 of 2026-11-26). The category decides the ticket's state,
/// so SLA clocks, reports, auto-close and notifications keep working for every custom type.
/// </summary>
public enum RecordStateCategory
{
    /// <summary>Not started (ticket New or Assigned). The type's first New state is where records start.</summary>
    New,

    /// <summary>Being worked on (In progress, or Reopened).</summary>
    InProgress,

    /// <summary>Waiting, with an on-hold reason.</summary>
    OnHold,

    /// <summary>Done; the requester can reopen it until it closes.</summary>
    Resolved,

    /// <summary>Final.</summary>
    Closed,
}

/// <summary>A state of a custom record type: key, bilingual name and the core's category.</summary>
public sealed record RecordState(string Key, SettingText Name, RecordStateCategory Category);

/// <summary>Reading, checking and following the states of custom record types.</summary>
public static partial class RecordStates
{
    /// <summary>Most states of one type.</summary>
    public const int MaxStates = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The states stored on a type, in order.</summary>
    public static IReadOnlyList<RecordState> Read(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<RecordState>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The states as stored.</summary>
    public static string Write(IReadOnlyList<RecordState> states) => JsonSerializer.Serialize(states, Json);

    /// <summary>Whether <paramref name="key"/> is a valid state key.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>
    /// Why <paramref name="states"/> cannot be a type's states, or <see langword="null"/>: 2–20
    /// states with unique keys and names, at least one New, one Resolved and one Closed.
    /// </summary>
    public static string? Problem(IReadOnlyList<RecordState>? states)
    {
        if (states is not { Count: >= 3 and <= MaxStates })
        {
            return $"Give 3 to {MaxStates} states.";
        }

        if (states.Any(s => s is null || !IsKey(s.Key) || s.Name is not { IsValid: true } || !Enum.IsDefined(s.Category)))
        {
            return "Each state needs a key (lower-case letters, digits and hyphens), a name in English or Arabic and a category.";
        }

        if (states.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != states.Count)
        {
            return "State keys must be different.";
        }

        return new[] { RecordStateCategory.New, RecordStateCategory.Resolved, RecordStateCategory.Closed }.All(c => states.Any(s => s.Category == c))
            ? null
            : "Give at least one New, one Resolved and one Closed state.";
    }

    /// <summary>The category of a ticket state.</summary>
    public static RecordStateCategory CategoryOf(TicketState state) => state switch
    {
        TicketState.New or TicketState.Assigned => RecordStateCategory.New,
        TicketState.OnHold => RecordStateCategory.OnHold,
        TicketState.Resolved => RecordStateCategory.Resolved,
        TicketState.Closed => RecordStateCategory.Closed,
        _ => RecordStateCategory.InProgress,
    };

    /// <summary>
    /// The ticket state a move to a state of <paramref name="category"/> means, from <paramref name="current"/>:
    /// back to work from Resolved is a reopening.
    /// </summary>
    public static TicketState TargetOf(RecordStateCategory category, TicketState current) => category switch
    {
        RecordStateCategory.New => TicketState.New,
        RecordStateCategory.OnHold => TicketState.OnHold,
        RecordStateCategory.Resolved => TicketState.Resolved,
        RecordStateCategory.Closed => TicketState.Closed,
        _ => current == TicketState.Resolved ? TicketState.Reopened : TicketState.InProgress,
    };

    /// <summary>The state a new record starts in: the first New state.</summary>
    public static RecordState Initial(IReadOnlyList<RecordState> states) => states.First(s => s.Category == RecordStateCategory.New);

    /// <summary>The state key that fits the ticket's state: the record's own when its category matches, else the first state of that category.</summary>
    public static string Follow(IReadOnlyList<RecordState> states, string current, TicketState state)
    {
        var category = CategoryOf(state);
        return states.FirstOrDefault(s => s.Key == current)?.Category == category
            ? current
            : states.FirstOrDefault(s => s.Category == category)?.Key ?? current;
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// Permissions made per custom record type (task K2): <c>records.&lt;key&gt;.read</c> (see every
/// record of the type; requesters always see their own), <c>.edit</c> (work on them: states,
/// details, replies and notes, logging them for others) and <c>.assign</c> (groups and assignees).
/// </summary>
public static partial class RecordPermissions
{
    /// <summary>The three actions.</summary>
    public static IReadOnlyList<string> Actions { get; } = ["read", "edit", "assign"];

    /// <summary>Permission to see every record of the type.</summary>
    public static string Read(string key) => $"records.{key}.read";

    /// <summary>Permission to work on records of the type.</summary>
    public static string Edit(string key) => $"records.{key}.edit";

    /// <summary>Permission to assign records of the type.</summary>
    public static string Assign(string key) => $"records.{key}.assign";

    /// <summary>The type key and action of a per-type permission, or <see langword="null"/>.</summary>
    public static (string Key, string Action)? Parse(string? permission)
    {
        var match = permission is null ? null : PermissionPattern().Match(permission);
        return match is { Success: true } ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    [GeneratedRegex("^records\\.([a-z](?:[a-z0-9-]{0,38}[a-z0-9])?)\\.(read|edit|assign)$")]
    private static partial Regex PermissionPattern();
}

/// <summary>Custom records in the search index (task K2): the requester, the assignee and roles with the type's read permission.</summary>
public static class RecordSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "record";

    /// <summary>Keys of the roles that see every record of <paramref name="typeKey"/>.</summary>
    public static async Task<IReadOnlyList<string>> StaffRolesAsync(ZelvoDbContext db, string typeKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var permission = RecordPermissions.Read(typeKey);
        return await db.Roles.Where(r => r.Permissions.Any(p => p.Permission == permission))
            .Select(r => r.Key).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The search document of a custom record.</summary>
    public static SearchDocument Document(Ticket ticket, RecordType type, IReadOnlyList<string> staffRoles)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(staffRoles);
        var readers = new List<string> { SearchReaders.User(ticket.RequesterId) };
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
                ["recordType"] = type.Key,
            },
            Url = $"/records/{type.Key}/{ticket.Id}",
            UpdatedAt = ticket.UpdatedAt,
        };
    }

    /// <summary>Upserts the documents of <paramref name="tickets"/> (custom records only).</summary>
    public static async Task IndexAsync(ZelvoDbContext db, ISearchIndexer indexer, IEnumerable<Ticket> tickets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(indexer);
        var roles = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var ticket in tickets.Where(t => t.RecordTypeId is not null))
        {
            var type = await db.RecordTypes.AsNoTracking().SingleAsync(r => r.Id == ticket.RecordTypeId, cancellationToken).ConfigureAwait(false);
            if (!roles.TryGetValue(type.Id, out var staff))
            {
                roles[type.Id] = staff = await StaffRolesAsync(db, type.Key, cancellationToken).ConfigureAwait(false);
            }

            indexer.Upsert(Document(ticket, type, staff));
        }
    }
}

/// <summary>Every custom record of the tenant, for rebuilding the search index.</summary>
public sealed class RecordSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => RecordSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var types = await db.RecordTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, cancellationToken).ConfigureAwait(false);
        var roles = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var type in types.Values)
        {
            roles[type.Id] = await RecordSearch.StaffRolesAsync(db, type.Key, cancellationToken).ConfigureAwait(false);
        }

        var tickets = db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Custom).OrderBy(t => t.Id).AsAsyncEnumerable();
        await foreach (var ticket in tickets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return RecordSearch.Document(ticket, types[ticket.RecordTypeId!.Value], roles[ticket.RecordTypeId.Value]);
        }
    }
}

/// <summary>
/// Keeps a custom record's state of its type in step with the ticket core (task K2): whenever the
/// ticket's state changes without the record's state (auto-close, assignment, a resolved part of
/// a problem, approvals), the record moves to the first state of the new category. Runs in every
/// save of the API and the worker.
/// </summary>
public sealed class CustomRecordStateInterceptor : SaveChangesInterceptor, IZelvoSaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        FollowAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await FollowAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task FollowAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is not ZelvoDbContext db)
        {
            return;
        }

        var tickets = db.ChangeTracker.Entries<Ticket>()
            .Where(e => e.Entity.Type == TicketType.Custom && e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity).ToList();
        foreach (var ticket in tickets)
        {
            var record = ticket.Custom
                ?? db.ChangeTracker.Entries<CustomRecord>().FirstOrDefault(e => e.Entity.TicketId == ticket.Id)?.Entity
                ?? await db.CustomRecords.FirstOrDefaultAsync(r => r.TicketId == ticket.Id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            var states = RecordStates.Read(await db.RecordTypes.AsNoTracking().Where(t => t.Id == record.RecordTypeId).Select(t => t.States)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false));
            var key = RecordStates.Follow(states, record.StateKey, ticket.State);
            if (key != record.StateKey)
            {
                record.StateKey = key;
            }
        }
    }
}
