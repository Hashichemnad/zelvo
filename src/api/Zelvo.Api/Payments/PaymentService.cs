using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;
using Zelvo.Data.Payments;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;
using Zelvo.Secrets;
using Zelvo.Tickets;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Payments;

/// <summary>Tenant settings of Settings → Payments (task E8).</summary>
internal static class PaymentSettings
{
    /// <summary>Online payments on or off (off by default: fees are for information only).</summary>
    public static SettingDefinition<bool> Enabled { get; } = new("payments.enabled", false, Identity.Permissions.PaymentsManage);

    /// <summary>Hours an unpaid request waits before it is closed (on submission and after approval).</summary>
    public static SettingDefinition<int> UnpaidExpiryHours { get; } = new("payments.unpaidExpiryHours", 72, Identity.Permissions.PaymentsManage, h => h is >= 1 and <= 24 * 90);
}

/// <summary>A line of how a fee was worked out.</summary>
internal sealed record FeeLine(LocalizedText Name, decimal AmountAed);

/// <summary>What a gateway's status API said about an attempt.</summary>
internal enum ConfirmOutcome
{
    /// <summary>Still pending, or the gateway could not be asked; try again later.</summary>
    Pending,

    /// <summary>Paid (now or before).</summary>
    Paid,

    /// <summary>Failed or cancelled; the requester can pay again.</summary>
    NotPaid,
}

