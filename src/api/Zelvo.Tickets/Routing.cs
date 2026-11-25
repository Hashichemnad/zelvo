using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;

namespace Zelvo.Tickets;

/// <summary>Tenant setting key of the assignment default.</summary>
public static class AssignmentSettingKeys
{
    /// <summary>Where tickets go that no rule matches (<see cref="AssignmentDefault"/>).</summary>
    public const string Default = "assignment.default";
}

/// <summary>Where tickets go that no assignment rule matches; no group leaves them without one.</summary>
/// <param name="GroupId">The group, or <see langword="null"/>.</param>
/// <param name="Method">Group queue or round-robin.</param>
public sealed record AssignmentDefault(Guid? GroupId, AssignmentMethod Method)
{
    /// <summary>No group.</summary>
    public static AssignmentDefault None { get; } = new(null, AssignmentMethod.GroupQueue);
}

/// <summary>What routing looks at.</summary>
/// <param name="CategoryId">The ticket's category.</param>
/// <param name="LocationId">The ticket's location.</param>
/// <param name="Language">The requester's language (<c>en</c> or <c>ar</c>).</param>
public sealed record RoutingRequest(Guid? CategoryId, Guid? LocationId, string? Language);

/// <summary>Where routing sends a ticket.</summary>
/// <param name="Rule">The rule that matched; <see langword="null"/> for the tenant default.</param>
/// <param name="GroupId">The group; <see langword="null"/> when no rule matched and there is no default group.</param>
/// <param name="Method">Group queue or round-robin.</param>
/// <param name="AssigneeId">The member round-robin picked; <see langword="null"/> for the group queue or when nobody qualifies.</param>
public sealed record RoutingDecision(AssignmentRule? Rule, Guid? GroupId, AssignmentMethod Method, Guid? AssigneeId);

/// <summary>A group member as round-robin sees them.</summary>
/// <param name="UserId">The member.</param>
/// <param name="LastAssignedAt">When they last got a ticket from the group by round-robin.</param>
public sealed record RotationCandidate(Guid UserId, DateTimeOffset? LastAssignedAt);

