using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Mcp;

/// <summary>
/// An AI agent of Zelvo's built-in MCP server (module 9, task E6): an integration (API key or
/// OAuth client, module 14) that an administrator allowed to use the MCP server. The agent acts
/// with the integration's role narrowed by its scopes; only the tools listed in
/// <see cref="Tools"/> are offered to it, and tools that change data need a person's approval
/// unless they are listed in <see cref="ToolsWithoutApproval"/> (product owner decision 2026-11-24).
/// </summary>
public sealed class McpAgent : ITenantOwned, IAuditedEntity
{
    /// <summary>The integration (<c>api_clients.id</c>); one agent per integration.</summary>
    public Guid ApiClientId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Tools the administrator turned on for this agent.</summary>
    public string[] Tools { get; set; } = [];

    /// <summary>Tools that change data and that this agent may use without a person's approval.</summary>
    public string[] ToolsWithoutApproval { get; set; } = [];

    /// <summary>When it was set up.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Where an AI agent's change stands.</summary>
public enum McpActionStatus
{
    /// <summary>Waiting for a person's approval.</summary>
    Pending,

    /// <summary>A person rejected it; nothing was changed.</summary>
    Rejected,

    /// <summary>Nobody decided in time; nothing was changed.</summary>
    Expired,

    /// <summary>Carried out.</summary>
    Succeeded,

    /// <summary>Carried out, but Zelvo refused the change (for example a validation error); nothing was changed.</summary>
    Failed,
}

/// <summary>
/// A change an AI agent asked for through an MCP tool: waiting for approval, decided, or carried
/// out. Every tool call that changes data has one, with or without approval, so administrators
/// see what each agent did. Audited like any other record.
/// </summary>
public sealed class McpAction : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Tool"/>.</summary>
    public const int ToolMaxLength = 64;

    /// <summary>Maximum length of <see cref="Summary"/>.</summary>
    public const int SummaryMaxLength = 500;

    /// <summary>Maximum length of <see cref="DecisionNote"/>.</summary>
    public const int NoteMaxLength = 500;

    /// <summary>Maximum length of <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 64;

    /// <summary>Maximum length of <see cref="Error"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Maximum length of <see cref="Arguments"/> and <see cref="Result"/> (characters of JSON).</summary>
    public const int JsonMaxLength = 64 * 1024;

    /// <summary>How long a request waits for a decision.</summary>
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromHours(24);

    /// <summary>Unique id (UUID version 7), given to the agent to check on the request.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The agent (<c>api_clients.id</c>).</summary>
    public Guid ApiClientId { get; init; }

    /// <summary>The tool, for example <c>create_incident</c>.</summary>
    public required string Tool { get; init; }

    /// <summary>The permission a person needs to approve it (the one the change itself needs).</summary>
    public required string Permission { get; init; }

    /// <summary>The tool's arguments as the agent sent them (JSON). Only the length is audited.</summary>
    [AuditLengthOnly]
    public required string Arguments { get; init; }

    /// <summary>What the change does, written by Zelvo for the approver, for example "Create incident 'Printer down' for …".</summary>
    [AuditLengthOnly]
    public required string Summary { get; init; }

    /// <summary>The ticket the change is about, when there is one.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>Where it stands.</summary>
    public McpActionStatus Status { get; set; } = McpActionStatus.Pending;

    /// <summary>Whether a person had to approve it.</summary>
    public bool ApprovalRequired { get; init; }

    /// <summary>When the agent asked.</summary>
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When an undecided request expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Who approved or rejected it (<c>users.id</c>).</summary>
    public Guid? DecidedById { get; set; }

    /// <summary>When it was approved or rejected.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The approver's note, for example why it was rejected.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>When it was carried out.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>What the change gave back (JSON), as the agent gets it. Only the length is audited.</summary>
    [AuditLengthOnly]
    public string? Result { get; set; }

    /// <summary>Why it failed (a problem code such as <c>validation-failed</c>).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Explanation of the failure.</summary>
    public string? Error { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>), so two approvers cannot both carry it out.</summary>
    public uint Version { get; init; }

    /// <summary>The status as of <paramref name="now"/>: an undecided request past its expiry is expired.</summary>
    public McpActionStatus StatusAt(DateTimeOffset now) =>
        Status == McpActionStatus.Pending && ExpiresAt is { } expires && expires <= now ? McpActionStatus.Expired : Status;
}

/// <summary>Whether Zelvo's AI may use a customer's MCP server.</summary>
public enum McpServerStatus
{
    /// <summary>Its turned-on tools can be used.</summary>
    Active,

    /// <summary>Switched off by an administrator.</summary>
    Disabled,
}

/// <summary>
/// A customer's own MCP server (for example an HR or asset system) that Zelvo's AI may use as
/// tools, set up in Settings → AI → MCP servers (module 9). The credential is encrypted with
/// <c>Zelvo.Secrets</c> and never returned. Every connection goes through the outgoing-address
/// policy. Tools are discovered from the server and each one is turned on by an administrator;
/// tools marked as changing data need a person's approval before they run.
/// </summary>
public sealed class McpServerConnection : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Url"/>.</summary>
    public const int UrlMaxLength = 2000;

    /// <summary>Maximum length of <see cref="AuthHeader"/>.</summary>
    public const int HeaderMaxLength = 64;

    /// <summary>Maximum length of <see cref="LastTestError"/>.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators, for example "HR system".</summary>
    public required string Name { get; set; }

    /// <summary>The server's Streamable HTTP endpoint.</summary>
    public required string Url { get; set; }

    /// <summary>Header that carries the credential (<c>Authorization</c> sends <c>Bearer &lt;secret&gt;</c>); null for none.</summary>
    public string? AuthHeader { get; set; }

    /// <summary>The credential, encrypted (<c>zv1.…</c>).</summary>
    [SensitiveData]
    public string? SecretCiphertext { get; set; }

    /// <summary>When the credential was last given.</summary>
    public DateTimeOffset? SecretSetAt { get; set; }

    /// <summary>Active or disabled.</summary>
    public McpServerStatus Status { get; set; } = McpServerStatus.Active;

    /// <summary>The server's tools as last discovered, with the administrator's choices (JSON array).</summary>
    [AuditLengthOnly]
    public string Tools { get; set; } = "[]";

    /// <summary>When the tools were last read from the server.</summary>
    public DateTimeOffset? ToolsRefreshedAt { get; set; }

    /// <summary>When the connection was last tested.</summary>
    public DateTimeOffset? LastTestAt { get; set; }

    /// <summary>Whether the last test succeeded.</summary>
    public bool? LastTestSucceeded { get; set; }

    /// <summary>Why the last test failed.</summary>
    public string? LastTestError { get; set; }

    /// <summary>When it was set up.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}
