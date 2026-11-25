using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Branding;

/// <summary>
/// A tenant's white-label branding (module 11, task F1): edited as a draft, previewed live and
/// published. One row per tenant; the values are <see cref="BrandingValue"/> as JSON. Audited.
/// </summary>
public sealed class TenantBranding : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>What the administrator is editing (<see cref="BrandingValue"/> as JSON).</summary>
    public required string Draft { get; set; }

    /// <summary>What everyone sees; null until the first publish (Zelvo's default look).</summary>
    public string? Published { get; set; }

    /// <summary>When it was last published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Who last published it.</summary>
    public Guid? PublishedById { get; set; }

    /// <summary>When the draft last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Where a custom domain stands.</summary>
public enum DomainStatus
{
    /// <summary>Waiting for the DNS record that proves the tenant controls it.</summary>
    Pending,

    /// <summary>Verified: the installation serves the tenant on it and may get a certificate for it.</summary>
    Verified,
}

/// <summary>How the certificate of a custom domain is obtained.</summary>
public enum CertificateMode
{
    /// <summary>Automatic: the reverse proxy gets one by ACME on the first visit (needs the internet or the customer's own ACME server).</summary>
    Automatic,

    /// <summary>The customer's own certificate, uploaded in Settings (offline networks).</summary>
    Uploaded,
}

/// <summary>
/// A custom domain of a tenant, for example <c>support.entity.gov.ae</c> (module 11). Verified by a
/// DNS TXT record; host names are unique across the installation. Audited (the private key only as
/// "set").
/// </summary>
public sealed class TenantDomain : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Host"/>.</summary>
    public const int HostMaxLength = 253;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Maximum length of <see cref="CertificatePem"/>.</summary>
    public const int CertificateMaxLength = 32_768;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Host name in lower case, without a trailing dot.</summary>
    public required string Host { get; init; }

    /// <summary>Where it stands.</summary>
    public DomainStatus Status { get; set; } = DomainStatus.Pending;

    /// <summary>Value of the TXT record <c>_zelvo-verify.&lt;host&gt;</c> that proves control.</summary>
    public required string VerificationToken { get; init; }

    /// <summary>When it was verified.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>When DNS was last checked.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>What the last check found missing.</summary>
    public string? LastError { get; set; }

    /// <summary>How the certificate is obtained.</summary>
    public CertificateMode CertificateMode { get; set; } = CertificateMode.Automatic;

    /// <summary>The uploaded certificate chain (PEM, public).</summary>
    public string? CertificatePem { get; set; }

    /// <summary>The uploaded certificate's private key, encrypted with the tenant's data key (A5).</summary>
    [SensitiveData]
    public string? PrivateKeyCiphertext { get; set; }

    /// <summary>When the uploaded certificate expires.</summary>
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Result of one DNS check of the sender domain.</summary>
public enum DnsCheck
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    /// <summary>The record is there and right.</summary>
    Passed,

    /// <summary>The record is missing.</summary>
    Missing,

    /// <summary>A record is there but does not say what it should.</summary>
    Wrong,
}

/// <summary>
/// The tenant's own email sender domain (module 11; B5 decision 2026-11-24: comes with branding):
/// notification emails go out as <c>&lt;local part&gt;@&lt;domain&gt;</c>, signed with DKIM by Zelvo,
/// once the guided check finds SPF and DKIM. One row per tenant. Audited (the private key only as "set").
/// </summary>
public sealed class SenderDomain : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="LocalPart"/>.</summary>
    public const int LocalPartMaxLength = 64;

    /// <summary>Maximum length of <see cref="DkimSelector"/>.</summary>
    public const int SelectorMaxLength = 63;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The domain, for example <c>entity.gov.ae</c>.</summary>
    public required string Domain { get; set; }

    /// <summary>The address before the @, for example <c>noreply</c>.</summary>
    public required string LocalPart { get; set; }

    /// <summary>DKIM selector; the key is published at <c>&lt;selector&gt;._domainkey.&lt;domain&gt;</c>.</summary>
    public required string DkimSelector { get; set; }

    /// <summary>The DKIM public key (base64 SubjectPublicKeyInfo) for the DNS record.</summary>
    public required string DkimPublicKey { get; set; }

    /// <summary>The DKIM private key (PKCS #8 PEM), encrypted with the tenant's data key.</summary>
    [SensitiveData]
    public required string DkimPrivateKeyCiphertext { get; set; }

    /// <summary>SPF check result.</summary>
    public DnsCheck Spf { get; set; }

    /// <summary>DKIM check result.</summary>
    public DnsCheck Dkim { get; set; }

    /// <summary>DMARC check result (advised, not required).</summary>
    public DnsCheck Dmarc { get; set; }

    /// <summary>SPF and DKIM passed: emails use this sender.</summary>
    public bool Verified { get; set; }

    /// <summary>When DNS was last checked.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>When it was set up.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>What a PDF export is of.</summary>
public enum ExportKind
{
    /// <summary>An incident with its conversation (staff or requester view).</summary>
    Incident,

    /// <summary>A service request with its conversation.</summary>
    ServiceRequest,
}

/// <summary>Where an export is.</summary>
public enum ExportStatus
{
    /// <summary>Waiting for the worker.</summary>
    Pending,

    /// <summary>The PDF is ready to download.</summary>
    Ready,

    /// <summary>It could not be made.</summary>
    Failed,
}

/// <summary>
/// A branded PDF export (module 11), made by the worker with headless Chromium (product owner
/// decision 2026-11-24) and kept for a day for the person who asked for it. Not audited as a
/// record: asking for it and downloading it are.
/// </summary>
public sealed class DocumentExport : ITenantOwned
{
    /// <summary>Maximum length of <see cref="FileName"/>.</summary>
    public const int FileNameMaxLength = 200;

    /// <summary>Maximum length of <see cref="Error"/>.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>What it is of.</summary>
    public ExportKind Kind { get; init; }

    /// <summary>The ticket.</summary>
    public Guid EntityId { get; init; }

    /// <summary>Language of the PDF (<c>en</c> or <c>ar</c>).</summary>
    public required string Language { get; init; }

    /// <summary>The staff view (internal notes and all fields the person may see) or the requester's.</summary>
    public bool StaffView { get; init; }

    /// <summary>Who asked for it; only they download it.</summary>
    public Guid RequestedById { get; init; }

    /// <summary>Where it is.</summary>
    public ExportStatus Status { get; set; } = ExportStatus.Pending;

    /// <summary>Attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>A worker holds it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>The PDF.</summary>
    public byte[]? Content { get; set; }

    /// <summary>File name to download it as.</summary>
    public string? FileName { get; set; }

    /// <summary>Why it failed.</summary>
    public string? Error { get; set; }

    /// <summary>When it was asked for.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was made or given up.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}
