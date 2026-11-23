using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Integrations;
using Zelvo.Licensing;
using Zelvo.Secrets;

namespace Zelvo.Integrations;

/// <summary>
/// Makes one attempt of a webhook delivery, for the tenant of the scope: signs and sends it,
/// writes the integration log, and records the outcome (success, a retry later, or giving up).
/// After <see cref="WebhookOptions.DisableAfterFailures"/> deliveries in a row fail for good, the
/// subscription is disabled (audited). Used by the worker and by the webhook tester.
/// </summary>
public sealed partial class WebhookDeliveryService(
    ZelvoDbContext db,
    ISecretProtector secrets,
    WebhookSender sender,
    OutboundAddressPolicy policy,
    IntegrationOptions options,
    ILicenseService licenses,
    IAuditLog audit,
    ILogger<WebhookDeliveryService> logger)
{
    /// <summary>Purpose of a subscription's signing secrets in <see cref="ISecretProtector"/> (current and previous).</summary>
    public static string SecretPurpose(Guid subscriptionId) => $"webhooks.subscription:{subscriptionId:N}.secret";

    /// <summary>Attempts delivery <paramref name="deliveryId"/> now; returns what the receiver did, or null when there was nothing to send.</summary>
    public async Task<WebhookSendResult?> AttemptAsync(long deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken).ConfigureAwait(false);
        if (delivery is not { Status: WebhookDeliveryStatus.Pending })
        {
            return null;
        }

        var subscription = await db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == delivery.SubscriptionId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (subscription is null || (!delivery.Manual && subscription.Status != WebhookStatus.Active))
        {
            Finish(delivery, WebhookDeliveryStatus.Failed, now, "subscription-not-active: the subscription is paused or disabled");
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        WebhookSendResult result;
        var permanent = false;
        if (policy.CheckUrl(subscription.Url, out var url) is { } refused)
        {
            result = new WebhookSendResult(false, null, 0, null, null, $"{refused}: the receiver URL is not allowed");
            permanent = true;
        }
        else
        {
            var signing = new List<string> { await secrets.UnprotectAsync(SecretPurpose(subscription.Id), subscription.SecretCiphertext, cancellationToken).ConfigureAwait(false) };
            if (subscription.PreviousSecretCiphertext is { } previous && subscription.PreviousSecretExpiresAt > now)
            {
                signing.Add(await secrets.UnprotectAsync(SecretPurpose(subscription.Id), previous, cancellationToken).ConfigureAwait(false));
            }

            result = await sender.SendAsync(url!, delivery.EventId, delivery.EventType, delivery.Payload, signing, licenses.EnvironmentName(), cancellationToken).ConfigureAwait(false);
            permanent = result.Error?.StartsWith("address-not-allowed", StringComparison.Ordinal) == true;
        }

        delivery.Attempts++;
        delivery.LastAttemptAt = now;
        delivery.LastStatusCode = result.StatusCode;
        delivery.LastDurationMs = result.DurationMs;
        delivery.LeaseUntil = null;

        db.IntegrationLog.Add(new IntegrationLogEntry
        {
            TenantId = delivery.TenantId,
            OccurredAt = now,
            Direction = IntegrationDirection.Outgoing,
            SubscriptionId = subscription.Id,
            DeliveryId = delivery.Id,
            EventType = delivery.EventType,
            Method = "POST",
            Target = Shorten(url?.GetLeftPart(UriPartial.Path) ?? subscription.Url, IntegrationLogEntry.TargetMaxLength)!,
            StatusCode = result.StatusCode,
            DurationMs = result.DurationMs,
            RequestBody = PersonalDataMasker.MaskBody(delivery.Payload, "application/json", IntegrationLogEntry.BodyMaxLength),
            ResponseBody = PersonalDataMasker.MaskBody(result.ResponseBody, result.ResponseContentType, IntegrationLogEntry.BodyMaxLength),
            Error = result.Error is null ? null : Shorten(PersonalDataMasker.MaskText(result.Error), IntegrationLogEntry.ErrorMaxLength),
            CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        });

        var webhooks = options.Webhooks;
        if (result.Succeeded)
        {
            Finish(delivery, WebhookDeliveryStatus.Succeeded, now, null);
            if (!delivery.Manual && subscription.ConsecutiveFailures > 0)
            {
                await db.WebhookSubscriptions.Where(s => s.Id == subscription.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.ConsecutiveFailures, 0), cancellationToken).ConfigureAwait(false);
            }
        }
        else if (permanent || delivery.Manual || delivery.Attempts >= webhooks.MaxAttempts)
        {
            Finish(delivery, WebhookDeliveryStatus.Failed, now, result.Error);
            if (!delivery.Manual)
            {
                await CountFailureAsync(subscription, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            delivery.LastError = Shorten(result.Error, WebhookDelivery.ErrorMaxLength);
            delivery.NextAttemptAt = now + webhooks.RetryDelays[delivery.Attempts - 1];
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task CountFailureAsync(WebhookSubscription subscription, CancellationToken cancellationToken)
    {
        // Counted in the database, so parallel workers do not lose counts; not a change to audit.
        var failures = await db.Database.SqlQuery<int>($"""
            UPDATE webhook_subscriptions SET consecutive_failures = consecutive_failures + 1
            WHERE id = {subscription.Id} RETURNING consecutive_failures AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        // The update changed the row version; read the subscription again before changing it.
        await db.Entry(subscription).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (failures is [var count] && count >= options.Webhooks.DisableAfterFailures && subscription.Status == WebhookStatus.Active)
        {
            subscription.Status = WebhookStatus.Disabled;
            subscription.DisabledReason = $"too-many-failures: {count} deliveries in a row failed";
            subscription.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add(AuditCategory.Admin, "integrations.webhook.disabled", "webhook_subscription", subscription.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["consecutiveFailures"] = count });
            LogDisabled(logger, subscription.Id, count);
        }
    }

    private static void Finish(WebhookDelivery delivery, WebhookDeliveryStatus status, DateTimeOffset now, string? error)
    {
        delivery.Status = status;
        delivery.CompletedAt = now;
        delivery.NextAttemptAt = null;
        delivery.LeaseUntil = null;
        delivery.LastError = Shorten(error, WebhookDelivery.ErrorMaxLength);
    }

    private static string? Shorten(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook subscription {SubscriptionId} was disabled after {Failures} failed deliveries in a row.")]
    private static partial void LogDisabled(ILogger logger, Guid subscriptionId, int failures);
}
