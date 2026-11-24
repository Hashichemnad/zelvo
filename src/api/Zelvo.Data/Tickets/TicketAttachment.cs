using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Tickets;

/// <summary>
/// A file attached to a ticket, or an image of a knowledge article (D1). Its content lives behind
/// the attachment store (in PostgreSQL, <see cref="TicketAttachmentContent"/>); nobody can
/// download it until the malware scan finds it clean (product owner decision 2026-11-22).
/// Exactly one of <see cref="TicketId"/> and <see cref="ArticleId"/> is set.
/// </summary>
public sealed class TicketAttachment : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="FileName"/>.</summary>
    public const int FileNameMaxLength = 255;

    /// <summary>Maximum length of <see cref="ScanResult"/>.</summary>
    public const int ScanResultMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket, for ticket attachments.</summary>
    public Guid? TicketId { get; init; }

    /// <summary>The knowledge article, for article images.</summary>
    public Guid? ArticleId { get; init; }

    /// <summary>File name as uploaded, cleaned of paths and control characters.</summary>
    public required string FileName { get; init; }

    /// <summary>Media type, from the file type (never from the uploader).</summary>
    public required string ContentType { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>SHA-256 of the content, hexadecimal.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Malware scan status.</summary>
    public AttachmentScanStatus ScanStatus { get; set; } = AttachmentScanStatus.Pending;

    /// <summary>What the scanner found (signature name) or why scanning failed.</summary>
    public string? ScanResult { get; set; }

    /// <summary>How many times scanning was tried.</summary>
    public int ScanAttempts { get; set; }

    /// <summary>When the scan finished.</summary>
    public DateTimeOffset? ScannedAt { get; set; }

    /// <summary>Who uploaded it.</summary>
    public Guid? UploadedById { get; init; }

    /// <summary>When it was uploaded.</summary>
    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Malware scan status of an attachment.</summary>
public enum AttachmentScanStatus
{
    /// <summary>Waiting for the scanner; cannot be downloaded.</summary>
    Pending,

    /// <summary>Scanned and clean; can be downloaded.</summary>
    Clean,

    /// <summary>Malware found; the content was deleted.</summary>
    Infected,

    /// <summary>Scanning is turned off for this installation; can be downloaded.</summary>
    NotScanned,
}

/// <summary>Content of an attachment, stored by the PostgreSQL attachment store. Not audited (the attachment is).</summary>
public sealed class TicketAttachmentContent : ITenantOwned
{
    /// <summary>The attachment.</summary>
    public Guid AttachmentId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The file's bytes.</summary>
    public required byte[] Content { get; init; }
}

/// <summary>An update on a major incident's communication timeline.</summary>
public sealed class MajorIncidentUpdate : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 4000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The major incident.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The update in English and Arabic (at least one).</summary>
    public required Localization.LocalizedText Body { get; init; }

    /// <summary>Who may read it.</summary>
    public UpdateAudience Audience { get; init; }

    /// <summary>Who posted it.</summary>
    public Guid PostedById { get; init; }

    /// <summary>When it was posted.</summary>
    public DateTimeOffset PostedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Who may read a timeline update.</summary>
public enum UpdateAudience
{
    /// <summary>Staff working on the incident.</summary>
    Internal,

    /// <summary>Also the requesters of the incident and of its child incidents.</summary>
    Requesters,
}