/// <summary>
/// Online payments (module 15, task E8). A paid service's fee (fixed, plus amounts its answers
/// add) becomes a <see cref="Payment"/> when it is due: on submission, after approval, or on
/// completion. While a fee due before the work is unpaid the request is held (not routed, clocks
/// paused, "Awaiting payment"). Paying sends the amount and a unique reference to the gateway,
/// whose page takes the card; Zelvo never sees card data. The result is always confirmed with the
/// gateway's status API, under a row lock, so a payment is counted once only whatever the gateway
/// sends and however often.
/// </summary>
internal sealed class PaymentService(
    ZelvoDbContext db, TenantSettingsStore settings, IServiceProvider services, PaymentGatewayClient client, PaymentOptions options,
    PendingNotifications pending, IAuditLog audit)
{
    // Only gateways need stored secrets; requests are made without them (installations without payments).
    private ISecretProtector Secrets => services.GetRequiredService<ISecretProtector>();

    private const string CredentialPurpose = "payments.gateway.credentials";

    /// <summary>The fee for <paramref name="ticket"/> from <paramref name="card"/>: the amount and the additions whose conditions hold.</summary>
    public async Task<List<FeeLine>> FeeAsync(Ticket ticket, ServiceCard card, CancellationToken cancellationToken)
    {
        var lines = new List<FeeLine>();
        if (!card.Fee.Paid || card.Fee.AmountAed is not { } amount)
        {
            return lines;
        }

        lines.Add(new FeeLine(card.Name, amount));
        if (card.Fee.Rules.Count > 0)
        {
            var facts = await RuleFacts.LoadAsync(db, ticket, cancellationToken);
            lines.AddRange(card.Fee.Rules.Where(r => RuleDocuments.Holds(r.Conditions, Data.Workflows.ConditionMatch.All, facts.Subject)).Select(r => new FeeLine(r.Name, r.AmountAed)));
        }

        return lines;
    }

    /// <summary>
    /// Holds <paramref name="ticket"/> for its fee when one is due at <paramref name="timing"/> and
    /// not paid yet (making the payment the first time). Returns whether it is held.
    /// </summary>
    public async Task<bool> HoldIfDueAsync(Ticket ticket, ServiceCard card, PaymentTiming timing, CancellationToken cancellationToken)
    {
        if (!card.Fee.Paid || card.Fee.Timing != timing && !(timing == PaymentTiming.OnSubmission && card.Fee.Timing == PaymentTiming.AfterApproval && card.Approvals.BeforeAssignment.Count == 0)
            || !await settings.GetAsync(PaymentSettings.Enabled, cancellationToken))
        {
            return false;
        }

        var existing = db.ChangeTracker.Entries<Payment>().Select(e => e.Entity).FirstOrDefault(p => p.TicketId == ticket.Id)
            ?? await db.Payments.FirstOrDefaultAsync(p => p.TicketId == ticket.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.State != PaymentState.AwaitingPayment)
            {
                return false;
            }

            await HoldAsync(ticket, true, cancellationToken);
            return true;
        }

        var payment = await NewPaymentAsync(ticket, card, card.Fee.Timing, cancellationToken);
        if (payment is null)
        {
            return false;
        }

        await HoldAsync(ticket, true, cancellationToken);
        return true;
    }

    /// <summary>A completed request whose fee is due on completion gets its payment (it closes only once paid). Returns the payment made, if any.</summary>
    public async Task<Payment?> DueOnCompletionAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        if (ticket.Type != TicketType.ServiceRequest || ticket.State != TicketState.Resolved || await db.Payments.AnyAsync(p => p.TicketId == ticket.Id, cancellationToken)
            || !await settings.GetAsync(PaymentSettings.Enabled, cancellationToken))
        {
            return null;
        }

        var card = await ServiceRequestEndpoints.CardOfAsync(db, ticket.Id, cancellationToken);
        return card.Fee.Paid && card.Fee.Timing == PaymentTiming.OnCompletion ? await NewPaymentAsync(ticket, card, PaymentTiming.OnCompletion, cancellationToken) : null;
    }

    /// <summary>Whether <paramref name="ticketId"/> has a fee still to pay.</summary>
    public Task<bool> OutstandingAsync(Guid ticketId, CancellationToken cancellationToken) =>
        db.Payments.AnyAsync(p => p.TicketId == ticketId && p.State == PaymentState.AwaitingPayment, cancellationToken);

    private async Task<Payment?> NewPaymentAsync(Ticket ticket, ServiceCard card, PaymentTiming timing, CancellationToken cancellationToken)
    {
        var gateway = await GatewayForAsync(card.Fee.GatewayId, cancellationToken);
        if (gateway is null)
        {
            // Payments are on but no gateway can take them: the fee stays for information only.
            audit.Add(AuditCategory.Change, "payments.no-gateway", "ticket", ticket.Id.ToString());
            return null;
        }

        var lines = await FeeAsync(ticket, card, cancellationToken);
        var total = lines.Sum(l => l.AmountAed);
        var hours = await settings.GetAsync(PaymentSettings.UnpaidExpiryHours, cancellationToken);
        var payment = new Payment
        {
            TicketId = ticket.Id,
            GatewayId = gateway.Id,
            AmountAed = total,
            Breakdown = JsonSerializer.Serialize(lines, JsonSerializerOptions.Web),
            Timing = timing,
            ExpiresAt = timing == PaymentTiming.OnCompletion ? null : DateTimeOffset.UtcNow.AddHours(hours),
        };
        db.Payments.Add(payment);
        audit.Add(AuditCategory.Change, "payments.requested", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["paymentId"] = payment.Id.ToString(),
            ["amountAed"] = total,
            ["timing"] = timing.ToString(),
        });
        Notify(NotificationEvents.PaymentRequested, ticket, payment, card.Name);
        return payment;
    }

    private async Task<PaymentGateway?> GatewayForAsync(Guid? gatewayId, CancellationToken cancellationToken) =>
        gatewayId is { } id
            ? await db.PaymentGateways.SingleOrDefaultAsync(g => g.Id == id && g.Enabled, cancellationToken)
            : await db.PaymentGateways.Where(g => g.Enabled).OrderByDescending(g => g.IsDefault).ThenBy(g => g.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    private async Task HoldAsync(Ticket ticket, bool held, CancellationToken cancellationToken)
    {
        ticket.Held = held;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        var details = db.ChangeTracker.Entries<ServiceRequestDetails>().FirstOrDefault(e => e.Entity.TicketId == ticket.Id)?.Entity
            ?? await db.ServiceRequests.SingleAsync(r => r.TicketId == ticket.Id, cancellationToken);
        details.Hold = held ? ServiceRequestHold.Payment : ServiceRequestHold.None;
    }

    /// <summary>
    /// Starts a try: a new unique reference, sent to the gateway with the amount. Returns the
    /// gateway page to send the payer to, or the error code.
    /// </summary>
    public async Task<(PaymentAttempt Attempt, string? RedirectUrl, string? Error)> StartAttemptAsync(
        Payment payment, Guid? payerId, string language, string description, CancellationToken cancellationToken)
    {
        var gateway = await db.PaymentGateways.SingleAsync(g => g.Id == payment.GatewayId, cancellationToken);
        var connector = GatewayConnector.Read(gateway.Connector);
        var attempt = new PaymentAttempt { PaymentId = payment.Id, GatewayId = gateway.Id, Reference = NewReference(), StartedById = payerId };
        db.PaymentAttempts.Add(attempt);
        var answer = await client.SendAsync(connector.Create, Values(gateway, payment, attempt, language, description), await CredentialsAsync(gateway, cancellationToken), cancellationToken);
        attempt.TransactionId = Limit(answer.Values.GetValueOrDefault("transactionId"));
        var redirect = answer.Values.GetValueOrDefault("redirectUrl");
        string? error = answer.Error;
        if (error is null && (redirect is null || client.Policy.CheckUrl(redirect, out _) is not null && !IsHttpsUrl(redirect)))
        {
            error = "redirect-invalid";
        }

        if (error is not null)
        {
            attempt.State = PaymentAttemptState.Error;
            attempt.GatewayStatus = Limit(error);
            attempt.EndedAt = DateTimeOffset.UtcNow;
        }

        audit.Add(AuditCategory.Change, "payments.attempt.started", "payment", payment.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reference"] = attempt.Reference,
            ["gatewayId"] = gateway.Id.ToString(),
            ["error"] = error,
        });
        return (attempt, error is null ? redirect : null, error);
    }

    // The payer's browser goes to the gateway's page, which may be on another host than its API.
    private static bool IsHttpsUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Asks the gateway's status API about <paramref name="attempt"/> and applies the answer once:
    /// the payment's row is locked, and a paid payment is never paid again. On payment the request
    /// moves on (released, or its approvals start); the receipt is numbered and sent.
    /// </summary>
    public async Task<ConfirmOutcome> ConfirmAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Payments are confirmed inside a transaction.");
        }

        var payment = await db.Payments.FromSql($"SELECT *, xmin FROM payments WHERE id = {attempt.PaymentId} FOR UPDATE").SingleAsync(cancellationToken);
        await db.Entry(attempt).ReloadAsync(cancellationToken);
        if (attempt.State == PaymentAttemptState.Paid)
        {
            return ConfirmOutcome.Paid;
        }

        if (attempt.State is PaymentAttemptState.Failed or PaymentAttemptState.Cancelled or PaymentAttemptState.Error)
        {
            return ConfirmOutcome.NotPaid;
        }

        var gateway = await db.PaymentGateways.SingleAsync(g => g.Id == attempt.GatewayId, cancellationToken);
        var connector = GatewayConnector.Read(gateway.Connector);
        var answer = await client.SendAsync(connector.Status, Values(gateway, payment, attempt, LocalizedText.English, string.Empty), await CredentialsAsync(gateway, cancellationToken), cancellationToken);
        attempt.CheckedAt = DateTimeOffset.UtcNow;
        if (!answer.Ok)
        {
            return ConfirmOutcome.Pending;
        }

        var status = answer.Values.GetValueOrDefault("status") ?? string.Empty;
        attempt.GatewayStatus = Limit(status);
        attempt.TransactionId ??= Limit(answer.Values.GetValueOrDefault("transactionId"));
        var paid = connector.StatusValues.Paid.Contains(status, StringComparer.OrdinalIgnoreCase);
        // The amount and reference, where the status API gives them, must be ours.
        if (paid && answer.Values.GetValueOrDefault("amount") is { } amountText
            && (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                || (connector.StatusAmountInMinorUnits ? amount / 100m : amount) != payment.AmountAed))
        {
            audit.Add(AuditCategory.Change, "payments.amount-mismatch", "payment", payment.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = attempt.Reference });
            return ConfirmOutcome.Pending;
        }

        if (paid && answer.Values.GetValueOrDefault("reference") is { Length: > 0 } reference && !string.Equals(reference, attempt.Reference, StringComparison.Ordinal))
        {
            audit.Add(AuditCategory.Change, "payments.reference-mismatch", "payment", payment.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = attempt.Reference });
            return ConfirmOutcome.Pending;
        }

        var now = DateTimeOffset.UtcNow;
        if (connector.StatusValues.Failed.Contains(status, StringComparer.OrdinalIgnoreCase) || connector.StatusValues.Cancelled.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            attempt.State = connector.StatusValues.Cancelled.Contains(status, StringComparer.OrdinalIgnoreCase) ? PaymentAttemptState.Cancelled : PaymentAttemptState.Failed;
            attempt.EndedAt = now;
            audit.Add(AuditCategory.Change, "payments.attempt.failed", "payment", payment.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = attempt.Reference, ["status"] = attempt.GatewayStatus });
            return ConfirmOutcome.NotPaid;
        }

        if (!paid)
        {
            return ConfirmOutcome.Pending;
        }

        attempt.State = PaymentAttemptState.Paid;
        attempt.EndedAt = now;
        if (payment.State != PaymentState.AwaitingPayment)
        {
            // Paid twice (another try was paid first, or the request had expired): kept for reconciliation and refund.
            audit.Add(AuditCategory.Change, "payments.paid-again", "payment", payment.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = attempt.Reference, ["state"] = payment.State.ToString() });
            return ConfirmOutcome.Paid;
        }

        payment.State = PaymentState.Paid;
        payment.PaidAt = now;
        payment.PaidAttemptId = attempt.Id;
        payment.TransactionId = attempt.TransactionId;
        payment.UpdatedAt = now;
        await NumberReceiptAsync(payment, cancellationToken);
        audit.Add(AuditCategory.Change, "payments.paid", "payment", payment.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reference"] = attempt.Reference,
            ["receipt"] = payment.ReceiptNumber,
            ["amountAed"] = payment.AmountAed,
            ["ticketId"] = payment.TicketId?.ToString(),
        });

        if (payment.IsTest)
        {
            gateway.TestPaymentSucceededAt = now;
            return ConfirmOutcome.Paid;
        }

        var ticket = await db.Tickets.SingleAsync(t => t.Id == payment.TicketId, cancellationToken);
        var card = await ServiceRequestEndpoints.CardOfAsync(db, ticket.Id, cancellationToken);
        Notify(NotificationEvents.PaymentReceived, ticket, payment, card.Name);
        if (ticket.Held && await db.ServiceRequests.AnyAsync(r => r.TicketId == ticket.Id && r.Hold == ServiceRequestHold.Payment, cancellationToken))
        {
            await HoldAsync(ticket, false, cancellationToken);
            PaidAndReleased = ticket;
        }

        return ConfirmOutcome.Paid;
    }

    /// <summary>The request a confirmation released from its payment hold; the caller moves it on (approvals or the service desk).</summary>
    public Ticket? PaidAndReleased { get; private set; }

    private async Task NumberReceiptAsync(Payment payment, CancellationToken cancellationToken)
    {
        // One receipt series per tenant: numbered under a lock, never reused.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('zelvo.receipts'), hashtext({payment.TenantId.ToString()}))", cancellationToken);
        var last = await db.Payments.Where(p => p.ReceiptSequence != null).MaxAsync(p => (long?)p.ReceiptSequence, cancellationToken) ?? 0;
        payment.ReceiptSequence = last + 1;
        payment.ReceiptNumber = $"RCT-{payment.ReceiptSequence:000000}";
    }

    /// <summary>Refunds through the gateway's refund API, or records a manual refund when it has none.</summary>
    public async Task<PaymentRefund> RefundAsync(Payment payment, decimal amount, string reason, Guid? by, CancellationToken cancellationToken)
    {
        var gateway = await db.PaymentGateways.SingleAsync(g => g.Id == payment.GatewayId, cancellationToken);
        var connector = GatewayConnector.Read(gateway.Connector);
        var refund = new PaymentRefund { PaymentId = payment.Id, AmountAed = amount, Reason = reason, CreatedById = by };
        if (connector.Refund is { } operation)
        {
            var attempt = await db.PaymentAttempts.SingleAsync(a => a.Id == payment.PaidAttemptId, cancellationToken);
            var values = new Dictionary<string, string>(Values(gateway, payment, attempt, LocalizedText.English, reason), StringComparer.Ordinal)
            {
                ["refundAmount"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["refundAmountMinor"] = ((long)(amount * 100)).ToString(CultureInfo.InvariantCulture),
                ["refundReference"] = refund.Id.ToString("N"),
            };
            var answer = await client.SendAsync(operation, values, await CredentialsAsync(gateway, cancellationToken), cancellationToken);
            var status = answer.Values.GetValueOrDefault("status");
            var done = answer.Ok && (connector.RefundSucceeded.Count == 0 || connector.RefundSucceeded.Contains(status ?? string.Empty, StringComparer.OrdinalIgnoreCase));
            refund.State = done ? RefundState.Completed : RefundState.Failed;
            refund.GatewayRefundId = Limit(answer.Values.GetValueOrDefault("refundId"));
        }
        else
        {
            refund.State = RefundState.Manual;
        }

        db.PaymentRefunds.Add(refund);
        if (refund.State != RefundState.Failed)
        {
            payment.RefundedAed += amount;
            payment.State = payment.RefundedAed >= payment.AmountAed ? PaymentState.Refunded : PaymentState.PartlyRefunded;
            payment.UpdatedAt = DateTimeOffset.UtcNow;
            if (payment.TicketId is { } ticketId)
            {
                var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, cancellationToken);
                var card = await ServiceRequestEndpoints.CardOfAsync(db, ticketId, cancellationToken);
                Notify(NotificationEvents.PaymentRefunded, ticket, payment, card.Name, amount, reason);
            }
        }

        audit.Add(AuditCategory.Change, "payments.refunded", "payment", payment.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["refundId"] = refund.Id.ToString(),
            ["amountAed"] = amount,
            ["state"] = refund.State.ToString(),
        });
        return refund;
    }

    /// <summary>An unpaid request past its time: the payment expires and the request is closed with the reason.</summary>
    public async Task ExpireAsync(Payment payment, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        payment.State = PaymentState.Expired;
        payment.UpdatedAt = now;
        var ticket = await db.Tickets.SingleAsync(t => t.Id == payment.TicketId, cancellationToken);
        if (TicketWorkflow.IsOpen(ticket.State))
        {
            var language = await db.Users.AsNoTracking().Where(u => u.Id == ticket.RequesterId).Select(u => u.PreferredLanguage).SingleOrDefaultAsync(cancellationToken);
            await HoldAsync(ticket, false, cancellationToken);
            ticket.State = TicketState.Closed;
            ticket.OnHoldReason = null;
            ticket.ResolutionNotes = NotificationTexts.For(language is LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English).Terms["payment.expired-note"];
            ticket.ClosedAt = now;
            ticket.UpdatedAt = now;
        }

        var card = await ServiceRequestEndpoints.CardOfAsync(db, ticket.Id, cancellationToken);
        Notify(NotificationEvents.PaymentExpired, ticket, payment, card.Name);
        audit.Add(AuditCategory.Change, "payments.expired", "payment", payment.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["ticketId"] = ticket.Id.ToString() });
    }

    /// <summary>Cancels the unpaid payment of a request that was closed another way.</summary>
    public async Task CancelOpenAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        foreach (var payment in await db.Payments.Where(p => p.TicketId == ticketId && p.State == PaymentState.AwaitingPayment).ToListAsync(cancellationToken))
        {
            payment.State = PaymentState.Cancelled;
            payment.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>The decrypted credentials of <paramref name="gateway"/>.</summary>
    public async Task<Dictionary<string, string>> CredentialsAsync(PaymentGateway gateway, CancellationToken cancellationToken) =>
        gateway.EncryptedCredentials is { } encrypted
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await Secrets.UnprotectAsync(CredentialPurpose, encrypted, cancellationToken)) ?? []
            : [];

    /// <summary>Encrypts credentials for storing.</summary>
    public Task<string> ProtectAsync(IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken) =>
        Secrets.ProtectAsync(CredentialPurpose, JsonSerializer.Serialize(credentials), cancellationToken);

    /// <summary>The values templates are filled with.</summary>
    public Dictionary<string, string> Values(PaymentGateway gateway, Payment payment, PaymentAttempt attempt, string language, string description)
    {
        var web = options.PublicUrl ?? new Uri("http://localhost:3000/");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = payment.AmountAed.ToString("0.00", CultureInfo.InvariantCulture),
            ["amountMinor"] = ((long)(payment.AmountAed * 100)).ToString(CultureInfo.InvariantCulture),
            ["currency"] = "AED",
            ["reference"] = attempt.Reference,
            ["transactionId"] = attempt.TransactionId ?? string.Empty,
            ["description"] = description,
            ["returnUrl"] = new Uri(web, $"{(language == LocalizedText.Arabic ? "ar" : "en")}/payments/return/{attempt.Reference}").ToString(),
            ["callbackUrl"] = new Uri(web, $"payments/callback/{attempt.Reference}").ToString(),
            ["merchantId"] = gateway.MerchantId ?? string.Empty,
            ["language"] = language,
        };
    }

    /// <summary>A reference no one can guess: "ZP" and 24 random letters and digits.</summary>
    public static string NewReference()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return "ZP" + string.Create(24, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }

    private static string? Limit(string? value) => value is null ? null : value[..Math.Min(value.Length, PaymentAttempt.GatewayValueMaxLength)];

    private void Notify(string eventType, Ticket ticket, Payment payment, LocalizedText serviceName, decimal? refund = null, string? reason = null)
    {
        var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
        {
            [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
            [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
            [Placeholders.ServiceName] = PlaceholderValue.OfText(serviceName),
            [Placeholders.PaymentAmount] = PlaceholderValue.OfText(payment.AmountAed.ToString("0.00", CultureInfo.InvariantCulture)),
            [Placeholders.PaymentReceipt] = PlaceholderValue.OfText(payment.ReceiptNumber ?? string.Empty),
            [Placeholders.PaymentTransaction] = PlaceholderValue.OfText(payment.TransactionId ?? string.Empty),
            [Placeholders.PaymentPaidAt] = PlaceholderValue.OfDate(payment.PaidAt),
            [Placeholders.PaymentDueBy] = PlaceholderValue.OfDate(payment.ExpiresAt),
            [Placeholders.RefundAmount] = PlaceholderValue.OfText(refund?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty),
            [Placeholders.RefundReason] = PlaceholderValue.OfText(reason ?? string.Empty),
        };
        pending.Add(new NotificationRequest(eventType, [ticket.RequesterId], values, RequestLookups.RequesterPath(ticket.Id), TicketId: ticket.Id));
    }
}
