using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Workspace;

/// <summary>
/// Who a canned response or a saved view is for (task B4, product owner decisions 2026-11-24):
/// everyone in the organisation, the members of one group, or only the person who made it.
/// </summary>
public enum WorkspaceScope
{
    /// <summary>Only its owner.</summary>
    Personal,

    /// <summary>Members of <c>GroupId</c>; made by the group's team leads.</summary>
    Group,

    /// <summary>Every staff member of the organisation; made by administrators.</summary>
    Tenant,
}

/// <summary>
/// A canned response (module 5): reusable reply wording in English and Arabic, with
/// <c>{{placeholders}}</c> filled in for the ticket when used. Tenant-wide ones are made by holders
/// of <c>responses.manage</c>, group ones by the group's team leads, personal ones by anyone who
/// works on tickets (decision 2026-11-24).
/// </summary>
public sealed class CannedResponse : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum length of each language of <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 10_000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Who may use it.</summary>
    public WorkspaceScope Scope { get; set; }

    /// <summary>The group, when <see cref="Scope"/> is <see cref="WorkspaceScope.Group"/>.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>The owner, when <see cref="Scope"/> is <see cref="WorkspaceScope.Personal"/>.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Short name shown in the picker, in English and Arabic (at least one).</summary>
    public required LocalizedText Title { get; set; }

    /// <summary>The wording in English and Arabic (at least one), plain text with placeholders.</summary>
    public required LocalizedText Body { get; set; }

    /// <summary>Inactive responses are kept but not offered.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Who made it.</summary>
    public Guid CreatedById { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A saved view of the agent workspace (module 5): a named set of list filters and a sort order.
/// Everyone saves their own; team leads share views with their groups and holders of
/// <c>workspace.views.share</c> with everyone (decision 2026-11-24).
/// </summary>
public sealed class SavedView : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Filters"/> (JSON).</summary>
    public const int FiltersMaxLength = 4000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Who sees it.</summary>
    public WorkspaceScope Scope { get; set; }

    /// <summary>The group, when <see cref="Scope"/> is <see cref="WorkspaceScope.Group"/>.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Who made it and may change it (holders of the sharing rights may too).</summary>
    public Guid OwnerId { get; init; }

    /// <summary>Name in English and Arabic (at least one).</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>The incident list filters and sort, as checked JSON (the list endpoint's query parameters).</summary>
    public required string Filters { get; set; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
