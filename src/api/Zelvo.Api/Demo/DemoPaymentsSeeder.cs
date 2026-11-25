using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Payments;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Payments;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task E8: online payments in sandbox mode for each sample organisation, so the payment flow can
/// be shown without real money (demo mode requirement): a "Demo pay (sandbox)" gateway connected to
/// the development payment mock of Compose with the generic connector, and payments turned on, so
/// the sample paid services (such as the trade licence) ask for their fee on submission.
/// </summary>
internal sealed class DemoPaymentsSeeder : IDemoSeeder
{
    /// <summary>The payment mock on the Compose network.</summary>
    public const string MockUrl = "http://payment-mock:8098";

    public string Key => "e8-payments";

    public int Order => 240;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        db.PaymentGateways.Add(new PaymentGateway
        {
            Name = "Demo pay (sandbox)",
            MerchantId = "DEMO-" + context.Tenant.Slug.ToUpperInvariant(),
            Mode = GatewayMode.Test,
            IsDefault = true,
            Connector = GatewayConnector.Write(Connector()),
            // The sandbox mock needs no credentials; a real gateway's are entered in Settings → Payments.
        });
        await settings.SetAsync(PaymentSettings.Enabled, true, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The mock mapped as a customer's administrator would map any gateway.</summary>
    internal static GatewayConnector Connector() => new()
    {
        Create = new GatewayOperation
        {
            Method = "POST",
            Url = MockUrl + "/payments",
            BodyFormat = BodyFormat.Json,
            Body = new()
            {
                ["amount"] = "{{amount}}", ["currency"] = "{{currency}}", ["reference"] = "{{reference}}",
                ["returnUrl"] = "{{returnUrl}}", ["callbackUrl"] = "{{callbackUrl}}",
            },
            Response = new() { ["transactionId"] = "id", ["redirectUrl"] = "url" },
        },
        Status = new GatewayOperation
        {
            Method = "GET",
            Url = MockUrl + "/payments/{{transactionId}}",
            BodyFormat = BodyFormat.None,
            Response = new() { ["status"] = "status", ["amount"] = "amount", ["reference"] = "reference" },
        },
        Refund = new GatewayOperation
        {
            Method = "POST",
            Url = MockUrl + "/payments/{{transactionId}}/refund",
            BodyFormat = BodyFormat.Json,
            Body = new() { ["amount"] = "{{refundAmount}}" },
            Response = new() { ["refundId"] = "refundId", ["status"] = "status" },
        },
        RefundSucceeded = ["REFUNDED"],
        StatusValues = new StatusValues { Paid = ["CAPTURED"], Failed = ["DECLINED"], Cancelled = ["CANCELLED"] },
        Callback = new CallbackSettings { ReferenceField = "reference" },
        Settlement = new SettlementColumns { Reference = "reference", TransactionId = "transaction", Amount = "amount", Status = "status" },
    };
}
