using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Connectors;

/// <summary>What a connector connects to (module 14, task E7).</summary>
public enum ConnectorKind
{
    /// <summary>Microsoft Entra ID (Microsoft Graph): users, departments, managers, groups; sign-in through Keycloak.</summary>
    EntraId,

    /// <summary>Active Directory or another LDAP directory; sign-in through Keycloak's LDAP federation.</summary>
    Ldap,

    /// <summary>Microsoft Dynamics 365 (Dataverse Web API).</summary>
    Dynamics365,

    /// <summary>Salesforce (REST API).</summary>
    Salesforce,

    /// <summary>HubSpot (CRM API v3).</summary>
    HubSpot,

    /// <summary>Zoho CRM (API v6).</summary>
    Zoho,

    /// <summary>Any other system with a REST API, set up on screen and called from workflow rules.</summary>
    Rest,
}

/// <summary>
/// A connector of a tenant (module 14, task E7): an identity directory or CRM that Zelvo syncs on
/// a schedule, or a REST API that workflow rules call. Settings that are not secret are JSON; the
/// credential is encrypted with the tenant's data key (<c>Zelvo.Secrets</c>) and never returned.
/// </summary>
public sealed class Connector : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Unique id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators.</summary>
    public required string Name { get; set; }

    /// <summary>What it connects to.</summary>
    public ConnectorKind Kind { get; init; }

    /// <summary>Settings as JSON (addresses, ids, mappings, REST operations).</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>The credential, encrypted (client secret, password, token, or a JSON object of several).</summary>
    [SensitiveData]
    public string? CredentialCiphertext { get; set; }

    /// <summary>When the credential was last set.</summary>
    public DateTimeOffset? CredentialSetAt { get; set; }

    /// <summary>Whether it syncs and can be called.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Minutes between syncs (directories and CRMs).</summary>
    public int SyncMinutes { get; set; } = 60;

    /// <summary>The account its notes and the people it creates are attributed to (disabled; never signs in).</summary>
    public Guid? SystemUserId { get; set; }

    /// <summary>When it syncs next.</summary>
    public DateTimeOffset? NextSyncAt { get; set; }

    /// <summary>The worker holding it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>When the last sync started.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Whether it worked.</summary>
    public bool? LastSyncSucceeded { get; set; }

    /// <summary>What the last sync did, as JSON counts.</summary>
    public string? LastSyncStats { get; set; }

    /// <summary>Where Zelvo continues reading (a delta link or the last change time), as the source gives it.</summary>
    public string? SyncCursor { get; set; }

    /// <summary>The last error.</summary>
    public string? LastError { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>What a connector link joins.</summary>
public enum ConnectorEntity
{
    /// <summary>A person (user account).</summary>
    User,

    /// <summary>A department.</summary>
    Department,

    /// <summary>A company or account of a CRM, as a customer group.</summary>
    CustomerGroup,

    /// <summary>A ticket and the CRM case or record of another system.</summary>
    Ticket,
}

/// <summary>
/// A Zelvo record and its counterpart in the connected system: who a synced person is there, which
/// CRM case a ticket is. Links stay when a record disappears from the source, so the person is
/// disabled rather than deleted.
/// </summary>
public sealed class ConnectorLink : ITenantOwned
{
    /// <summary>Maximum length of <see cref="ExternalId"/>.</summary>
    public const int ExternalIdMaxLength = 500;

    /// <summary>Sequential id.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The connector.</summary>
    public Guid ConnectorId { get; init; }

    /// <summary>What it joins.</summary>
    public ConnectorEntity Entity { get; init; }

    /// <summary>The Zelvo record.</summary>
    public Guid ZelvoId { get; init; }

    /// <summary>Its id in the connected system (object id, GUID, DN, record id).</summary>
    public required string ExternalId { get; init; }

    /// <summary>When the source last had it.</summary>
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether the sync disabled it because the source no longer has it.</summary>
    public bool Gone { get; set; }
}

/// <summary>What an outgoing connector call does.</summary>
public enum ConnectorCallKind
{
    /// <summary>Creates or updates the ticket's case in a CRM, with a note of the change.</summary>
    CrmCase,

    /// <summary>A REST connector's operation, asked for by a workflow rule.</summary>
    Rest,
}

/// <summary>Where an outgoing connector call stands.</summary>
public enum ConnectorCallStatus
{
    /// <summary>Waits to be sent (or to be tried again).</summary>
    Pending,

    /// <summary>Sent and accepted.</summary>
    Succeeded,

    /// <summary>Given up.</summary>
    Failed,
}

/// <summary>
/// The outbox of connector calls (task E7): queued with the change that asks for them (a ticket of
/// a CRM contact changed, a workflow rule's action) and sent by the worker with retries, never
/// inside the save. Kept 30 days; bodies are in the integration log, masked.
/// </summary>
public sealed class ConnectorCall : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Operation"/>.</summary>
    public const int OperationMaxLength = 60;

    /// <summary>Maximum length of <see cref="Error"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Sequential id.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The connector.</summary>
    public Guid ConnectorId { get; init; }

    /// <summary>What it does.</summary>
    public ConnectorCallKind Kind { get; init; }

    /// <summary>A REST connector's operation key, or the CRM change (<c>created</c>, <c>state</c>).</summary>
    public required string Operation { get; init; }

    /// <summary>The ticket.</summary>
    public Guid? TicketId { get; init; }

    /// <summary>The workflow rule that asked for it.</summary>
    public Guid? RuleId { get; init; }

    /// <summary>Where it stands.</summary>
    public ConnectorCallStatus Status { get; set; } = ConnectorCallStatus.Pending;

    /// <summary>Attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When it is sent (again).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>The worker holding it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>HTTP status of the last answer.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Duration of the last attempt.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Why it failed.</summary>
    public string? Error { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it finished.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>How a monitoring tool posts alerts.</summary>
public enum AlertFormat
{
    /// <summary>Prometheus Alertmanager's webhook (several alerts with fingerprints, firing or resolved).</summary>
    Alertmanager,

    /// <summary>Any JSON, read with the source's field mapping (presets for Zabbix and Datadog).</summary>
    Generic,
}

/// <summary>
/// A monitoring tool that sends alerts to Zelvo (module 14, task E7): Zabbix, Prometheus, Datadog
/// and others post to the source's webhook address with its token (only its SHA-256 is stored);
/// a new alert raises an incident, the same alert firing again adds to it, and clearing adds a note
/// and, when the source says so, resolves it (product owner decision 2026-11-25).
/// </summary>
public sealed class MonitoringSource : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Unique id; part of the webhook address.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators and as the incidents' requester.</summary>
    public required string Name { get; set; }

    /// <summary>How alerts arrive.</summary>
    public AlertFormat Format { get; set; }

    /// <summary>The field mapping and severity levels, as JSON.</summary>
    public string Mapping { get; set; } = "{}";

    /// <summary>SHA-256 of the token (hex); the token is shown once.</summary>
    [SensitiveData]
    public required string TokenHash { get; set; }

    /// <summary>Category of the incidents (then the assignment rules route them).</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Location of the incidents.</summary>
    public Guid? LocationId { get; set; }

    /// <summary>Group of the incidents (instead of the assignment rules).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Resolve the incident when its alert clears (off: a note only).</summary>
    public bool ResolveOnClear { get; set; }

    /// <summary>Whether alerts are taken.</summary>
    public bool Active { get; set; } = true;

    /// <summary>The source's own account, the requester and author of its incidents and notes (disabled; never signs in).</summary>
    public Guid SystemUserId { get; set; }

    /// <summary>When the last alert came.</summary>
    public DateTimeOffset? LastAlertAt { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Where a monitoring alert stands.</summary>
public enum AlertState
{
    /// <summary>Firing: its incident is open.</summary>
    Firing,

    /// <summary>Cleared.</summary>
    Resolved,
}

/// <summary>One alert of a source and the incident it raised. The same key firing again while it fires adds to the incident.</summary>
public sealed class MonitoringAlert : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 300;

    /// <summary>Sequential id.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The source.</summary>
    public Guid SourceId { get; init; }

    /// <summary>The alert's identity at the source (Alertmanager fingerprint, Zabbix event id, Datadog alert id).</summary>
    public required string Key { get; init; }

    /// <summary>The incident.</summary>
    public Guid TicketId { get; init; }

    /// <summary>Firing or resolved.</summary>
    public AlertState State { get; set; }

    /// <summary>Times it fired.</summary>
    public int Count { get; set; } = 1;

    /// <summary>When it first fired.</summary>
    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last fired or cleared.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
