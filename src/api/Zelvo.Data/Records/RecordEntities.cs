using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Records;

/// <summary>
/// A record type an administrator made on the shared ticket core (module 20, task K2; product owner
/// decision 20 of 2026-11-26), for example facilities or HR requests. Its tickets have record type
/// <c>Custom</c> and point here; they are numbered with the type's own prefix and series, move
/// through the type's own states (each mapped onto a state category of the ticket core, so SLA
/// clocks, reports and auto-close keep working), and are routed, timed, notified, searched and
/// reported like every other ticket. Permissions are made per type: <c>records.&lt;key&gt;.read</c>,
/// <c>.edit</c> and <c>.assign</c>.
/// </summary>
public sealed class RecordType : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 40;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Key in addresses and permissions (<c>facilities</c>); never changes.</summary>
    public required string Key { get; init; }

    /// <summary>Name in English and Arabic ("Facilities request" / "طلب مرافق").</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What it is for, shown in the portal.</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Number prefix (<c>FAC-000123</c>), different from every other record type's.</summary>
    public required string NumberPrefix { get; set; }

    /// <summary>The type's states in order (JSON array: key, bilingual name, state category).</summary>
    public string States { get; set; } = "[]";

    /// <summary>Requesters raise them in the portal (otherwise only staff log them).</summary>
    public bool PortalEntry { get; set; }

    /// <summary>Priority of new records for SLA targets, 1 (highest) to 5.</summary>
    public int DefaultPriority { get; set; } = 3;

    /// <summary>New records can be raised; inactive types keep their records.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Last number given in the type's series.</summary>
    public long LastNumber { get; set; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Details of a ticket of a custom record type: its state of the type and its priority.</summary>
public sealed class CustomRecord : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="StateKey"/>.</summary>
    public const int StateKeyMaxLength = 40;

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The record type.</summary>
    public Guid RecordTypeId { get; init; }

    /// <summary>Key of the type's state the record is in; its category matches the ticket's state.</summary>
    public required string StateKey { get; set; }

    /// <summary>Priority for SLA targets, 1 (highest) to 5.</summary>
    public int Priority { get; set; } = 3;
}
