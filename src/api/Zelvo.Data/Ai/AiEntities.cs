using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Ai;

/// <summary>Which API an AI provider connection speaks (module 9).</summary>
public enum AiProviderKind
{
    /// <summary>Anthropic Claude (Messages API).</summary>
    Anthropic,

    /// <summary>OpenAI (Chat Completions API).</summary>
    OpenAI,

    /// <summary>Microsoft Azure OpenAI, the API behind Microsoft Copilot (Chat Completions on the customer's Azure resource).</summary>
    AzureOpenAI,

    /// <summary>Google Gemini (generateContent API).</summary>
    Gemini,

    /// <summary>Any endpoint that speaks the OpenAI Chat Completions API, including models the customer hosts itself.</summary>
    OpenAICompatible,
}

/// <summary>Whether a provider connection can be used.</summary>
public enum AiProviderStatus
{
    /// <summary>Features assigned to it can use it.</summary>
    Active,

    /// <summary>Switched off by an administrator: no calls are made. Kept, with its usage history.</summary>
    Disabled,
}

/// <summary>
/// An AI provider connection set up in Settings → AI. A tenant can have several and choose one
/// per feature (product owner decision 2026-11-23). The API key is stored encrypted
/// (<c>Zelvo.Secrets</c>) and never returned. Providers are switched off rather than deleted, so
/// usage and the AI log keep their history when the tenant changes provider.
/// </summary>
public sealed class AiProvider : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Endpoint"/>.</summary>
    public const int EndpointMaxLength = 2000;

    /// <summary>Maximum length of <see cref="ApiVersion"/>.</summary>
    public const int ApiVersionMaxLength = 40;

    /// <summary>Maximum length of <see cref="LastTestError"/>.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators, for example "Claude (production)".</summary>
    public required string Name { get; set; }

    /// <summary>Which API it speaks. Fixed once created.</summary>
    public AiProviderKind Kind { get; init; }

    /// <summary>
    /// Base address: required for Azure OpenAI (the resource address) and OpenAI-compatible
    /// endpoints; optional for the others (their public address is used when empty).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure OpenAI only: an <c>api-version</c> for deployment addresses; empty uses Azure's v1 API.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>The API key, encrypted (<c>zv1.…</c>). Optional for OpenAI-compatible endpoints.</summary>
    [SensitiveData]
    public string? ApiKeyCiphertext { get; set; }

    /// <summary>When the API key was last given.</summary>
    public DateTimeOffset? ApiKeyChangedAt { get; set; }

    /// <summary>Active or disabled.</summary>
    public AiProviderStatus Status { get; set; } = AiProviderStatus.Active;

    /// <summary>When the connection was last tested.</summary>
    public DateTimeOffset? LastTestAt { get; set; }

    /// <summary>Whether the last test succeeded.</summary>
    public bool? LastTestSucceeded { get; set; }

    /// <summary>Why the last test failed.</summary>
    public string? LastTestError { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// Which provider and model an AI feature uses, and whether it is on. A feature without a row,
/// or switched off, makes no AI calls: AI stays off until the administrator sets it up.
/// </summary>
public sealed class AiFeatureSetting : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Feature"/>.</summary>
    public const int FeatureMaxLength = 40;

    /// <summary>Maximum length of <see cref="Model"/>.</summary>
    public const int ModelMaxLength = 200;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Feature key, for example <c>summary</c>.</summary>
    public required string Feature { get; init; }

    /// <summary>Whether the feature may call the provider.</summary>
    public bool Enabled { get; set; }

    /// <summary>The provider connection.</summary>
    public Guid? ProviderId { get; set; }

    /// <summary>Model id as the provider names it (for Azure OpenAI, the deployment name).</summary>
    public string? Model { get; set; }

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// What a model costs, entered by the administrator (product owner decision 2026-11-23: providers
/// publish no prices Zelvo could read offline). In US dollars per million tokens.
/// </summary>
public sealed class AiModelPrice : ITenantOwned, IAuditedEntity
{
    /// <summary>Highest price per million tokens that can be entered.</summary>
    public const decimal MaxPrice = 10_000m;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The provider connection.</summary>
    public Guid ProviderId { get; init; }

    /// <summary>Model id, as in <see cref="AiFeatureSetting.Model"/>.</summary>
    public required string Model { get; init; }

    /// <summary>US dollars per million input (prompt) tokens.</summary>
    public decimal InputPerMillion { get; set; }

    /// <summary>US dollars per million output tokens (including reasoning tokens where the provider counts them).</summary>
    public decimal OutputPerMillion { get; set; }

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>How an AI call ended.</summary>
public enum AiCallOutcome
{
    /// <summary>The provider answered and the answer passed the checks.</summary>
    Succeeded,

    /// <summary>The provider could not be reached, refused the call or answered with an error.</summary>
    Failed,

    /// <summary>The answer did not pass Zelvo's checks (for example, not the JSON the feature needs).</summary>
    Rejected,

    /// <summary>Not sent: the tenant's monthly spending limit was reached.</summary>
    LimitReached,
}

/// <summary>
/// One AI call, for the usage and cost dashboard, the monthly limit and the AI log ("every AI
/// action is logged"). Counts and cost are kept; the masked prompt and answer are cleared after
/// <c>Ai:LogRetention</c> (30 days) by the worker. Only masked text is ever stored.
/// </summary>
public sealed class AiCall : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Prompt"/> and <see cref="Response"/> (characters).</summary>
    public const int TextMaxLength = 32_768;

    /// <summary>Maximum length of <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 60;

    /// <summary>Maximum length of <see cref="Error"/>.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Maximum length of <see cref="EntityType"/> and <see cref="EntityId"/>.</summary>
    public const int EntityMaxLength = 100;

    /// <summary>Maximum length of <see cref="CorrelationId"/>.</summary>
    public const int CorrelationIdMaxLength = 64;

    /// <summary>Sequence number.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>When the call started.</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Feature key, or <c>test</c> for the test button.</summary>
    public required string Feature { get; init; }

    /// <summary>The provider connection, if one was chosen.</summary>
    public Guid? ProviderId { get; init; }

    /// <summary>Kind of the provider, kept in case the connection changes later.</summary>
    public AiProviderKind? ProviderKind { get; init; }

    /// <summary>Model id.</summary>
    public string? Model { get; init; }

    /// <summary>The person the call was made for, if any (worker jobs have none).</summary>
    public Guid? UserId { get; set; }

    /// <summary>What the call was about, for example <c>ticket</c>.</summary>
    public string? EntityType { get; init; }

    /// <summary>Id of what the call was about.</summary>
    public string? EntityId { get; init; }

    /// <summary>How it ended.</summary>
    public AiCallOutcome Outcome { get; set; }

    /// <summary>Stable error code, for example <c>provider-auth-failed</c>.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Short explanation of the error (personal data masked).</summary>
    public string? Error { get; set; }

    /// <summary>Input tokens as counted by the provider.</summary>
    public int InputTokens { get; set; }

    /// <summary>Output tokens as counted by the provider.</summary>
    public int OutputTokens { get; set; }

    /// <summary>Cost in US dollars from the administrator's prices; null when no price is set for the model.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>Duration in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>The input looked like an attempt to give the AI instructions (prompt injection).</summary>
    public bool InjectionSuspected { get; init; }

    /// <summary>How many personal values were masked before sending.</summary>
    public int MaskedValues { get; init; }

    /// <summary>What was sent, personal data masked; cleared after the retention period.</summary>
    public string? Prompt { get; set; }

    /// <summary>The provider's answer as received (still masked); cleared after the retention period.</summary>
    public string? Response { get; set; }

    /// <summary>Trace id, to find the call in logs and traces.</summary>
    public string? CorrelationId { get; init; }
}
