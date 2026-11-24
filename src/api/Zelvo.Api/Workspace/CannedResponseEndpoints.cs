using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Workspace;
using Zelvo.Notifications;

namespace Zelvo.Api.Workspace;

/// <summary>
/// Canned responses (module 5, task B4): reply wording in English and Arabic with placeholders.
/// Decision 2026-11-24: holders of <c>responses.manage</c> make tenant-wide ones, a group's team
/// leads make the group's, and everyone who works on incidents keeps personal ones. Changes are
/// audited.
/// </summary>
internal static class CannedResponseEndpoints
{
    /// <summary>Placeholders a canned response may use; filled in for the ticket when it is used.</summary>
    internal static readonly IReadOnlyList<string> Placeholders =
        ["requester.name", "ticket.number", "ticket.title", "agent.name", "tenant.name"];

    public static RouteGroupBuilder MapCannedResponseEndpoints(this RouteGroupBuilder group)
    {
        var responses = group.MapGroup("/canned-responses").WithTags("Agent workspace").RequireTenant();

        responses.MapGet("/", ListAsync)
            .WithName("ListCannedResponses")
            .WithSummary("Canned responses the caller can use: the organisation's, their groups' and their own (active only). With manage=true, those the caller can change instead, active or not. Needs incidents.edit.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<IReadOnlyList<CannedResponseResponse>>();

        responses.MapGet("/placeholders", () => TypedResults.Ok(Placeholders))
            .WithName("ListCannedResponsePlaceholders")
            .WithSummary("Placeholders canned responses may use, for example {{requester.name}}.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<IReadOnlyList<string>>();

        responses.MapPost("/", CreateAsync)
            .WithName("CreateCannedResponse")
            .RequirePerson()
            .WithSummary("Make a canned response: Personal (anyone with incidents.edit), Group (the group's team leads or responses.manage) or Tenant (responses.manage). Title and wording in English, Arabic or both; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<CannedResponseResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        responses.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateCannedResponse")
            .RequirePerson()
            .WithSummary("Change a canned response the caller may manage (the same rules as making one apply to its new scope); audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<CannedResponseResponse>()
            .ProducesValidationProblem();

        responses.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteCannedResponse")
            .RequirePerson()
            .WithSummary("Remove a canned response the caller may manage. Comments that used it keep their text; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/incidents/{id:guid}/canned-responses/{responseId:guid}/render", RenderAsync)
            .WithTags("Agent workspace")
            .RequireTenant()
            .WithName("RenderCannedResponse")
            .WithSummary("The wording of a canned response for an incident in a language, with its placeholders filled in. When the response lacks that language, the other one is returned (fallback true). Needs incidents.edit.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<RenderedResponse>();

        return group;
    }

    /// <summary>Active responses <paramref name="userId"/> may use.</summary>
    internal static IQueryable<CannedResponse> Usable(ZelvoDbContext db, Guid userId) =>
        db.CannedResponses.Where(r => r.Active && (r.Scope == WorkspaceScope.Tenant
            || (r.Scope == WorkspaceScope.Group && db.SupportGroupMembers.Any(m => m.GroupId == r.GroupId && m.UserId == userId))
            || (r.Scope == WorkspaceScope.Personal && r.OwnerId == userId)));

    internal static async Task<Ok<List<CannedResponseResponse>>> ListAsync(
        bool? manage, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account?.Id ?? Guid.Empty;
        var lead = await WorkspaceAccess.LedGroupsAsync(db, me, cancellationToken);
        var all = user.Has(Permissions.ResponsesManage);
        var query = manage == true
            ? db.CannedResponses.Where(r => (r.Scope == WorkspaceScope.Personal && r.OwnerId == me)
                || (r.Scope == WorkspaceScope.Tenant && all)
                || (r.Scope == WorkspaceScope.Group && (all || lead.Contains(r.GroupId!.Value))))
            : Usable(db, me);
        var rows = await query.AsNoTracking().OrderBy(r => r.Scope).ThenBy(r => r.Title.En ?? r.Title.Ar).Take(1000).ToListAsync(cancellationToken);
        var groups = await WorkspaceAccess.GroupNamesAsync(db, rows.Select(r => r.GroupId), cancellationToken);
        return TypedResults.Ok(rows.Select(r => CannedResponseResponse.From(r, groups, MayManage(r.Scope, r.GroupId, r.OwnerId, me, all, lead))).ToList());
    }

    internal static async Task<Results<Created<CannedResponseResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CannedResponseRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        if (await CheckAsync(request, db, user, cancellationToken) is { } problem)
        {
            return problem;
        }

        var scope = request.Scope!.Value;
        var response = new CannedResponse
        {
            Scope = scope,
            GroupId = scope == WorkspaceScope.Group ? request.GroupId : null,
            OwnerId = scope == WorkspaceScope.Personal ? me : null,
            Title = new LocalizedText(request.Title?.En, request.Title?.Ar),
            Body = new LocalizedText(request.Body?.En, request.Body?.Ar),
            Active = request.Active ?? true,
            CreatedById = me,
        };
        db.CannedResponses.Add(response);
        await db.SaveChangesAsync(cancellationToken);
        var groups = await WorkspaceAccess.GroupNamesAsync(db, [response.GroupId], cancellationToken);
        return TypedResults.Created($"/api/v1/canned-responses/{response.Id}", CannedResponseResponse.From(response, groups, true));
    }

    internal static async Task<Results<Ok<CannedResponseResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, CannedResponseRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var response = await db.CannedResponses.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (response is null || !await MayManageAsync(db, user, response, cancellationToken))
        {
            return NotFound();
        }

        if (await CheckAsync(request, db, user, cancellationToken) is { } problem)
        {
            return problem;
        }

        var scope = request.Scope!.Value;
        response.Scope = scope;
        response.GroupId = scope == WorkspaceScope.Group ? request.GroupId : null;
        // A personal response stays with its owner; one made personal becomes the caller's.
        response.OwnerId = scope == WorkspaceScope.Personal ? response.OwnerId ?? user.Account!.Id : null;
        response.Title = new LocalizedText(request.Title?.En, request.Title?.Ar);
        response.Body = new LocalizedText(request.Body?.En, request.Body?.Ar);
        response.Active = request.Active ?? response.Active;
        response.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var groups = await WorkspaceAccess.GroupNamesAsync(db, [response.GroupId], cancellationToken);
        return TypedResults.Ok(CannedResponseResponse.From(response, groups, true));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var response = await db.CannedResponses.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (response is null || !await MayManageAsync(db, user, response, cancellationToken))
        {
            return NotFound();
        }

        db.CannedResponses.Remove(response);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<RenderedResponse>, ValidationProblem, ProblemHttpResult>> RenderAsync(
        Guid id, Guid responseId, RenderResponseRequest request, ZelvoDbContext db, CurrentUser user, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.Incidents(db).AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var me = user.Account?.Id ?? Guid.Empty;
        var response = await Usable(db, me).AsNoTracking().SingleOrDefaultAsync(r => r.Id == responseId, cancellationToken);
        if (response is null)
        {
            return NotFound();
        }

        var wanted = request.Language ?? LocalizedText.English;
        var text = response.Body.Get(wanted);
        var language = wanted;
        if (text is null)
        {
            language = wanted == LocalizedText.Arabic ? LocalizedText.English : LocalizedText.Arabic;
            text = response.Body.Get(language) ?? string.Empty;
        }

        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["requester.name"] = ticket.Requester?.DisplayName ?? string.Empty,
            ["ticket.number"] = ticket.Number,
            ["ticket.title"] = ticket.Title,
            ["agent.name"] = user.Account?.DisplayName ?? string.Empty,
            ["tenant.name"] = tenantName.GetOrFallback(language) ?? string.Empty,
        };
        var rendered = TemplateSyntax.Placeholder().Replace(text, m => values.GetValueOrDefault(m.Groups[1].Value, m.Value));
        return TypedResults.Ok(new RenderedResponse(response.Id, language, language != wanted, rendered));
    }

    private static async Task<ValidationProblem?> CheckAsync(CannedResponseRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var title = new LocalizedText(request.Title?.En, request.Title?.Ar);
        var body = new LocalizedText(request.Body?.En, request.Body?.Ar);
        if (!title.HasAnyValue)
        {
            return UserEndpoints.Validation("title", "Give the response a title in English or Arabic.");
        }

        if (!body.HasAnyValue)
        {
            return UserEndpoints.Validation("body", "Write the response in English or Arabic.");
        }

        foreach (var (field, value) in new[] { ("body.en", body.En), ("body.ar", body.Ar) })
        {
            if (TemplateSyntax.Problems(value, [.. Placeholders]) is { Count: > 0 } problems)
            {
                return UserEndpoints.Validation(field, string.Join(" ", problems));
            }
        }

        var me = user.Account!.Id;
        switch (request.Scope)
        {
            case WorkspaceScope.Tenant when !user.Has(Permissions.ResponsesManage):
                return Forbidden("Only holders of responses.manage make responses for the whole organisation.");
            case WorkspaceScope.Group:
                if (request.GroupId is not { } groupId || !await db.SupportGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
                {
                    return UserEndpoints.Validation("groupId", "Choose the group.");
                }

                if (!user.Has(Permissions.ResponsesManage) && !(await WorkspaceAccess.LedGroupsAsync(db, me, cancellationToken)).Contains(groupId))
                {
                    return Forbidden("Only the group's team leads make responses for the group.");
                }

                break;
        }

        return null;
    }

    private static ValidationProblem Forbidden(string message) => UserEndpoints.Validation("scope", message);

    private static async Task<bool> MayManageAsync(ZelvoDbContext db, CurrentUser user, CannedResponse r, CancellationToken cancellationToken)
    {
        var me = user.Account?.Id ?? Guid.Empty;
        return MayManage(r.Scope, r.GroupId, r.OwnerId, me, user.Has(Permissions.ResponsesManage), await WorkspaceAccess.LedGroupsAsync(db, me, cancellationToken));
    }

    private static bool MayManage(WorkspaceScope scope, Guid? groupId, Guid? ownerId, Guid me, bool all, IReadOnlySet<Guid> lead) => scope switch
    {
        WorkspaceScope.Personal => ownerId == me,
        WorkspaceScope.Group => all || (groupId is { } g && lead.Contains(g)),
        _ => all,
    };

    private static ProblemHttpResult NotFound() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "canned-response-not-found", "The canned response does not exist.");
}

/// <summary>Who may share and change workspace items (decisions 2026-11-24).</summary>
internal static class WorkspaceAccess
{
    /// <summary>Groups <paramref name="userId"/> is a team lead of.</summary>
    public static async Task<IReadOnlySet<Guid>> LedGroupsAsync(ZelvoDbContext db, Guid userId, CancellationToken cancellationToken) =>
        (await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == userId && m.IsLead).Select(m => m.GroupId)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

    /// <summary>A group by id and name.</summary>
    public static NamedRef? GroupRef(Guid? id, Dictionary<Guid, LocalizedText> names) =>
        id is { } g ? new NamedRef(g, LocalizedTextDto.From(names.GetValueOrDefault(g) ?? new LocalizedText(null, null))) : null;

    /// <summary>Names of groups, by id.</summary>
    public static async Task<Dictionary<Guid, LocalizedText>> GroupNamesAsync(ZelvoDbContext db, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.SupportGroups.AsNoTracking().Where(g => wanted.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A canned response.</summary>
/// <param name="Id">Response id.</param>
/// <param name="Scope">Personal, Group or Tenant.</param>
/// <param name="Group">The group, for Group responses.</param>
/// <param name="Title">Name shown in the picker, in English and Arabic.</param>
/// <param name="Body">The wording in English and Arabic, with placeholders.</param>
/// <param name="Active">Offered to agents.</param>
/// <param name="CanEdit">Whether the caller may change it.</param>
/// <param name="UpdatedAt">When it last changed.</param>
internal sealed record CannedResponseResponse(
    Guid Id, WorkspaceScope Scope, NamedRef? Group, LocalizedTextDto Title, LocalizedTextDto Body, bool Active, bool CanEdit, DateTimeOffset UpdatedAt)
{
    public static CannedResponseResponse From(CannedResponse r, Dictionary<Guid, LocalizedText> groups, bool canEdit) =>
        new(r.Id, r.Scope, WorkspaceAccess.GroupRef(r.GroupId, groups),
            LocalizedTextDto.From(r.Title), LocalizedTextDto.From(r.Body), r.Active, canEdit, r.UpdatedAt);
}

/// <summary>A canned response's wording for one incident.</summary>
/// <param name="Id">Response id.</param>
/// <param name="Language">Language of <paramref name="Text"/>.</param>
/// <param name="Fallback">The response lacks the asked language, so the other is given.</param>
/// <param name="Text">The wording with placeholders filled in.</param>
internal sealed record RenderedResponse(Guid Id, string Language, bool Fallback, string Text);

/// <summary>A bilingual text of a canned response.</summary>
public sealed class CannedResponseText
{
    /// <summary>English.</summary>
    [StringLength(CannedResponse.BodyMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(CannedResponse.BodyMaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A bilingual title of a canned response.</summary>
public sealed class CannedResponseTitle
{
    /// <summary>English.</summary>
    [StringLength(CannedResponse.TitleMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(CannedResponse.TitleMaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A canned response to make or change.</summary>
public sealed class CannedResponseRequest
{
    /// <summary>Personal, Group or Tenant.</summary>
    [Required]
    public WorkspaceScope? Scope { get; init; }

    /// <summary>With Group: the group.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Name in the picker, English and/or Arabic, up to 200 characters each.</summary>
    [Required]
    public CannedResponseTitle? Title { get; init; }

    /// <summary>The wording, English and/or Arabic, up to 10,000 characters each; placeholders from the list only.</summary>
    [Required]
    public CannedResponseText? Body { get; init; }

    /// <summary>Offered to agents (default true).</summary>
    public bool? Active { get; init; }
}

/// <summary>Which language to fill a canned response in.</summary>
public sealed class RenderResponseRequest
{
    /// <summary><c>en</c> (default) or <c>ar</c>.</summary>
    [AllowedValues(null, LocalizedText.English, LocalizedText.Arabic)]
    public string? Language { get; init; }
}
