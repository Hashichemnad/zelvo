using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Problems;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Tickets;

/// <summary>Permission keys of problem management (module 16, task H1). The API's permission catalog lists them.</summary>
public static class ProblemPermissions
{
    /// <summary>See problems, their root-cause analysis, links and the known error database.</summary>
    public const string Read = "problems.read";

    /// <summary>
    /// Work on problems: raise them from incidents or by hand, write the analysis, link incidents,
    /// configuration items and articles, move them through Assessment and Root-cause analysis,
    /// write known errors, reply and add notes.
    /// </summary>
    public const string Edit = "problems.edit";

    /// <summary>
    /// The problem manager (product owner decision 11 of 2026-11-26): assign problems, record them
    /// as known errors, resolve and close them (and their incidents), publish and retire known errors.
    /// </summary>
    public const string Manage = "problems.manage";
}

/// <summary>
/// The stages module 16 names, shown to people. The ticket core's state decides New, Resolved and
/// Closed; while the problem is worked on (assigned, in progress, on hold or reopened) its phase
/// decides Assessment, Root-cause analysis or Known error.
/// </summary>
public enum ProblemStage
{
    /// <summary>Logged, work not started.</summary>
    New,

    /// <summary>Being assessed.</summary>
    Assessment,

    /// <summary>Root cause being analysed.</summary>
    RootCauseAnalysis,

    /// <summary>Cause known, workaround recorded.</summary>
    KnownError,

    /// <summary>Fixed for good.</summary>
    Resolved,

    /// <summary>Final.</summary>
    Closed,
}

/// <summary>How a problem's stage follows from its ticket state and phase (task H1).</summary>
public static class ProblemStages
{
    /// <summary>The stage of a problem.</summary>
    public static ProblemStage Of(TicketState state, ProblemPhase phase) => state switch
    {
        TicketState.New or TicketState.Assigned => ProblemStage.New,
        TicketState.Resolved => ProblemStage.Resolved,
        TicketState.Closed => ProblemStage.Closed,
        _ => phase switch
        {
            ProblemPhase.RootCauseAnalysis => ProblemStage.RootCauseAnalysis,
            ProblemPhase.KnownError => ProblemStage.KnownError,
            _ => ProblemStage.Assessment,
        },
    };
}

/// <summary>Problems in the search index (task H1): found by the roles with <see cref="ProblemPermissions.Read"/> and the assignee.</summary>
public static class ProblemSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "problem";

    /// <summary>Document type of known errors.</summary>
    public const string KnownErrorType = "known-error";

    /// <summary>Keys of the roles that see problems.</summary>
    public static async Task<IReadOnlyList<string>> StaffRolesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Roles.Where(r => r.Permissions.Any(p => p.Permission == ProblemPermissions.Read))
            .Select(r => r.Key).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The search document of a problem.</summary>
    public static SearchDocument Document(Ticket ticket, IReadOnlyList<string> staffRoles)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(staffRoles);
        var readers = staffRoles.Select(SearchReaders.Role).ToList();
        if (ticket.AssigneeId is { } assignee)
        {
            readers.Add(SearchReaders.User(assignee));
        }

        var title = $"{ticket.Number} {ticket.Title}";
        var text = new List<string>();
        if (ticket.Description is { Length: > 0 } description)
        {
            text.Add(description);
        }

        if (ticket.Problem?.RootCause is { Length: > 0 } rootCause)
        {
            text.Add(rootCause);
        }

        return new SearchDocument
        {
            Type = Type,
            Id = ticket.Id.ToString(),
            Title = new LocalizedText(title, title),
            Text = text,
            Keywords = [ticket.Number],
            Readers = [.. readers.Distinct(StringComparer.Ordinal)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = ticket.Number,
                ["state"] = ticket.State.ToString(),
                ["stage"] = ProblemStages.Of(ticket.State, ticket.Problem?.Phase ?? ProblemPhase.Assessment).ToString(),
            },
            Url = $"/problems/{ticket.Id}",
            UpdatedAt = ticket.UpdatedAt,
        };
    }

    /// <summary>The search document of a known error: its number, title, symptoms and workaround in both languages.</summary>
    public static SearchDocument Document(KnownError error, IReadOnlyList<string> staffRoles)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(staffRoles);
        var text = new[] { error.Symptoms.En, error.Symptoms.Ar, error.Workaround.En, error.Workaround.Ar }.OfType<string>().Where(t => t.Length > 0).ToList();
        return new SearchDocument
        {
            Type = KnownErrorType,
            Id = error.Id.ToString(),
            Title = new LocalizedText($"{error.Number} {error.Title.En ?? error.Title.Ar}", $"{error.Number} {error.Title.Ar ?? error.Title.En}"),
            Text = text,
            Keywords = [error.Number],
            Readers = [.. staffRoles.Select(SearchReaders.Role).Distinct(StringComparer.Ordinal)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = error.Number,
                ["status"] = error.Status.ToString(),
            },
            Url = $"/problems/{error.ProblemId}",
            UpdatedAt = error.UpdatedAt,
        };
    }
}

/// <summary>Every problem of the tenant, for rebuilding the search index.</summary>
public sealed class ProblemSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => ProblemSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var staff = await ProblemSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        var tickets = db.Tickets.AsNoTracking().Include(t => t.Problem).Where(t => t.Type == TicketType.Problem).OrderBy(t => t.Id).AsAsyncEnumerable();
        await foreach (var ticket in tickets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return ProblemSearch.Document(ticket, staff);
        }
    }
}

/// <summary>Every known error of the tenant, for rebuilding the search index.</summary>
public sealed class KnownErrorSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => ProblemSearch.KnownErrorType;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var staff = await ProblemSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        await foreach (var error in db.KnownErrors.AsNoTracking().OrderBy(e => e.Id).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return ProblemSearch.Document(error, staff);
        }
    }
}
