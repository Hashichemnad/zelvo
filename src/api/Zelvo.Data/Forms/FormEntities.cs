using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data.Forms;

/// <summary>Where a form or field set stands (task C1, module 2).</summary>
public enum FormState
{
    /// <summary>Being built; never published. Not used for tickets.</summary>
    Draft,

    /// <summary>A published version is in use. The draft may hold changes not yet published.</summary>
    Published,

    /// <summary>Taken out of use: no new tickets use it. Tickets filled with it keep their answers.</summary>
    Retired,
}

/// <summary>
/// A form built in Settings → Forms (module 2): fields, rules and field sets. Admins edit the
/// <see cref="DraftSchema"/>; publishing freezes it as a <see cref="FormVersion"/>, which is what
/// tickets use. A ticket keeps the version it was filled with.
/// </summary>
public sealed class Form : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 60;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Maximum size of a schema in characters of JSON.</summary>
    public const int SchemaMaxLength = 500_000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Short unique key, for example <c>laptop-problem</c>, for integrations.</summary>
    public required string Key { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the form is for, in English and Arabic (either may be empty).</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Lifecycle state.</summary>
    public FormState State { get; set; } = FormState.Draft;

    /// <summary>The fields being edited, as JSON (see <c>Zelvo.Tickets.Forms.FormSchema</c>).</summary>
    [AuditLengthOnly]
    public string DraftSchema { get; set; } = "{\"elements\":[]}";

    /// <summary>Whether the draft differs from the published version.</summary>
    public bool HasUnpublishedChanges { get; set; } = true;

    /// <summary>The version tickets use now.</summary>
    public Guid? PublishedVersionId { get; set; }

    /// <summary>Who created the form.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A published version of a form. Never changed once written.</summary>
public sealed class FormVersion : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The form.</summary>
    public Guid FormId { get; init; }

    /// <summary>1 for the first published version, then counting up.</summary>
    public int Number { get; init; }

    /// <summary>The form's elements as JSON. Field sets are referenced, not copied.</summary>
    [AuditLengthOnly]
    public required string Schema { get; init; }

    /// <summary>Who published it.</summary>
    public Guid? PublishedById { get; init; }

    /// <summary>When it was published.</summary>
    public DateTimeOffset PublishedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A reusable group of fields, for example "Requester details" (module 2). Forms refer to it; when
/// a new version is published, every form that uses it shows the new fields at once.
/// </summary>
public sealed class FieldSet : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Short unique key, for example <c>requester-details</c>.</summary>
    public required string Key { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the field set holds (either language may be empty).</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Lifecycle state.</summary>
    public FormState State { get; set; } = FormState.Draft;

    /// <summary>The fields being edited, as JSON (<c>{"fields": [...]}</c>).</summary>
    [AuditLengthOnly]
    public string DraftSchema { get; set; } = "{\"fields\":[]}";

    /// <summary>Whether the draft differs from the published version.</summary>
    public bool HasUnpublishedChanges { get; set; } = true;

    /// <summary>The version forms use now.</summary>
    public Guid? PublishedVersionId { get; set; }

    /// <summary>Who created it.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A published version of a field set. Never changed once written.</summary>
public sealed class FieldSetVersion : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The field set.</summary>
    public Guid FieldSetId { get; init; }

    /// <summary>1 for the first published version, then counting up.</summary>
    public int Number { get; init; }

    /// <summary>The fields as JSON.</summary>
    [AuditLengthOnly]
    public required string Schema { get; init; }

    /// <summary>Who published it.</summary>
    public Guid? PublishedById { get; init; }

    /// <summary>When it was published.</summary>
    public DateTimeOffset PublishedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Which form a record type uses (module 2: "each category can ask its own questions"). A binding
/// without a category is the record type's default; otherwise the binding of the ticket's category,
/// or of the nearest category above it, wins.
/// </summary>
public sealed class FormBinding : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The record type, for example incidents.</summary>
    public TicketType RecordType { get; init; }

    /// <summary>The category (and the ones below it); <see langword="null"/>: the record type's default.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>The form.</summary>
    public Guid FormId { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The answers given on a ticket's form. The fields as they were when the form was filled are kept
/// with them, so later changes to the form or its field sets never change what the answers meant.
/// Answers to fields marked as personal data are kept apart and masked in the audit log.
/// </summary>
public sealed class TicketFormResponse : ITenantOwned, IAuditedEntity
{
    /// <summary>The ticket (one response per ticket).</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The form.</summary>
    public Guid FormId { get; init; }

    /// <summary>The published version that was filled.</summary>
    public Guid FormVersionId { get; init; }

    /// <summary>The resolved fields (field sets expanded) at the time, as JSON.</summary>
    [AuditLengthOnly]
    public required string Fields { get; init; }

    /// <summary>Answers to fields that are not personal data, as a JSON object by field key.</summary>
    public string Answers { get; set; } = "{}";

    /// <summary>Answers to fields marked as personal data, as a JSON object by field key.</summary>
    [SensitiveData]
    public string PersonalAnswers { get; set; } = "{}";

    /// <summary>Who filled it in.</summary>
    public Guid? SubmittedById { get; init; }

    /// <summary>When it was filled in.</summary>
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the answers last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
