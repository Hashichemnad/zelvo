using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.ServiceDesk;

/// <summary>The assignment default as a tenant setting.</summary>
internal static class AssignmentSettings
{
    public static SettingDefinition<AssignmentDefault> Default { get; } =
        new(AssignmentSettingKeys.Default, AssignmentDefault.None, Permissions.DirectoryManage, d => Enum.IsDefined(d.Method));
}

/// <summary>
/// Assignment rules (module 1, task B2; product owner decision 2026-11-23): tried in order, the
/// first match wins, and the tenant default catches the rest. Every change is audited.
/// </summary>
internal static class AssignmentRuleEndpoints
{
    public static RouteGroupBuilder MapAssignmentRuleEndpoints(this RouteGroupBuilder group)
    {
        var rules = group.MapGroup("/assignment-rules").WithTags("Directory").RequireTenant();
        rules.MapGet("/", ListAsync).WithName("ListAssignmentRules")
            .WithSummary("Assignment rules in the order they are tried. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<IReadOnlyList<RuleResponse>>();
        rules.MapPost("/", CreateAsync).WithName("CreateAssignmentRule")
            .WithSummary("Add a rule; it is tried last. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<RuleResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        rules.MapPut("/order", ReorderAsync).WithName("OrderAssignmentRules")
            .WithSummary("Set the order of all rules (every rule exactly once). Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<IReadOnlyList<RuleResponse>>().ProducesValidationProblem();
        rules.MapGet("/default", GetDefaultAsync).WithName("GetAssignmentDefault")
            .WithSummary("Where tickets go that no rule matches. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<AssignmentDefaultDto>();
        rules.MapPut("/default", SetDefaultAsync).WithName("SetAssignmentDefault")
            .WithSummary("Change where tickets go that no rule matches (no group: they stay without one). Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<AssignmentDefaultDto>().ProducesValidationProblem();
        rules.MapPost("/test", TestAsync).WithName("TestAssignmentRules")
            .WithSummary("Which rule, group and person a ticket with this category, location and requester language would get now. Assigns nothing and does not move round-robin on. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<RoutingPreviewResponse>().ProducesValidationProblem();
        rules.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateAssignmentRule")
            .WithSummary("Replace a rule's name, conditions and target. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<RuleResponse>().ProducesValidationProblem();
        rules.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteAssignmentRule")
            .WithSummary("Delete a rule. Tickets it routed keep their group and assignee. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<Ok<List<RuleResponse>>> ListAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ResponsesAsync(db, cancellationToken));

    internal static async Task<Results<Created<RuleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        RuleInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(request, db, null, cancellationToken) is { } problem)
        {
            return problem;
        }

        var rule = new AssignmentRule { Name = DirectoryProblems.Clean(request.Name!) };
        Apply(rule, request);
        rule.Position = await db.AssignmentRules.AnyAsync(cancellationToken) ? await db.AssignmentRules.MaxAsync(r => r.Position, cancellationToken) + 1 : 0;
        db.AssignmentRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/assignment-rules/{rule.Id}", (await ResponsesAsync(db, cancellationToken)).Single(r => r.Id == rule.Id));
    }

    internal static async Task<Results<Ok<RuleResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, RuleInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rule = await db.AssignmentRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return DirectoryProblems.NotFound("rule");
        }

        if (await ValidateAsync(request, db, rule, cancellationToken) is { } problem)
        {
            return problem;
        }

        rule.Name = DirectoryProblems.Clean(request.Name!);
        Apply(rule, request);
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await ResponsesAsync(db, cancellationToken)).Single(r => r.Id == id));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rule = await db.AssignmentRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return DirectoryProblems.NotFound("rule");
        }

        db.AssignmentRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<List<RuleResponse>>, ValidationProblem>> ReorderAsync(
        OrderRulesRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rules = await db.AssignmentRules.ToListAsync(cancellationToken);
        var ids = request.Ids!;
        if (ids.Count != rules.Count || ids.Distinct().Count() != ids.Count || !rules.All(r => ids.Contains(r.Id)))
        {
            return UserEndpoints.Validation("ids", "List every rule exactly once.");
        }

        foreach (var rule in rules)
        {
            var position = ids.IndexOf(rule.Id);
            if (rule.Position != position)
            {
                rule.Position = position;
                rule.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponsesAsync(db, cancellationToken));
    }

    internal static async Task<Ok<AssignmentDefaultDto>> GetDefaultAsync(TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(AssignmentSettings.Default, cancellationToken);
        return TypedResults.Ok(new AssignmentDefaultDto { GroupId = value.GroupId, Method = value.Method });
    }

    internal static async Task<Results<Ok<AssignmentDefaultDto>, ValidationProblem>> SetDefaultAsync(
        AssignmentDefaultDto request, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        if (request.GroupId is { } groupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        var value = new AssignmentDefault(request.GroupId, request.Method ?? AssignmentMethod.GroupQueue);
        await settings.SetAsync(AssignmentSettings.Default, value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new AssignmentDefaultDto { GroupId = value.GroupId, Method = value.Method });
    }

    internal static async Task<Results<Ok<RoutingPreviewResponse>, ValidationProblem>> TestAsync(
        RoutingTestRequest request, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        if (request.Language is { } language && !LocalizedText.Languages.Contains(language))
        {
            return UserEndpoints.Validation("language", "Use en or ar.");
        }

        var fallback = await settings.GetAsync(AssignmentSettings.Default, cancellationToken);
        var decision = await TicketRouter.PreviewAsync(db, new RoutingRequest(request.CategoryId, request.LocationId, request.Language), fallback, cancellationToken);
        return TypedResults.Ok(await RoutingPreviewResponse.FromAsync(db, decision, cancellationToken));
    }

    private static void Apply(AssignmentRule rule, RuleInput request)
    {
        rule.Active = request.Active ?? true;
        rule.CategoryId = request.CategoryId;
        rule.LocationId = request.LocationId;
        rule.Language = request.Language;
        rule.GroupId = request.GroupId!.Value;
        rule.Method = request.Method!.Value;
        rule.SkillId = request.Method == AssignmentMethod.RoundRobin ? request.SkillId : null;
    }

    private static async Task<ValidationProblem?> ValidateAsync(RuleInput request, ZelvoDbContext db, AssignmentRule? existing, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, AssignmentRule.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {AssignmentRule.NameMaxLength} characters in each language.");
        }

        if (request.Language is { } language && !LocalizedText.Languages.Contains(language))
        {
            return UserEndpoints.Validation("language", "Use en or ar.");
        }

        if (request.Method is not { } method || !Enum.IsDefined(method))
        {
            return UserEndpoints.Validation("method", "Use GroupQueue or RoundRobin.");
        }

        if (request.SkillId is not null && method != AssignmentMethod.RoundRobin)
        {
            return UserEndpoints.Validation("skillId", "A required skill needs the round-robin method.");
        }

        // Keeping a reference that became inactive is fine; choosing a new one needs it active.
        if (request.GroupId is not { } groupId
            || (groupId != existing?.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
            || !await db.SupportGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        if (request.CategoryId is { } category && category != existing?.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        if (request.LocationId is { } location && location != existing?.LocationId && !await TreeEndpoints.IsSelectableAsync(db.Locations, location, cancellationToken))
        {
            return UserEndpoints.Validation("locationId", "Choose an active location.");
        }

        if (request.SkillId is { } skill && skill != existing?.SkillId && !await db.Skills.AnyAsync(s => s.Id == skill && s.Active, cancellationToken))
        {
            return UserEndpoints.Validation("skillId", "Choose an active skill.");
        }

        return null;
    }

    internal static async Task<List<RuleResponse>> ResponsesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rules = await db.AssignmentRules.AsNoTracking().OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        var categories = new TreeIndex<Category>(await db.Categories.AsNoTracking().ToListAsync(cancellationToken));
        var locations = new TreeIndex<Location>(await db.Locations.AsNoTracking().ToListAsync(cancellationToken));
        var groups = await db.SupportGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var skills = await db.Skills.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        return [.. rules.Select(r => new RuleResponse(
            r.Id, LocalizedTextDto.From(r.Name), r.Position, r.Active,
            categories.RefOf(r.CategoryId), locations.RefOf(r.LocationId), r.Language,
            NamedRef.From(r.GroupId, groups.GetValueOrDefault(r.GroupId) ?? new LocalizedText(null, null)), r.Method,
            r.SkillId is { } s ? NamedRef.From(s, skills.GetValueOrDefault(s) ?? new LocalizedText(null, null)) : null))];
    }
}

/// <summary>An assignment rule.</summary>
internal sealed record RuleResponse(
    Guid Id, LocalizedTextDto Name, int Position, bool Active, NodeRef? Category, NodeRef? Location, string? Language,
    NamedRef Group, AssignmentMethod Method, NamedRef? Skill);

/// <summary>What routing would do now.</summary>
internal sealed record RoutingPreviewResponse(NamedRef? Rule, NamedRef? Group, AssignmentMethod Method, PersonResponse? Assignee)
{
    public static async Task<RoutingPreviewResponse> FromAsync(ZelvoDbContext db, RoutingDecision decision, CancellationToken cancellationToken)
    {
        var group = decision.GroupId is { } g ? await db.SupportGroups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken) : null;
        var assignee = decision.AssigneeId is { } a ? await db.Users.AsNoTracking().Where(u => u.Id == a).Select(u => new PersonResponse(u.Id, u.DisplayName)).SingleOrDefaultAsync(cancellationToken) : null;
        return new(
            decision.Rule is { } rule ? NamedRef.From(rule.Id, rule.Name) : null,
            decision.GroupId is { } id && group is not null ? NamedRef.From(id, group) : null,
            decision.Method, assignee);
    }
}

/// <summary>Where tickets go that no rule matches.</summary>
public sealed class AssignmentDefaultDto
{
    /// <summary>The group; null for none.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>GroupQueue (default) or RoundRobin.</summary>
    public AssignmentMethod? Method { get; init; }
}

/// <summary>An assignment rule as sent by clients.</summary>
public sealed class RuleInput
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Whether the rule is tried (default true).</summary>
    public bool? Active { get; init; }

    /// <summary>Matches tickets in this category or below it.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Matches tickets at this location or below it.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Matches requesters with this language (en or ar); round-robin then picks members who handle it.</summary>
    [MaxLength(2)]
    public string? Language { get; init; }

    /// <summary>Where matching tickets go.</summary>
    [Required]
    public Guid? GroupId { get; init; }

    /// <summary>GroupQueue or RoundRobin.</summary>
    [Required]
    public AssignmentMethod? Method { get; init; }

    /// <summary>Round-robin only: pick only members with this skill.</summary>
    public Guid? SkillId { get; init; }
}

/// <summary>The order of all rules.</summary>
public sealed class OrderRulesRequest
{
    /// <summary>Every rule id exactly once, first tried first.</summary>
    [Required]
    [MaxLength(1000)]
    public List<Guid>? Ids { get; init; }
}

/// <summary>A ticket to try the rules with.</summary>
public sealed class RoutingTestRequest
{
    /// <summary>Category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Location.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Requester language (en or ar).</summary>
    [MaxLength(2)]
    public string? Language { get; init; }
}
