using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workspace;

namespace Zelvo.Api.Workspace;

/// <summary>
/// Saved views of the agent workspace (module 5, task B4): named filters and a sort order of the
/// incident list. Decision 2026-11-24: everyone saves their own; a group's team leads share views
/// with the group and holders of <c>workspace.views.share</c> with everyone. Audited.
/// </summary>
internal static class SavedViewEndpoints
{
    /// <summary>Most views one person can save for themselves.</summary>
    internal const int MaxPersonalViews = 50;

    public static RouteGroupBuilder MapSavedViewEndpoints(this RouteGroupBuilder group)
    {
        var views = group.MapGroup("/workspace/views").WithTags("Agent workspace").RequireTenant();

        views.MapGet("/", ListAsync)
            .WithName("ListSavedViews")
            .WithSummary("Saved views the caller sees: their own, their groups' and the organisation's. Needs incidents.read.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<IReadOnlyList<SavedViewResponse>>();

        views.MapPost("/", CreateAsync)
            .WithName("CreateSavedView")
            .RequirePerson()
            .WithSummary("Save a view: Personal, Group (the group's team leads or workspace.views.share) or Tenant (workspace.views.share). Filters are the incident list's query parameters (state, open, priority, major, assignee, group, category, location, requester, sla, sort); audited.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<SavedViewResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        views.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateSavedView")
            .RequirePerson()
            .WithSummary("Change a saved view the caller may change (its owner, or who may share at its scope); audited.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<SavedViewResponse>()
            .ProducesValidationProblem();

        views.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteSavedView")
            .RequirePerson()
            .WithSummary("Remove a saved view the caller may change; audited.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }

    internal static async Task<Ok<List<SavedViewResponse>>> ListAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account?.Id ?? Guid.Empty;
        var rows = await db.SavedViews.AsNoTracking()
            .Where(v => (v.Scope == WorkspaceScope.Personal && v.OwnerId == me) || v.Scope == WorkspaceScope.Tenant
                || (v.Scope == WorkspaceScope.Group && db.SupportGroupMembers.Any(m => m.GroupId == v.GroupId && m.UserId == me)))
            .OrderBy(v => v.Scope).ThenBy(v => v.CreatedAt).Take(500)
            .ToListAsync(cancellationToken);
        var lead = await WorkspaceAccess.LedGroupsAsync(db, me, cancellationToken);
        var groups = await WorkspaceAccess.GroupNamesAsync(db, rows.Select(v => v.GroupId), cancellationToken);
        return TypedResults.Ok(rows.Select(v => SavedViewResponse.From(v, groups, MayChange(v, me, user, lead))).ToList());
    }

    internal static async Task<Results<Created<SavedViewResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SavedViewRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var (filters, problem) = await CheckAsync(request, db, user, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (request.Scope == WorkspaceScope.Personal
            && await db.SavedViews.CountAsync(v => v.Scope == WorkspaceScope.Personal && v.OwnerId == me, cancellationToken) >= MaxPersonalViews)
        {
            return UserEndpoints.Validation("scope", $"You can keep at most {MaxPersonalViews} personal views.");
        }

        var view = new SavedView
        {
            Scope = request.Scope!.Value,
            GroupId = request.Scope == WorkspaceScope.Group ? request.GroupId : null,
            OwnerId = me,
            Name = new LocalizedText(request.Name?.En, request.Name?.Ar),
            Filters = filters!,
        };
        db.SavedViews.Add(view);
        await db.SaveChangesAsync(cancellationToken);
        var groups = await WorkspaceAccess.GroupNamesAsync(db, [view.GroupId], cancellationToken);
        return TypedResults.Created($"/api/v1/workspace/views/{view.Id}", SavedViewResponse.From(view, groups, true));
    }

    internal static async Task<Results<Ok<SavedViewResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, SavedViewRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var view = await db.SavedViews.SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (view is null || !MayChange(view, me, user, await WorkspaceAccess.LedGroupsAsync(db, me, cancellationToken)))
        {
            return NotFound();
        }

        var (filters, problem) = await CheckAsync(request, db, user, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        view.Scope = request.Scope!.Value;
        view.GroupId = view.Scope == WorkspaceScope.Group ? request.GroupId : null;
        view.Name = new LocalizedText(request.Name?.En, request.Name?.Ar);
        view.Filters = filters!;
        view.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var groups = await WorkspaceAccess.GroupNamesAsync(db, [view.GroupId], cancellationToken);
        return TypedResults.Ok(SavedViewResponse.From(view, groups, true));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var view = await db.SavedViews.SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (view is null || !MayChange(view, me, user, await WorkspaceAccess.LedGroupsAsync(db, me, cancellationToken)))
        {
            return NotFound();
        }

        db.SavedViews.Remove(view);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The owner changes their own views; shared views also whoever may share at that scope.</summary>
    private static bool MayChange(SavedView view, Guid me, CurrentUser user, IReadOnlySet<Guid> lead) => view.Scope switch
    {
        WorkspaceScope.Personal => view.OwnerId == me,
        WorkspaceScope.Group => view.OwnerId == me || user.Has(Permissions.WorkspaceViewsShare) || lead.Contains(view.GroupId!.Value),
        _ => view.OwnerId == me || user.Has(Permissions.WorkspaceViewsShare),
    };

    private static async Task<(string? Filters, ValidationProblem? Problem)> CheckAsync(
        SavedViewRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!new LocalizedText(request.Name?.En, request.Name?.Ar).HasAnyValue)
        {
            return (null, UserEndpoints.Validation("name", "Name the view in English or Arabic."));
        }

        switch (request.Scope)
        {
            case WorkspaceScope.Tenant when !user.Has(Permissions.WorkspaceViewsShare):
                return (null, UserEndpoints.Validation("scope", "Only holders of workspace.views.share share views with the whole organisation."));
            case WorkspaceScope.Group:
                if (request.GroupId is not { } groupId || !await db.SupportGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
                {
                    return (null, UserEndpoints.Validation("groupId", "Choose the group."));
                }

                if (!user.Has(Permissions.WorkspaceViewsShare) && !(await WorkspaceAccess.LedGroupsAsync(db, user.Account!.Id, cancellationToken)).Contains(groupId))
                {
                    return (null, UserEndpoints.Validation("scope", "Only the group's team leads share views with the group."));
                }

                break;
        }

        return ViewFilters.Normalize(request.Filters) is { } normalized
            ? (normalized, null)
            : (null, UserEndpoints.Validation("filters", ViewFilters.Help));
    }

    private static ProblemHttpResult NotFound() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "saved-view-not-found", "The saved view does not exist.");
}

/// <summary>Checks the filters of a saved view: the incident list's query parameters and their values.</summary>
internal static class ViewFilters
{
    public const string Help =
        "Use the incident list's filters: state, open, priority (1-9), major, assignee (me, none or a user id), group (mine, none or a group id), category, location, requester (ids), sla (atRisk or breached) and sort (due or created).";

    /// <summary>Filters as stored JSON, or <see langword="null"/> when one is unknown or has a wrong value.</summary>
    public static string? Normalize(JsonObject? filters)
    {
        var result = new JsonObject();
        foreach (var (key, node) in filters ?? [])
        {
            if (node is null)
            {
                continue;
            }

            JsonNode? value = key switch
            {
                "state" => Text(node) is { } s && Enum.TryParse<TicketState>(s, ignoreCase: false, out var state) && Enum.IsDefined(state) ? state.ToString() : null,
                "open" or "major" => node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? node.GetValue<bool>() : null,
                "priority" => node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var p) && p is >= 1 and <= 9 ? p : null,
                "assignee" => Text(node) is { } a && (a is "me" or "none" || Guid.TryParse(a, out _)) ? a : null,
                "group" => Text(node) is { } g && (g is "mine" or "none" || Guid.TryParse(g, out _)) ? g : null,
                "category" or "location" or "requester" => Text(node) is { } id && Guid.TryParse(id, out var guid) ? guid.ToString() : null,
                "sla" => Text(node) is "atRisk" or "breached" ? Text(node) : null,
                "sort" => Text(node) is "due" or "created" ? Text(node) : null,
                _ => null,
            };
            if (value is null)
            {
                return null;
            }

            result[key] = value;
        }

        var json = result.ToJsonString();
        return json.Length <= SavedView.FiltersMaxLength ? json : null;
    }

    private static string? Text(JsonNode node) => node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}

/// <summary>A saved view.</summary>
/// <param name="Id">View id.</param>
/// <param name="Scope">Personal, Group or Tenant.</param>
/// <param name="Group">The group, for Group views.</param>
/// <param name="Name">Name in English and Arabic.</param>
/// <param name="Filters">Incident list filters and sort.</param>
/// <param name="CanEdit">Whether the caller may change it.</param>
internal sealed record SavedViewResponse(Guid Id, WorkspaceScope Scope, NamedRef? Group, LocalizedTextDto Name, JsonObject Filters, bool CanEdit)
{
    public static SavedViewResponse From(SavedView v, Dictionary<Guid, LocalizedText> groups, bool canEdit) =>
        new(v.Id, v.Scope, WorkspaceAccess.GroupRef(v.GroupId, groups), LocalizedTextDto.From(v.Name),
            JsonNode.Parse(v.Filters)?.AsObject() ?? [], canEdit);
}

/// <summary>A bilingual view name.</summary>
public sealed class SavedViewName
{
    /// <summary>English.</summary>
    [StringLength(SavedView.NameMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(SavedView.NameMaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A saved view to make or change.</summary>
public sealed class SavedViewRequest
{
    /// <summary>Name in English and/or Arabic, up to 100 characters each.</summary>
    [Required]
    public SavedViewName? Name { get; init; }

    /// <summary>Personal, Group or Tenant.</summary>
    [Required]
    public WorkspaceScope? Scope { get; init; }

    /// <summary>With Group: the group.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>The incident list's filters and sort, for example <c>{"open": true, "assignee": "me", "sort": "due"}</c>.</summary>
    public JsonObject? Filters { get; init; }
}
