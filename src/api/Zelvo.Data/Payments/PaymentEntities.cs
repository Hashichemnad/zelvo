using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Payments;

/// <summary>Sandbox or live.</summary>
public enum GatewayMode
{
    /// <summary>The gateway's sandbox: no real money.</summary>
    Test,

    /// <summary>Real payments. Needs a successful test payment first.</summary>
    Live,
}

/// <summary>
/// A payment gateway a tenant connected (module 15, task E8) through Zelvo's one generic payment
/// connector: the gateway's API addresses, request and response field mapping, signature method
/// and credentials, set up by the customer's administrator from the gateway's own documentation.
/// Credentials are encrypted with <c>Zelvo.Secrets</c> and never returned.
/// </summary>
public sealed class PaymentGateway : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="MerchantId"/>.</summary>
    public const int MerchantIdMaxLength = 200;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators, for example "AjmanPay".</summary>
    public required string Name { get; set; }

    /// <summary>Sandbox or live.</summary>
    public GatewayMode Mode { get; set; } = GatewayMode.Test;

    /// <summary>Whether services may use it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The gateway used by paid services that do not name one.</summary>
    public bool IsDefault { get; set; }

    /// <summary>The merchant id the gateway gave the customer.</summary>
    public string? MerchantId { get; set; }

    /// <summary>The connector: operations, field mapping, status values, callback and signature settings (JSON).</summary>
    [AuditLengthOnly]
    public string Connector { get; set; } = "{}";

    /// <summary>The credentials (JSON object name → value), encrypted; never returned.</summary>
    [SensitiveData]
    public string? EncryptedCredentials { get; set; }

    /// <summary>Names of the credentials kept (no values), for the settings screen.</summary>
    public List<string> CredentialNames { get; set; } = [];

    /// <summary>When a sandbox test payment last succeeded; going live needs one.</summary>
    public DateTimeOffset? TestPaymentSucceededAt { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>When a paid service is paid.</summary>
public enum PaymentTiming
{
    /// <summary>When the request is submitted; it waits for payment before approval or assignment.</summary>
    OnSubmission,

    /// <summary>Once approved before assignment (on submission when the service has no such approval).</summary>
    AfterApproval,

    /// <summary>When the request is completed; it closes only once paid.</summary>
    OnCompletion,
}

/// <summary>Where a payment stands.</summary>
public enum PaymentState
{
    /// <summary>Not paid yet; the requester can pay (again).</summary>
    AwaitingPayment,

    /// <summary>Paid, confirmed with the gateway's status API.</summary>
    Paid,

    /// <summary>Not paid in time: the request was closed (on submission and after approval).</summary>
    Expired,

    /// <summary>Not needed any more (the request was closed or withdrawn before it was paid).</summary>
    Cancelled,

    /// <summary>Part of the amount was refunded.</summary>
    PartlyRefunded,

    /// <summary>The whole amount was refunded.</summary>
    Refunded,
}

/// <summary>
/// The fee of a service request (module 15, task E8), in AED: what is due, when, through which
/// gateway, and where it stands. Sandbox test payments from Settings have no ticket.
/// </summary>
public sealed class Payment : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="ReceiptNumber"/>.</summary>
    public const int ReceiptMaxLength = 30;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The request; none for a sandbox test payment.</summary>
    public Guid? TicketId { get; init; }

    /// <summary>The gateway.</summary>
    public Guid GatewayId { get; set; }

    /// <summary>Amount in AED (two decimals).</summary>
    public decimal AmountAed { get; init; }

    /// <summary>How the amount was worked out (fee and the answer-based additions), for the receipt (JSON).</summary>
    public string Breakdown { get; init; } = "[]";

    /// <summary>When it is due.</summary>
    public PaymentTiming Timing { get; init; }

    /// <summary>Where it stands.</summary>
    public PaymentState State { get; set; } = PaymentState.AwaitingPayment;

    /// <summary>A sandbox test payment from Settings → Payments.</summary>
    public bool IsTest { get; init; }

    /// <summary>The attempt that paid it.</summary>
    public Guid? PaidAttemptId { get; set; }

    /// <summary>The gateway's transaction id of the paid attempt.</summary>
    public string? TransactionId { get; set; }

    /// <summary>Receipt number, for example <c>RCT-000123</c>; never reused.</summary>
    public string? ReceiptNumber { get; set; }

    /// <summary>Position in the tenant's receipt series.</summary>
    public long? ReceiptSequence { get; set; }

    /// <summary>Amount refunded so far.</summary>
    public decimal RefundedAed { get; set; }

    /// <summary>When unpaid requests expire (on submission and after approval).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>When it was paid.</summary>
    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Where one try at paying stands.</summary>
public enum PaymentAttemptState
{
    /// <summary>Sent to the gateway; the payer was sent to its page.</summary>
    Redirected,

    /// <summary>The gateway's status API said it is paid.</summary>
    Paid,

    /// <summary>The gateway said it failed.</summary>
    Failed,

    /// <summary>The payer cancelled it.</summary>
    Cancelled,

    /// <summary>The gateway could not be reached or refused the request.</summary>
    Error,
}

/// <summary>
/// One try at paying (task E8): a unique reference sent to the gateway with the amount, the
/// gateway's transaction id and what its status API said. Each try has its own reference, so a
/// failed try can be paid again and a gateway response is processed once only.
/// </summary>
public sealed class PaymentAttempt : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Reference"/>.</summary>
    public const int ReferenceMaxLength = 40;

    /// <summary>Maximum length of <see cref="TransactionId"/> and <see cref="GatewayStatus"/>.</summary>
    public const int GatewayValueMaxLength = 200;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The payment.</summary>
    public Guid PaymentId { get; init; }

    /// <summary>The gateway at the time.</summary>
    public Guid GatewayId { get; init; }

    /// <summary>The unique reference sent to the gateway (random, unique in the installation).</summary>
    public required string Reference { get; init; }

    /// <summary>Where it stands.</summary>
    public PaymentAttemptState State { get; set; } = PaymentAttemptState.Redirected;

    /// <summary>The gateway's transaction id.</summary>
    public string? TransactionId { get; set; }

    /// <summary>The status the gateway's status API last gave.</summary>
    public string? GatewayStatus { get; set; }

    /// <summary>Who started it.</summary>
    public Guid? StartedById { get; init; }

    /// <summary>When it was started.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the status was last asked of the gateway.</summary>
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>When it ended (paid, failed, cancelled).</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>How a refund was made.</summary>
public enum RefundState
{
    /// <summary>The gateway's refund API accepted it.</summary>
    Completed,

    /// <summary>The gateway refused it; nothing was refunded.</summary>
    Failed,

    /// <summary>The gateway has no refund API: recorded as refunded outside Zelvo.</summary>
    Manual,
}

/// <summary>A refund of a payment (task E8), with its reason.</summary>
public sealed class PaymentRefund : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Reason"/>.</summary>
    public const int ReasonMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The payment.</summary>
    public Guid PaymentId { get; init; }

    /// <summary>Amount in AED.</summary>
    public decimal AmountAed { get; init; }

    /// <summary>Why.</summary>
    public required string Reason { get; init; }

    /// <summary>How it went.</summary>
    public RefundState State { get; set; }

    /// <summary>The gateway's refund id.</summary>
    public string? GatewayRefundId { get; set; }

    /// <summary>Who refunded.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The daily reconciliation of one gateway (task E8): Zelvo's payments of the day matched to the
/// gateway's transactions (its status API, and the settlement file when one is imported).
/// </summary>
public sealed class PaymentReconciliation : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The gateway.</summary>
    public Guid GatewayId { get; init; }

    /// <summary>The day (UTC).</summary>
    public DateOnly Day { get; init; }

    /// <summary>Payments of the day that match.</summary>
    public int Matched { get; set; }

    /// <summary>Payments whose status or amount differ at the gateway.</summary>
    public int Mismatched { get; set; }

    /// <summary>Transactions in the settlement file that Zelvo does not know.</summary>
    public int UnknownToZelvo { get; set; }

    /// <summary>Total of the matched payments in AED.</summary>
    public decimal TotalAed { get; set; }

    /// <summary>The lines (JSON array of reference, receipt, amounts, statuses, result).</summary>
    public string Lines { get; set; } = "[]";

    /// <summary>When a settlement file was imported.</summary>
    public DateTimeOffset? FileImportedAt { get; set; }

    /// <summary>When it was run.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last run again.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
