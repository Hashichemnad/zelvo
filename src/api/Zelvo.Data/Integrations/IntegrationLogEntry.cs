using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Integrations;

/// <summary>Direction of a logged integration call.</summary>
public enum IntegrationDirection
{
    /// <summary>An API call made by an integration (API key or OAuth client).</summary>
    Incoming,

    /// <summary>A webhook Zelvo sent.</summary>
    Outgoing,
}

/// <summary>
/// One call in or out, for the integration log (module 14, developer tools): who, what, status,
/// timing and the payload with personal data masked. Kept for a limited time
/// (<c>Integrations:LogRetention</c>), then removed by the worker.
/// </summary>
public sealed class IntegrationLogEntry : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Method"/>.</summary>
    public const int MethodMaxLength = 10;

    /// <summary>Maximum length of <see cref="Target"/>.</summary>
    public const int TargetMaxLength = 2000;

    /// <summary>Maximum length of <see cref="RequestBody"/> and <see cref="ResponseBody"/> (characters).</summary>
    public const int BodyMaxLength = 16_384;

    /// <summary>Maximum length of <see cref="Error"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Maximum length of <see cref="CorrelationId"/>.</summary>
    public const int CorrelationIdMaxLength = 64;

    /// <summary>Sequence number.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>When the call started.</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>In or out.</summary>
    public IntegrationDirection Direction { get; init; }

    /// <summary>Incoming: the integration that called.</summary>
    public Guid? ApiClientId { get; init; }

    /// <summary>Outgoing: the webhook subscription.</summary>
    public Guid? SubscriptionId { get; init; }

    /// <summary>Outgoing: the delivery.</summary>
    public long? DeliveryId { get; init; }

    /// <summary>Outgoing: the event type.</summary>
    public string? EventType { get; init; }

    /// <summary>HTTP method.</summary>
    public required string Method { get; init; }

    /// <summary>Incoming: path with query values masked. Outgoing: the receiver's URL without query string.</summary>
    public required string Target { get; init; }

    /// <summary>HTTP status, if there was an answer.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Duration in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>Request body, personal data masked, cut to <see cref="BodyMaxLength"/>.</summary>
    public string? RequestBody { get; init; }

    /// <summary>Response body, personal data masked, cut to <see cref="BodyMaxLength"/>.</summary>
    public string? ResponseBody { get; init; }

    /// <summary>Why the call failed, when it did not reach the other side.</summary>
    public string? Error { get; init; }

    /// <summary>Trace id, to find the call in logs and traces.</summary>
    public string? CorrelationId { get; init; }
}
