using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;

namespace Zelvo.Api.Privacy;

/// <summary>
/// How erased people and anonymised tickets appear where Zelvo shows older records (task F5;
/// decided 2026-11-25): the audit log is kept unchanged as the organisation's record, but screens
/// show an erased actor as "Erased person" (no name), and a ticket's history no longer shows the
/// removed texts.
/// </summary>
internal static class ErasedPeople
{
    /// <summary>Fields whose old and new values a ticket's history hides once the ticket is anonymised.</summary>
    public static IReadOnlySet<string> TicketTextFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "description", "resolutionNotes",
    };

    /// <summary>The ids among <paramref name="actorIds"/> (user ids as text) of people who were erased.</summary>
    public static async Task<HashSet<string>> AmongAsync(ZelvoDbContext db, IEnumerable<string?> actorIds, CancellationToken cancellationToken)
    {
        var ids = actorIds.Select(a => Guid.TryParse(a, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var erased = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.ErasedAt != null).Select(u => u.Id).ToListAsync(cancellationToken);
        return erased.Select(g => g.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether an audit entry's actor is one of <paramref name="erased"/>.</summary>
    public static bool IsErased(AuditEvent e, IReadOnlySet<string> erased) =>
        e.ActorType == AuditActorType.User && e.ActorId is { } id && erased.Contains(id);

    /// <summary>Hides the removed texts of an anonymised ticket's history entry.</summary>
    public static void MaskTicketTexts(JsonObject? changes)
    {
        if (changes is null)
        {
            return;
        }

        foreach (var key in changes.Select(c => c.Key).Where(TicketTextFields.Contains).ToList())
        {
            changes[key] = new JsonObject { ["old"] = null, ["new"] = null, ["removed"] = true };
        }
    }
}
