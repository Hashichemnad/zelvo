using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Tickets;

namespace Zelvo.Api.ServiceDesk;

/// <summary>
/// Skills and groups (task B2). Groups hold staff; team leads of a group may add and remove its
/// members and change their availability. Every change is audited.
/// </summary>
internal static class GroupEndpoints
{
    public static RouteGroupBuilder MapGroupEndpoints(this RouteGroupBuilder group)
    {
        var skills = group.MapGroup("/skills").WithTags("Directory").RequireTenant();
        skills.MapGet("/", ListSkillsAsync).WithName("ListSkills")
            .WithSummary("Skills, by English name, with how many people have each. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<IReadOnlyList<SkillResponse>>();
        skills.MapPost("/", CreateSkillAsync).WithName("CreateSkill")
            .WithSummary("Add a skill. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<SkillResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        skills.MapPatch("/{id:guid}", UpdateSkillAsync).WithName("UpdateSkill")
            .WithSummary("Rename a skill or make it active or inactive. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<SkillResponse>().ProducesValidationProblem();
        skills.MapDelete("/{id:guid}", DeleteSkillAsync).WithName("DeleteSkill")
            .WithSummary("Delete a skill no assignment rule needs; people who had it lose it. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces(StatusCodes.Status204NoContent);

        var groups = group.MapGroup("/groups").WithTags("Directory").RequireTenant();
        groups.MapGet("/", ListGroupsAsync).WithName("ListGroups")
            .WithSummary("Groups tickets are assigned to, by English name. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<IReadOnlyList<GroupSummaryResponse>>();
        groups.MapGet("/{id:guid}", GetGroupAsync).WithName("GetGroup")
            .WithSummary("One group with its members, their availability, languages and skills. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<GroupResponse>();
        groups.MapPost("/", CreateGroupAsync).WithName("CreateGroup")
            .WithSummary("Add a group. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<GroupResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        groups.MapPatch("/{id:guid}", UpdateGroupAsync).WithName("UpdateGroup")
            .WithSummary("Rename a group, change its description, or make it active or inactive (not while the default or an active rule sends tickets to it). Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<GroupResponse>().ProducesValidationProblem();
        groups.MapDelete("/{id:guid}", DeleteGroupAsync).WithName("DeleteGroup")
            .WithSummary("Delete a group no ticket or rule uses. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces(StatusCodes.Status204NoContent);
        groups.MapPut("/{id:guid}/members/{userId:guid}", SetMemberAsync).WithName("SetGroupMember")
            .WithSummary("Add a member to the group or change whether they lead it. Members are active staff (roles holding incidents.edit). Needs directory.manage; leads of the group may add members who are not leads. Audited.")
            .RequireAuthorization().Produces<GroupResponse>().ProducesValidationProblem();
        groups.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync).WithName("RemoveGroupMember")
            .WithSummary("Remove a member from the group. Their open incidents keep their assignee. Needs directory.manage; leads of the group may remove members who are not leads. Audited.")
            .RequireAuthorization().Produces<GroupResponse>();
        return group;
    }

    // Skills

    internal static async Task<Ok<List<SkillResponse>>> ListSkillsAsync(bool? includeInactive, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var skills = await db.Skills.AsNoTracking().Where(s => includeInactive == true || s.Active)
            .Select(s => new { Skill = s, Users = db.UserSkills.Count(u => u.SkillId == s.Id) })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(skills.OrderBy(s => s.Skill.Name.En ?? s.Skill.Name.Ar, StringComparer.OrdinalIgnoreCase)
            .Select(s => SkillResponse.From(s.Skill, s.Users)).ToList());
    }

    internal static async Task<Results<Created<SkillResponse>, ValidationProblem, ProblemHttpResult>> CreateSkillAsync(
        SkillRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, Skill.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {Skill.NameMaxLength} characters in each language.");
        }

        if ((await db.Skills.ToListAsync(cancellationToken)).Any(s => DirectoryProblems.SameName(s.Name, name)))
        {
            return DirectoryProblems.NameTaken();
        }

        var skill = new Skill { Name = name, Active = request.Active ?? true };
        db.Skills.Add(skill);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/skills/{skill.Id}", SkillResponse.From(skill, 0));
    }

    internal static async Task<Results<Ok<SkillResponse>, ValidationProblem, ProblemHttpResult>> UpdateSkillAsync(
        Guid id, UpdateSkillRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (skill is null)
        {
            return DirectoryProblems.NotFound("skill");
        }

        if (request.Name is not null)
        {
            var name = DirectoryProblems.Clean(request.Name);
            if (!DirectoryProblems.Fits(name, Skill.NameMaxLength))
            {
                return UserEndpoints.Validation("name", $"Use at most {Skill.NameMaxLength} characters in each language.");
            }

            if ((await db.Skills.Where(s => s.Id != id).ToListAsync(cancellationToken)).Any(s => DirectoryProblems.SameName(s.Name, name)))
            {
                return DirectoryProblems.NameTaken();
            }

            skill.Name = name;
        }

        skill.Active = request.Active ?? skill.Active;
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(SkillResponse.From(skill, await db.UserSkills.CountAsync(u => u.SkillId == id, cancellationToken)));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteSkillAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (skill is null)
        {
            return DirectoryProblems.NotFound("skill");
        }

        if (await db.AssignmentRules.AnyAsync(r => r.SkillId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("Assignment rules need this skill; change them first or make the skill inactive.");
        }

        db.UserSkills.RemoveRange(await db.UserSkills.Where(u => u.SkillId == id).ToListAsync(cancellationToken));
        db.Skills.Remove(skill);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // Groups

    internal static async Task<Ok<List<GroupSummaryResponse>>> ListGroupsAsync(bool? includeInactive, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var groups = await db.SupportGroups.AsNoTracking().Where(g => includeInactive == true || g.Active)
            .Select(g => new { Group = g, Members = g.Members.Count, Leads = g.Members.Count(m => m.IsLead) })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(groups.OrderBy(g => g.Group.Name.En ?? g.Group.Name.Ar, StringComparer.OrdinalIgnoreCase)
            .Select(g => GroupSummaryResponse.From(g.Group, g.Members, g.Leads)).ToList());
    }

    internal static async Task<Results<Ok<GroupResponse>, ProblemHttpResult>> GetGroupAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await LoadAsync(db, id, cancellationToken) is { } group ? TypedResults.Ok(group) : DirectoryProblems.NotFound("group");

    internal static async Task<Results<Created<GroupResponse>, ValidationProblem, ProblemHttpResult>> CreateGroupAsync(
        GroupRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        var description = new Data.Localization.LocalizedText(request.Description?.En?.Trim(), request.Description?.Ar?.Trim());
        if (!DirectoryProblems.Fits(name, SupportGroup.NameMaxLength) || !DirectoryProblems.Fits(description, SupportGroup.DescriptionMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {SupportGroup.NameMaxLength} characters in each language for the name and {SupportGroup.DescriptionMaxLength} for the description.");
        }

        if ((await db.SupportGroups.ToListAsync(cancellationToken)).Any(g => DirectoryProblems.SameName(g.Name, name)))
        {
            return DirectoryProblems.NameTaken();
        }

        var group = new SupportGroup { Name = name, Description = description, Active = request.Active ?? true, ChatLimit = request.ChatLimit ?? SupportGroup.DefaultChatLimit };
        db.SupportGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/groups/{group.Id}", (await LoadAsync(db, group.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<GroupResponse>, ValidationProblem, ProblemHttpResult>> UpdateGroupAsync(
        Guid id, UpdateGroupRequest request, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var group = await db.SupportGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return DirectoryProblems.NotFound("group");
        }

        if (request.Name is not null)
        {
            var name = DirectoryProblems.Clean(request.Name);
            if (!DirectoryProblems.Fits(name, SupportGroup.NameMaxLength))
            {
                return UserEndpoints.Validation("name", $"Use at most {SupportGroup.NameMaxLength} characters in each language.");
            }

            if ((await db.SupportGroups.Where(g => g.Id != id).ToListAsync(cancellationToken)).Any(g => DirectoryProblems.SameName(g.Name, name)))
            {
                return DirectoryProblems.NameTaken();
            }

            group.Name = name;
        }

        if (request.Description is not null)
        {
            var description = new Data.Localization.LocalizedText(request.Description.En?.Trim(), request.Description.Ar?.Trim());
            if (!DirectoryProblems.Fits(description, SupportGroup.DescriptionMaxLength))
            {
                return UserEndpoints.Validation("description", $"Use at most {SupportGroup.DescriptionMaxLength} characters in each language.");
            }

            group.Description = description;
        }

        if (request.Active == false && group.Active)
        {
            var fallback = await settings.GetAsync(AssignmentSettings.Default, cancellationToken);
            if (fallback.GroupId == id || await db.AssignmentRules.AnyAsync(r => r.Active && r.GroupId == id, cancellationToken))
            {
                return DirectoryProblems.InUse("The default or an active assignment rule sends tickets to this group; change them first.");
            }
        }

        group.Active = request.Active ?? group.Active;
        group.ChatLimit = request.ChatLimit ?? group.ChatLimit;
        group.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await LoadAsync(db, id, cancellationToken))!);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteGroupAsync(
        Guid id, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var group = await db.SupportGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return DirectoryProblems.NotFound("group");
        }

        var fallback = await settings.GetAsync(AssignmentSettings.Default, cancellationToken);
        if (fallback.GroupId == id || await db.Tickets.AnyAsync(t => t.GroupId == id, cancellationToken)
            || await db.AssignmentRules.AnyAsync(r => r.GroupId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("Tickets, assignment rules or the default use this group; make it inactive instead.");
        }

        db.SupportGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<GroupResponse>, ValidationProblem, ProblemHttpResult>> SetMemberAsync(
        Guid id, Guid userId, SetGroupMemberRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var group = await db.SupportGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null || !MayRead(user))
        {
            return DirectoryProblems.NotFound("group");
        }

        var existing = group.Members.SingleOrDefault(m => m.UserId == userId);
        var touchesLead = request.IsLead == true || existing?.IsLead == true;
        if (!user.Has(Permissions.DirectoryManage) && (touchesLead || !IsLead(group, user)))
        {
            return DirectoryProblems.Forbidden("Only administrators, and the group's leads for members who are not leads, may change members.");
        }

        if (existing is null)
        {
            if (!await IsStaffAsync(db, userId, cancellationToken))
            {
                return UserEndpoints.Validation("userId", "Only active staff (roles that may work on incidents) can be members of a group.");
            }

            group.Members.Add(new SupportGroupMember { GroupId = id, UserId = userId, IsLead = request.IsLead ?? false });
        }
        else
        {
            existing.IsLead = request.IsLead ?? existing.IsLead;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await LoadAsync(db, id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<GroupResponse>, ProblemHttpResult>> RemoveMemberAsync(
        Guid id, Guid userId, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var group = await db.SupportGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null || !MayRead(user))
        {
            return DirectoryProblems.NotFound("group");
        }

        var member = group.Members.SingleOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "member-not-found", "The person is not a member of this group.");
        }

        if (!user.Has(Permissions.DirectoryManage) && (member.IsLead || !IsLead(group, user)))
        {
            return DirectoryProblems.Forbidden("Only administrators, and the group's leads for members who are not leads, may change members.");
        }

        group.Members.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await LoadAsync(db, id, cancellationToken))!);
    }

    /// <summary>
    /// Whether the caller leads the group. Lead rights apply only while the caller's staff
    /// permissions apply (so the second factor is still needed).
    /// </summary>
    internal static bool IsLead(SupportGroup group, CurrentUser user) =>
        user.Account is { } me && user.Has(IncidentPermissions.Edit) && group.Members.Any(m => m.UserId == me.Id && m.IsLead);

    /// <summary>Callers who may know the group exists; others get 404, so ids cannot be probed.</summary>
    private static bool MayRead(CurrentUser user) => user.Has(Permissions.DirectoryRead) || user.Has(IncidentPermissions.Edit);

    /// <summary>Active users whose roles hold incidents.edit.</summary>
    internal static IQueryable<UserAccount> Staff(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == IncidentPermissions.Edit)));

    private static Task<bool> IsStaffAsync(ZelvoDbContext db, Guid userId, CancellationToken cancellationToken) =>
        Staff(db).AnyAsync(u => u.Id == userId, cancellationToken);

    internal static async Task<GroupResponse?> LoadAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var group = await db.SupportGroups.AsNoTracking()
            .Include(g => g.Members).ThenInclude(m => m.User!).ThenInclude(u => u.Skills).ThenInclude(s => s.Skill)
            .AsSplitQuery()
            .SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return null;
        }

        var rotation = await db.SupportGroupRotations.AsNoTracking().Where(r => r.GroupId == id)
            .ToDictionaryAsync(r => r.UserId, r => r.LastAssignedAt, cancellationToken);
        return GroupResponse.From(group, rotation);
    }
}

/// <summary>A skill.</summary>
internal sealed record SkillResponse(Guid Id, LocalizedTextDto Name, bool Active, int UserCount)
{
    public static SkillResponse From(Skill skill, int users) => new(skill.Id, LocalizedTextDto.From(skill.Name), skill.Active, users);
}

/// <summary>A group without its members.</summary>
internal sealed record GroupSummaryResponse(Guid Id, LocalizedTextDto Name, LocalizedTextDto Description, bool Active, int MemberCount, int LeadCount)
{
    public static GroupSummaryResponse From(SupportGroup g, int members, int leads) =>
        new(g.Id, LocalizedTextDto.From(g.Name), LocalizedTextDto.From(g.Description), g.Active, members, leads);
}

/// <summary>A member of a group.</summary>
internal sealed record GroupMemberResponse(
    Guid UserId, string? DisplayName, bool IsLead, bool AvailableForAssignment, IReadOnlyList<string> Languages,
    IReadOnlyList<NamedRef> Skills, UserStatus Status, DateTimeOffset? LastAssignedAt);

/// <summary>A group with its members.</summary>
internal sealed record GroupResponse(
    Guid Id, LocalizedTextDto Name, LocalizedTextDto Description, bool Active, int MemberCount, int LeadCount, IReadOnlyList<GroupMemberResponse> Members,
    Guid? CalendarId, int ChatLimit)
{
    public static GroupResponse From(SupportGroup g, IReadOnlyDictionary<Guid, DateTimeOffset> rotation) => new(
        g.Id, LocalizedTextDto.From(g.Name), LocalizedTextDto.From(g.Description), g.Active, g.Members.Count, g.Members.Count(m => m.IsLead),
        [.. g.Members.OrderByDescending(m => m.IsLead).ThenBy(m => m.User?.DisplayName ?? m.User?.Email, StringComparer.OrdinalIgnoreCase)
            .Select(m => new GroupMemberResponse(
                m.UserId, m.User?.DisplayName, m.IsLead, m.User?.AvailableForAssignment ?? false, m.User?.Languages ?? [],
                [.. (m.User?.Skills ?? []).Where(s => s.Skill is not null).Select(s => NamedRef.From(s.SkillId, s.Skill!.Name))],
                m.User?.Status ?? UserStatus.Disabled,
                rotation.TryGetValue(m.UserId, out var at) ? at : null))],
        g.CalendarId, g.ChatLimit);
}

/// <summary>A new skill.</summary>
public sealed class SkillRequest
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Active (default true).</summary>
    public bool? Active { get; init; }
}

/// <summary>Changes to a skill.</summary>
public sealed class UpdateSkillRequest
{
    /// <summary>New name.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Active or inactive.</summary>
    public bool? Active { get; init; }
}

/// <summary>A new group.</summary>
public sealed class GroupRequest
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What the group does (either language may be blank).</summary>
    public UpdateDescription? Description { get; init; }

    /// <summary>Live chats one member answers at a time, 1 to 20 (task E2; default 3).</summary>
    [Range(1, 20)]
    public int? ChatLimit { get; init; }

    /// <summary>Active (default true).</summary>
    public bool? Active { get; init; }
}

/// <summary>Changes to a group.</summary>
public sealed class UpdateGroupRequest
{
    /// <summary>New name.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>New description (blank languages remove it).</summary>
    public UpdateDescription? Description { get; init; }

    /// <summary>Live chats one member answers at a time, 1 to 20 (task E2; default 3).</summary>
    [Range(1, 20)]
    public int? ChatLimit { get; init; }

    /// <summary>Active or inactive.</summary>
    public bool? Active { get; init; }
}

/// <summary>A description, where both languages may be blank.</summary>
public sealed class UpdateDescription
{
    /// <summary>English.</summary>
    [StringLength(SupportGroup.DescriptionMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(SupportGroup.DescriptionMaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A member of a group.</summary>
public sealed class SetGroupMemberRequest
{
    /// <summary>Team lead of the group (only administrators may change it). Default false for new members.</summary>
    public bool? IsLead { get; init; }
}
