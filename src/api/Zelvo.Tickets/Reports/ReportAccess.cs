using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;

namespace Zelvo.Tickets.Reports;

/// <summary>
/// Who sees what in reports (product owner decision 2026-11-25): holders of
/// <see cref="ReportPermissions.All"/> (executives, tenant administrators) see every group and
/// agent; holders of <see cref="ReportPermissions.Read"/> (team leads) see the groups they lead and
/// the agents in them; other staff see only their own figures. Record types follow the person's read
/// permissions. Used by the API for the caller and by the worker for each recipient of a schedule.
/// </summary>
public static class ReportAccess
{
    /// <summary>The scope of person <paramref name="userId"/> with <paramref name="permissions"/>; null when they may see no reports.</summary>
    public static async Task<ReportScope?> ScopeAsync(ZelvoDbContext db, Guid userId, IReadOnlySet<string> permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(permissions);
        var types = ReportPermissions.TypesFor(permissions);
        if (types.Count == 0)
        {
            return null;
        }

        if (permissions.Contains(ReportPermissions.All))
        {
            return new ReportScope(types, null, null, true);
        }

        if (permissions.Contains(ReportPermissions.Read))
        {
            var led = await db.Set<SupportGroupMember>().AsNoTracking().Where(m => m.UserId == userId && m.IsLead).Select(m => m.GroupId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (led.Count > 0)
            {
                return new ReportScope(types, led.ToHashSet(), null, false);
            }
        }

        return new ReportScope(types, null, userId, false);
    }

    /// <summary>
    /// The permissions an active person holds through their roles, for the worker (no token, so a
    /// role that needs a second factor counts: the person set up the schedule with it).
    /// Empty for people who are not active.
    /// </summary>
    public static async Task<IReadOnlySet<string>> PermissionsOfAsync(ZelvoDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var active = await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false);
        if (!active)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var permissions = await db.Set<UserRole>().AsNoTracking().Where(r => r.UserId == userId)
            .SelectMany(r => r.Role!.Permissions.Select(p => p.Permission)).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return permissions.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether the person may receive scheduled reports: active and holding a reports permission.</summary>
    public static bool MayReceive(IReadOnlySet<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return (permissions.Contains(ReportPermissions.Read) || permissions.Contains(ReportPermissions.All)) && ReportPermissions.TypesFor(permissions).Count > 0;
    }
}

/// <summary>A dashboard and its filter, as stored with PDF exports and schedules.</summary>
/// <param name="Kind">The dashboard.</param>
/// <param name="Filter">What it shows.</param>
public sealed record ReportRequest(ReportKind Kind, ReportFilter Filter)
{
    /// <summary>JSON of the request.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, TicketSettingKeys.Json);

    /// <summary>The request in <paramref name="json"/>, or null.</summary>
    public static ReportRequest? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReportRequest>(json, TicketSettingKeys.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
