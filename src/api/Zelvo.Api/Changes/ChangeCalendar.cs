using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Changes;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Changes;

/// <summary>A configuration item named in a conflict.</summary>
internal sealed record ConflictItem(Guid Id, string Number, string Name);

/// <summary>
/// A conflict of a change's planned window (task H3): another open change on one of the same
/// configuration items in an overlapping window (<c>change</c>), or a blackout period of the whole
/// tenant or of an item the change affects, directly or through what depends on it (<c>blackout</c>).
/// </summary>
internal sealed record ChangeConflictResponse(
    string Kind, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<ConflictItem> Items,
    Guid? ChangeId = null, string? ChangeNumber = null, string? ChangeTitle = null, ChangeStage? ChangeStage = null,
    Guid? BlackoutId = null, LocalizedTextDto? BlackoutName = null);

/// <summary>Conflict detection of change windows (task H3).</summary>
internal static class ChangeConflicts
{
    /// <summary>Steps of "what depends on this item" followed for blackouts of an item (a business service blacks out what it runs on).</summary>
    public const int BlackoutDepth = 3;

    /// <summary>The conflicts of <paramref name="ticket"/>'s planned window.</summary>
    public static async Task<IReadOnlyList<ChangeConflictResponse>> ForAsync(ZelvoDbContext db, Ticket ticket, CancellationToken cancellationToken)
    {
        if (ticket.Change is not { PlannedStart: { } start, PlannedEnd: { } end } || ticket.State == TicketState.Closed)
        {
            return [];
        }

        var items = await db.TicketConfigurationItems.AsNoTracking().Where(l => l.TicketId == ticket.Id).Select(l => l.ConfigurationItemId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ForWindowAsync(db, ticket.Id, start, end, items, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The conflicts of a window on <paramref name="items"/>, leaving out change <paramref name="self"/>.</summary>
    public static async Task<IReadOnlyList<ChangeConflictResponse>> ForWindowAsync(
        ZelvoDbContext db, Guid? self, DateTimeOffset start, DateTimeOffset end, IReadOnlyCollection<Guid> items, CancellationToken cancellationToken)
    {
        var result = new List<ChangeConflictResponse>();
        var names = new Dictionary<Guid, ConflictItem>();
        if (items.Count > 0)
        {
            var others = await db.TicketConfigurationItems.AsNoTracking()
                .Where(l => items.Contains(l.ConfigurationItemId) && l.TicketId != self)
                .Join(db.Tickets.AsNoTracking().Include(t => t.Change).Where(t => t.Type == TicketType.Change
                        && t.State != TicketState.Closed && t.State != TicketState.Resolved
                        && t.Change!.PlannedStart < end && t.Change.PlannedEnd > start),
                    l => l.TicketId, t => t.Id, (l, t) => new { l.ConfigurationItemId, Ticket = t })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            await LoadNamesAsync(db, names, others.Select(o => o.ConfigurationItemId), cancellationToken).ConfigureAwait(false);
            foreach (var group in others.GroupBy(o => o.Ticket.Id))
            {
                var other = group.First().Ticket;
                result.Add(new("change", other.Change!.PlannedStart!.Value, other.Change.PlannedEnd!.Value,
                    [.. group.Select(o => names[o.ConfigurationItemId]).DistinctBy(i => i.Id)],
                    ChangeId: other.Id, ChangeNumber: other.Number, ChangeTitle: other.Title, ChangeStage: ChangeCore.StageOf(other)));
            }
        }

        var blackouts = await db.ChangeBlackouts.AsNoTracking().Where(b => b.StartsAt < end && b.EndsAt > start).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (blackouts.Count > 0)
        {
            HashSet<Guid>? reach = null;
            if (blackouts.Any(b => b.ConfigurationItemId is not null) && items.Count > 0)
            {
                await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
                var impact = await CmdbGraph.ImpactAsync(db, items, BlackoutDepth, cancellationToken).ConfigureAwait(false);
                reach = [.. items, .. impact.Items.Select(i => i.Id)];
            }

            await LoadNamesAsync(db, names, blackouts.Select(b => b.ConfigurationItemId).OfType<Guid>(), cancellationToken).ConfigureAwait(false);
            foreach (var blackout in blackouts.OrderBy(b => b.StartsAt))
            {
                if (blackout.ConfigurationItemId is { } item)
                {
                    if (reach is null || !reach.Contains(item))
                    {
                        continue;
                    }

                    result.Add(new("blackout", blackout.StartsAt, blackout.EndsAt, names.TryGetValue(item, out var n) ? [n] : [],
                        BlackoutId: blackout.Id, BlackoutName: LocalizedTextDto.From(blackout.Name)));
                }
                else
                {
                    result.Add(new("blackout", blackout.StartsAt, blackout.EndsAt, [], BlackoutId: blackout.Id, BlackoutName: LocalizedTextDto.From(blackout.Name)));
                }
            }
        }

        return [.. result.OrderBy(r => r.Start).ThenBy(r => r.Kind, StringComparer.Ordinal)];
    }

    private static async Task LoadNamesAsync(ZelvoDbContext db, Dictionary<Guid, ConflictItem> names, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var missing = ids.Where(id => !names.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var item in await db.ConfigurationItems.AsNoTracking().Where(i => missing.Contains(i.Id)).Select(i => new ConflictItem(i.Id, i.Number, i.Name))
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[item.Id] = item;
        }
    }
}

/// <summary>A change on the calendar.</summary>
internal sealed record CalendarChange(
    Guid Id, string Number, string Title, ChangeKind Kind, ChangeStage Stage, ChangeRiskLevel? RiskLevel, DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<ConflictItem> Items, bool Conflict);

/// <summary>A blackout period.</summary>
internal sealed record BlackoutResponse(Guid Id, LocalizedTextDto Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, ConflictItem? Item, string Etag);

/// <summary>The change calendar of a period.</summary>
internal sealed record ChangeCalendarResponse(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<CalendarChange> Changes, IReadOnlyList<BlackoutResponse> Blackouts);

/// <summary>The change calendar and blackout periods (task H3).</summary>
internal static class ChangeCalendarEndpoints
{
    /// <summary>Longest period of one calendar view.</summary>
    public const int MaxDays = 400;

    public static RouteGroupBuilder MapChangeCalendarEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/changes/calendar", CalendarAsync).WithName("GetChangeCalendar").WithTags("Changes").RequireTenant()
            .WithSummary("The change calendar: changes whose planned window overlaps from–to (at most 400 days; rejected and cancelled ones left out), each marked when it conflicts with another change on the same configuration item or a blackout period, and the blackout periods of the period. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<ChangeCalendarResponse>().ProducesValidationProblem();
        group.MapPost("/changes/conflicts", ConflictsAsync).WithName("CheckChangeConflicts").WithTags("Changes").RequireTenant()
            .WithSummary("The conflicts a window on some configuration items would have, before a change is saved or submitted. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeConflictResponse>>().ProducesValidationProblem();

        var blackouts = group.MapGroup("/change-blackouts").WithTags("Changes").RequireTenant();
        blackouts.MapGet("/", ListAsync).WithName("ListChangeBlackouts")
            .WithSummary("Blackout periods, soonest first; with current=true only those not over yet. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<BlackoutResponse>>();
        blackouts.MapPost("/", CreateAsync).WithName("CreateChangeBlackout")
            .WithSummary("Add a blackout period for the whole tenant or one configuration item (a business service also covers what it runs on). Needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<BlackoutResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        blackouts.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateChangeBlackout")
            .WithSummary("Change a blackout period. Needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<BlackoutResponse>().ProducesValidationProblem();
        blackouts.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteChangeBlackout")
            .WithSummary("Remove a blackout period. Needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<IResult> CalendarAsync(DateTimeOffset? from, DateTimeOffset? to, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var start = from ?? DateTimeOffset.UtcNow.Date;
        var end = to ?? start.AddDays(31);
        if (end <= start || (end - start).TotalDays > MaxDays)
        {
            return UserEndpoints.Validation("to", $"Give a period of at most {MaxDays} days that ends after it starts.");
        }

        var changes = await ChangeCore.Changes(db).AsNoTracking()
            .Where(t => t.Change!.PlannedStart < end && t.Change.PlannedEnd > start
                && (t.Change.Closure == null || t.Change.Closure == ChangeClosure.Completed))
            .OrderBy(t => t.Change!.PlannedStart).Take(1000).ToListAsync(cancellationToken);
        var ids = changes.Select(c => c.Id).ToList();
        var links = await db.TicketConfigurationItems.AsNoTracking().Where(l => ids.Contains(l.TicketId))
            .Join(db.ConfigurationItems.AsNoTracking(), l => l.ConfigurationItemId, i => i.Id, (l, i) => new { l.TicketId, Item = new ConflictItem(i.Id, i.Number, i.Name) })
            .ToListAsync(cancellationToken);
        var itemsOf = links.GroupBy(l => l.TicketId).ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToList());
        var blackouts = await BlackoutsAsync(db, db.ChangeBlackouts.AsNoTracking().Where(b => b.StartsAt < end && b.EndsAt > start), cancellationToken);

        var result = new List<CalendarChange>();
        foreach (var change in changes)
        {
            var items = itemsOf.GetValueOrDefault(change.Id) ?? [];
            var open = change.State is not (TicketState.Closed or TicketState.Resolved);
            var conflict = false;
            if (open)
            {
                // Another open change on a shared item in an overlapping window, or a blackout of the tenant, in the period already loaded.
                conflict = changes.Any(o => o.Id != change.Id && o.State is not (TicketState.Closed or TicketState.Resolved)
                        && o.Change!.PlannedStart < change.Change!.PlannedEnd && o.Change.PlannedEnd > change.Change.PlannedStart
                        && (itemsOf.GetValueOrDefault(o.Id) ?? []).Any(i => items.Any(x => x.Id == i.Id)))
                    || blackouts.Any(b => b.Item is null && b.StartsAt < change.Change!.PlannedEnd && b.EndsAt > change.Change.PlannedStart);
                if (!conflict && blackouts.Any(b => b.Item is not null && b.StartsAt < change.Change!.PlannedEnd && b.EndsAt > change.Change.PlannedStart))
                {
                    conflict = (await ChangeConflicts.ForAsync(db, change, cancellationToken)).Any(c => c.Kind == "blackout");
                }
            }

            result.Add(new(change.Id, change.Number, change.Title, change.Change!.Kind, ChangeCore.StageOf(change), change.Change.RiskLevel,
                change.Change.PlannedStart!.Value, change.Change.PlannedEnd!.Value, items, conflict));
        }

        return TypedResults.Ok(new ChangeCalendarResponse(start, end, result, blackouts));
    }

    internal static async Task<IResult> ConflictsAsync(ConflictCheckRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.PlannedEnd <= request.PlannedStart)
        {
            return UserEndpoints.Validation("plannedEnd", "The window ends after it starts.");
        }

        var items = (request.ConfigurationItemIds ?? []).Distinct().ToList();
        return TypedResults.Ok(await ChangeConflicts.ForWindowAsync(db, request.ChangeId, request.PlannedStart!.Value, request.PlannedEnd!.Value, items, cancellationToken));
    }

    internal static async Task<IResult> ListAsync(bool? current, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.ChangeBlackouts.AsNoTracking();
        if (current == true)
        {
            var now = DateTimeOffset.UtcNow;
            query = query.Where(b => b.EndsAt > now);
        }

        return TypedResults.Ok(await BlackoutsAsync(db, query.OrderBy(b => b.StartsAt).Take(500), cancellationToken));
    }

    internal static async Task<IResult> CreateAsync(BlackoutRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (await CheckAsync(db, request, cancellationToken) is { } problem)
        {
            return problem;
        }

        var blackout = new ChangeBlackout
        {
            Name = new LocalizedText(request.Name!.En, request.Name.Ar),
            StartsAt = request.StartsAt!.Value,
            EndsAt = request.EndsAt!.Value,
            ConfigurationItemId = request.ConfigurationItemId,
        };
        db.ChangeBlackouts.Add(blackout);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/change-blackouts/{blackout.Id}", (await BlackoutsAsync(db, db.ChangeBlackouts.AsNoTracking().Where(b => b.Id == blackout.Id), cancellationToken)).Single());
    }

    internal static async Task<IResult> UpdateAsync(Guid id, BlackoutRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var blackout = await db.ChangeBlackouts.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (blackout is null)
        {
            return FormsCommon.NotFound("blackout");
        }

        if (await CheckAsync(db, request, cancellationToken) is { } problem)
        {
            return problem;
        }

        blackout.Name = new LocalizedText(request.Name!.En, request.Name.Ar);
        blackout.StartsAt = request.StartsAt!.Value;
        blackout.EndsAt = request.EndsAt!.Value;
        blackout.ConfigurationItemId = request.ConfigurationItemId;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await BlackoutsAsync(db, db.ChangeBlackouts.AsNoTracking().Where(b => b.Id == id), cancellationToken)).Single());
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var blackout = await db.ChangeBlackouts.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (blackout is null)
        {
            return FormsCommon.NotFound("blackout");
        }

        db.ChangeBlackouts.Remove(blackout);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult?> CheckAsync(ZelvoDbContext db, BlackoutRequest request, CancellationToken cancellationToken)
    {
        if (request.Name is null || (string.IsNullOrWhiteSpace(request.Name.En) && string.IsNullOrWhiteSpace(request.Name.Ar)))
        {
            return UserEndpoints.Validation("name", "Name the blackout period in English or Arabic.");
        }

        if (request.EndsAt <= request.StartsAt)
        {
            return UserEndpoints.Validation("endsAt", "The period ends after it starts.");
        }

        if (request.ConfigurationItemId is { } item && !await db.ConfigurationItems.AnyAsync(i => i.Id == item, cancellationToken))
        {
            return UserEndpoints.Validation("configurationItemId", "The configuration item does not exist.");
        }

        return null;
    }

    private static async Task<List<BlackoutResponse>> BlackoutsAsync(ZelvoDbContext db, IQueryable<ChangeBlackout> query, CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken);
        var itemIds = rows.Select(r => r.ConfigurationItemId).OfType<Guid>().Distinct().ToList();
        var items = await db.ConfigurationItems.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new ConflictItem(i.Id, i.Number, i.Name), cancellationToken);
        return [.. rows.Select(b => new BlackoutResponse(b.Id, LocalizedTextDto.From(b.Name), b.StartsAt, b.EndsAt,
            b.ConfigurationItemId is { } i ? items.GetValueOrDefault(i) : null, $"\"{b.Version}\""))];
    }
}

/// <summary>A window to check.</summary>
public sealed class ConflictCheckRequest
{
    /// <summary>The change being planned, left out of the results.</summary>
    public Guid? ChangeId { get; init; }

    /// <summary>Start of the window.</summary>
    [Required]
    public DateTimeOffset? PlannedStart { get; init; }

    /// <summary>End of the window.</summary>
    [Required]
    public DateTimeOffset? PlannedEnd { get; init; }

    /// <summary>The items it touches.</summary>
    [MaxLength(ChangeEndpoints.MaxItems)]
    public IReadOnlyList<Guid>? ConfigurationItemIds { get; init; }
}

/// <summary>A blackout period.</summary>
public sealed class BlackoutRequest
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public BlackoutName? Name { get; init; }

    /// <summary>Start.</summary>
    [Required]
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>End.</summary>
    [Required]
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>The item it applies to; none: the whole tenant.</summary>
    public Guid? ConfigurationItemId { get; init; }
}

/// <summary>A bilingual name of up to 200 characters per language.</summary>
public sealed class BlackoutName
{
    /// <summary>English.</summary>
    [StringLength(ChangeBlackout.NameMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(ChangeBlackout.NameMaxLength)]
    public string? Ar { get; init; }
}
