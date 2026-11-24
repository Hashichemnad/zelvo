using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Directory;

/// <summary>
/// A node of a tenant's category or location tree (task B2, product owner decision 2026-11-23:
/// up to <see cref="MaxDepth"/> levels, bilingual names, shared by all record types). Nodes can be
/// made inactive; they can be deleted only while nothing uses them.
/// </summary>
public abstract class TreeNode : ITenantOwned, IAuditedEntity
{
    /// <summary>Deepest level of a tree (1 is the top).</summary>
    public const int MaxDepth = 3;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The node above this one; <see langword="null"/> at the top.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Level in the tree: 1 at the top, at most <see cref="MaxDepth"/>.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Order among the node's siblings (0 first).</summary>
    public int Position { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Inactive nodes stay on existing tickets but cannot be chosen for new ones.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When the node was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the node was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>What a ticket is about, for example Hardware → Laptop → Battery.</summary>
public sealed class Category : TreeNode;

/// <summary>Where the requester is, for example Ajman → Main building → Floor 2.</summary>
public sealed class Location : TreeNode;

/// <summary>Something an agent can do, for example "Networks" or "SAP"; used by round-robin rules.</summary>
public sealed class Skill : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Inactive skills cannot be given to people or required by new rules.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When the skill was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the skill was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A skill of a user.</summary>
public sealed class UserSkill : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The user.</summary>
    public Guid UserId { get; init; }

    /// <summary>The skill.</summary>
    public Guid SkillId { get; init; }

    /// <summary>The skill (navigation).</summary>
    public Skill? Skill { get; init; }
}

/// <summary>
/// A group of agents that tickets are assigned to (module 1 and 10), for example "Service desk" or
/// "Network team". Members are staff; team leads of the group may manage its members.
/// </summary>
public sealed class SupportGroup : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the group does, in English and Arabic (either may be empty).</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Inactive groups receive no new tickets.</summary>
    public bool Active { get; set; } = true;

    /// <summary>The team's business hours for SLA clocks (task B3); <see langword="null"/>: the SLA policy's or the tenant default.</summary>
    public Guid? CalendarId { get; set; }

    /// <summary>Members of the group.</summary>
    public List<SupportGroupMember> Members { get; init; } = [];

    /// <summary>When the group was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the group was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A member of a group.</summary>
public sealed class SupportGroupMember : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The group.</summary>
    public Guid GroupId { get; init; }

    /// <summary>The member.</summary>
    public Guid UserId { get; init; }

    /// <summary>The member (navigation).</summary>
    public Identity.UserAccount? User { get; init; }

    /// <summary>Team lead of the group: may add and remove its members and change their availability.</summary>
    public bool IsLead { get; set; }

    /// <summary>When the person joined the group.</summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Round-robin state: when a member last got a ticket from the group. Not audited; the assignment
/// itself is. Rows are written while the group's row is locked, so concurrent routing is fair.
/// </summary>
public sealed class SupportGroupRotation : ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The group.</summary>
    public Guid GroupId { get; init; }

    /// <summary>The member.</summary>
    public Guid UserId { get; init; }

    /// <summary>When the member last got a ticket from this group by round-robin.</summary>
    public DateTimeOffset LastAssignedAt { get; set; }
}

/// <summary>How a rule picks who works on a ticket.</summary>
public enum AssignmentMethod
{
    /// <summary>The ticket goes to the group's queue; someone in the group picks it up.</summary>
    GroupQueue,

    /// <summary>The next available, qualified member of the group gets it.</summary>
    RoundRobin,
}

/// <summary>
/// An assignment rule (module 1; product owner decision 2026-11-23): optional conditions on the
/// ticket's category, location and requester language, and where matching tickets go. Rules are
/// tried in <see cref="Position"/> order; the first match wins.
/// </summary>
public sealed class AssignmentRule : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Order in which rules are tried (0 first).</summary>
    public int Position { get; set; }

    /// <summary>Inactive rules are skipped.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Matches tickets in this category or below it; <see langword="null"/>: any.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Matches tickets at this location or below it; <see langword="null"/>: any.</summary>
    public Guid? LocationId { get; set; }

    /// <summary>Matches requesters with this language (<c>en</c> or <c>ar</c>); <see langword="null"/>: any. Round-robin then picks only members who handle it.</summary>
    public string? Language { get; set; }

    /// <summary>Where matching tickets go.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Group queue or round-robin.</summary>
    public AssignmentMethod Method { get; set; }

    /// <summary>Round-robin only: pick only members with this skill.</summary>
    public Guid? SkillId { get; set; }

    /// <summary>When the rule was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the rule was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A department of the organisation (task C1, product owner decision 2026-11-24), for example
/// "Finance" or "IT". People belong to one; forms fill it in from the requester's profile, and
/// services name their owning department (task C2).
/// </summary>
public sealed class Department : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Inactive departments stay on people but cannot be chosen again.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When the department was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the department was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