/// <summary>
/// Assignment rules and round-robin (module 1, task B2; product owner decisions 2026-11-23). Rules
/// are tried in order and the first match wins; round-robin gives the ticket to the available,
/// qualified member whose last ticket from the group came longest ago.
/// </summary>
public static class TicketRouter
{
    /// <summary>Whether <paramref name="rule"/> matches. Category and location ancestries list the node and the nodes above it.</summary>
    public static bool Matches(AssignmentRule rule, IReadOnlyCollection<Guid> categoryAncestry, IReadOnlyCollection<Guid> locationAncestry, string? language)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(categoryAncestry);
        ArgumentNullException.ThrowIfNull(locationAncestry);
        return rule.Active
            && (rule.CategoryId is not { } category || categoryAncestry.Contains(category))
            && (rule.LocationId is not { } location || locationAncestry.Contains(location))
            && (rule.Language is null || string.Equals(rule.Language, language, StringComparison.Ordinal));
    }

    /// <summary>The first matching rule, in position order, whose group is active.</summary>
    public static AssignmentRule? FirstMatch(
        IEnumerable<AssignmentRule> rules, IReadOnlySet<Guid> activeGroups,
        IReadOnlyCollection<Guid> categoryAncestry, IReadOnlyCollection<Guid> locationAncestry, string? language)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(activeGroups);
        return rules.OrderBy(r => r.Position).ThenBy(r => r.Id)
            .FirstOrDefault(r => activeGroups.Contains(r.GroupId) && Matches(r, categoryAncestry, locationAncestry, language));
    }

    /// <summary>Round-robin order: never assigned first, then the longest ago, then by id (stable).</summary>
    public static Guid? PickNext(IEnumerable<RotationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderBy(c => c.LastAssignedAt.HasValue)
            .ThenBy(c => c.LastAssignedAt)
            .ThenBy(c => c.UserId)
            .Select(c => (Guid?)c.UserId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Routes a ticket: picks the rule (or the default) and, for round-robin, the member, whose
    /// rotation is moved on. Call inside the transaction that saves the ticket: the group's row is
    /// locked until it commits, so concurrent tickets go to different members.
    /// </summary>
    public static Task<RoutingDecision> RouteAsync(
        ZelvoDbContext db, RoutingRequest request, AssignmentDefault fallback, DateTimeOffset now, CancellationToken cancellationToken) =>
        DecideAsync(db, request, fallback, now, commit: true, cancellationToken);

    /// <summary>What <see cref="RouteAsync"/> would do now, without moving the rotation.</summary>
    public static Task<RoutingDecision> PreviewAsync(
        ZelvoDbContext db, RoutingRequest request, AssignmentDefault fallback, CancellationToken cancellationToken) =>
        DecideAsync(db, request, fallback, DateTimeOffset.UtcNow, commit: false, cancellationToken);

    /// <summary>The node and the nodes above it (at most <see cref="TreeNode.MaxDepth"/>).</summary>
    public static async Task<IReadOnlyList<Guid>> AncestryAsync<TNode>(IQueryable<TNode> nodes, Guid? id, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var ancestry = new List<Guid>();
        var current = id;
        while (current is { } nodeId && ancestry.Count < TreeNode.MaxDepth)
        {
            ancestry.Add(nodeId);
            current = await nodes.Where(n => n.Id == nodeId).Select(n => n.ParentId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        return ancestry;
    }

    /// <summary>
    /// Active members of the group who can take tickets now: available for assignment, roles
    /// holding <c>incidents.edit</c>, with the skill and language when asked for.
    /// </summary>
    public static IQueryable<SupportGroupMember> Candidates(ZelvoDbContext db, Guid groupId, Guid? skillId, string? language)
    {
        ArgumentNullException.ThrowIfNull(db);
        var members = db.SupportGroupMembers.Where(m => m.GroupId == groupId
            && m.User!.Status == UserStatus.Active
            && m.User.AvailableForAssignment
            && m.User.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == IncidentPermissions.Edit)));
        if (skillId is { } skill)
        {
            members = members.Where(m => m.User!.Skills.Any(s => s.SkillId == skill));
        }

        if (language is not null)
        {
            members = members.Where(m => m.User!.Languages.Contains(language));
        }

        return members;
    }

    /// <summary>
    /// Round-robin in <paramref name="groupId"/>: the available, qualified member whose last ticket
    /// from the group came longest ago; with <paramref name="commit"/> the group's row is locked
    /// until the caller's transaction ends and the rotation moves on. <see langword="null"/> when
    /// nobody qualifies (the ticket stays in the group's queue).
    /// </summary>
    public static async Task<Guid?> RoundRobinAsync(
        ZelvoDbContext db, Guid groupId, Guid? skillId, string? language, DateTimeOffset now, bool commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var group = groupId;
        if (commit)
        {
            // Serialises round-robin per group until the caller's transaction ends.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM support_groups WHERE id = {group} FOR UPDATE", cancellationToken).ConfigureAwait(false);
        }

        var candidates = await Candidates(db, group, skillId, language)
            .Select(m => new RotationCandidate(m.UserId,
                db.SupportGroupRotations.Where(r => r.GroupId == m.GroupId && r.UserId == m.UserId).Select(r => (DateTimeOffset?)r.LastAssignedAt).FirstOrDefault()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var assignee = PickNext(candidates);
        if (commit && assignee is { } picked)
        {
            // Strictly later than every earlier pick in the group: a request that started earlier
            // but waited for the lock must not make its pick look longer ago than the others.
            var latest = candidates.Max(c => c.LastAssignedAt);
            var stamp = latest is { } last && last >= now ? last.AddTicks(10) : now;
            var rotation = db.ChangeTracker.Entries<SupportGroupRotation>().FirstOrDefault(e => e.Entity.GroupId == group && e.Entity.UserId == picked)?.Entity
                ?? await db.SupportGroupRotations.SingleOrDefaultAsync(r => r.GroupId == group && r.UserId == picked, cancellationToken).ConfigureAwait(false);
            if (rotation is null)
            {
                db.SupportGroupRotations.Add(new SupportGroupRotation { GroupId = group, UserId = picked, LastAssignedAt = stamp });
            }
            else
            {
                rotation.LastAssignedAt = stamp;
            }
        }

        return assignee;
    }

    private static async Task<RoutingDecision> DecideAsync(
        ZelvoDbContext db, RoutingRequest request, AssignmentDefault fallback, DateTimeOffset now, bool commit, CancellationToken cancellationToken)
    {
        var categories = await AncestryAsync(db.Categories, request.CategoryId, cancellationToken).ConfigureAwait(false);
        var locations = await AncestryAsync(db.Locations, request.LocationId, cancellationToken).ConfigureAwait(false);
        var rules = await db.AssignmentRules.AsNoTracking().Where(r => r.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
        var activeGroups = (await db.SupportGroups.Where(g => g.Active).Select(g => g.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var rule = FirstMatch(rules, activeGroups, categories, locations, request.Language);
        var groupId = rule?.GroupId ?? (fallback.GroupId is { } g && activeGroups.Contains(g) ? g : null);
        var method = rule?.Method ?? fallback.Method;
        if (groupId is not { } group || method != AssignmentMethod.RoundRobin)
        {
            return new RoutingDecision(rule, groupId, method, null);
        }

        var assignee = await RoundRobinAsync(db, group, rule?.SkillId, rule?.Language, now, commit, cancellationToken).ConfigureAwait(false);
        return new RoutingDecision(rule, groupId, method, assignee);
    }
}
