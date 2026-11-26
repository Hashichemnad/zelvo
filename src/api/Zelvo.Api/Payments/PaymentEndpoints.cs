using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;
using Zelvo.Data.Payments;
using Zelvo.Data.Tickets;
using Zelvo.Licensing;

namespace Zelvo.Api.Payments;

/// <summary>
/// Online payments (module 15, task E8): Settings → Payments (the switch and gateways), paying a
/// request's fee, the gateway's return and direct notification, receipts, refunds, finance's list,
/// Excel export and daily reconciliation.
/// </summary>
internal static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/settings/payments").WithTags("Payments").RequireTenant();
        settings.MapGet("/", GetSettingsAsync).WithName("GetPaymentSettings")
            .WithSummary("Whether online payments are on and how long unpaid requests wait. Every signed-in user can read whether they are on.")
            .RequireAuthorization().Produces<PaymentSettingsResponse>();
        settings.MapPut("/", PutSettingsAsync).WithName("UpdatePaymentSettings")
            .WithSummary("Turn online payments on or off (off: fees are for information only) and set the hours unpaid requests wait. Needs payments.manage; audited.")
            .RequirePermission(Permissions.PaymentsManage).Produces<PaymentSettingsResponse>().ProducesValidationProblem();

        var gateways = group.MapGroup("/payments/gateways").WithTags("Payments").RequireTenant();
        gateways.MapGet("/", ListGatewaysAsync).WithName("ListPaymentGateways")
            .WithSummary("The tenant's gateways with their connector; credentials are never returned, only their names. Needs payments.manage.")
            .RequirePermission(Permissions.PaymentsManage).Produces<IReadOnlyList<GatewayResponse>>();
        gateways.MapPost("/", CreateGatewayAsync).WithName("CreatePaymentGateway")
            .WithSummary("Connect a gateway with the generic connector: name, merchant id, operations (create, status, refund) with field mapping, status values, callback signature and credentials (encrypted). Starts in test mode. Needs payments.manage; audited without credential values.")
            .RequirePermission(Permissions.PaymentsManage).Produces<GatewayResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        gateways.MapPut("/{id:guid}", UpdateGatewayAsync).WithName("UpdatePaymentGateway")
            .WithSummary("Change a gateway. Credentials left out are kept, except that a new gateway host needs them again. Live mode needs a successful sandbox test payment. Needs payments.manage; audited.")
            .RequirePermission(Permissions.PaymentsManage).Produces<GatewayResponse>().ProducesValidationProblem();
        gateways.MapDelete("/{id:guid}", DeleteGatewayAsync).WithName("DeletePaymentGateway")
            .WithSummary("Remove a gateway no payment used (otherwise turn it off). Needs payments.manage; audited.")
            .RequirePermission(Permissions.PaymentsManage).Produces(StatusCodes.Status204NoContent);
        gateways.MapPost("/{id:guid}/test-connection", TestConnectionAsync).WithName("TestPaymentGateway")
            .WithSummary("Calls the gateway's status API with a test reference and says whether it answered. Needs payments.manage.")
            .RequirePermission(Permissions.PaymentsManage).Produces<TestConnectionResponse>();
        gateways.MapPost("/{id:guid}/test-payment", TestPaymentAsync).WithName("StartTestPayment")
            .WithSummary("Starts a sandbox payment of 1.00 AED (test mode only) and returns the gateway's page; once confirmed it allows live mode. Needs payments.manage; audited.")
            .RequirePermission(Permissions.PaymentsManage).Produces<PayResponse>();

        var payments = group.MapGroup("/payments").WithTags("Payments").RequireTenant();
        payments.MapGet("/", ListAsync).WithName("ListPayments")
            .WithSummary("Payments, newest first, by state, gateway and paid date. Needs payments.read.")
            .RequirePermission(Permissions.PaymentsRead).Produces<PageResult<PaymentResponse>>();
        payments.MapGet("/export", ExportAsync).WithName("ExportPayments")
            .WithSummary("The payments of a period as Excel (format=xlsx) or CSV for finance. Needs payments.read; audited.")
            .RequirePermission(Permissions.PaymentsRead).Produces<byte[]>(contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        payments.MapGet("/{id:guid}", GetAsync).WithName("GetPayment")
            .WithSummary("A payment with its tries and refunds: for its requester, staff with requests.read and payments.read.")
            .RequireAuthorization().Produces<PaymentResponse>();
        payments.MapGet("/{id:guid}/receipt", ReceiptAsync).WithName("GetPaymentReceipt")
            .WithSummary("The bilingual e-receipt of a paid payment: receipt number, transaction id, amount and its lines, service, request and payer.")
            .RequireAuthorization().Produces<ReceiptResponse>();
        payments.MapPost("/{id:guid}/pay", PayAsync).WithName("PayPayment")
            .WithSummary("The requester (or who made the request) pays: a new try with a unique reference is sent to the gateway, which returns the page to pay on. Card details are entered only there. Audited.")
            .RequirePerson().RequireAuthorization().Produces<PayResponse>();
        payments.MapPost("/{id:guid}/refunds", RefundAsync).WithName("RefundPayment")
            .WithSummary("Refund all or part of a paid payment, with a reason: through the gateway's refund API, or recorded as manual when it has none. Needs payments.refund; audited.")
            .RequirePermission(Permissions.PaymentsRefund).Produces<PaymentResponse>().ProducesValidationProblem();
        payments.MapPost("/attempts/{reference}/confirm", ConfirmAsync).WithName("ConfirmPaymentAttempt")
            .WithSummary("After the gateway sends the payer back: Zelvo asks the gateway's status API and applies the result once. For whoever started the try, the requester and payments.read.")
            .RequirePerson().RequireAuthorization().Produces<PaymentResponse>();
        payments.MapPost("/callback/{reference}", CallbackAsync).WithName("PaymentCallback")
            .WithSummary("The gateway's direct notification. Its signature is checked when the connector names one; the result is then confirmed with the status API and applied once. Always answers 200 so nothing is learnt from it.")
            .AllowAnonymousAccess().DisableAntiforgery().Produces(StatusCodes.Status200OK);
        payments.MapGet("/reconciliations", ReconciliationsAsync).WithName("ListPaymentReconciliations")
            .WithSummary("Daily reconciliations by gateway and day, newest first. Needs payments.read.")
            .RequirePermission(Permissions.PaymentsRead).Produces<IReadOnlyList<ReconciliationResponse>>();
        payments.MapPost("/reconciliations", RunReconciliationAsync).WithName("RunPaymentReconciliation")
            .WithSummary("Reconcile a gateway's day again (status API for each payment of the day). Needs payments.read; audited.")
            .RequirePermission(Permissions.PaymentsRead).Produces<ReconciliationResponse>();
        payments.MapPost("/reconciliations/{id:guid}/settlement-file", SettlementFileAsync).WithName("ImportSettlementFile")
            .WithSummary("Import the gateway's settlement file (CSV or Excel, columns as the connector says) for the day: lines are matched to Zelvo's payments, and transactions Zelvo does not know are listed. Needs payments.read; audited.")
            .RequirePermission(Permissions.PaymentsRead).DisableAntiforgery().Produces<ReconciliationResponse>();
        payments.MapGet("/reconciliations/{id:guid}/export", ReconciliationExportAsync).WithName("ExportPaymentReconciliation")
            .WithSummary("A reconciliation's lines as Excel or CSV. Needs payments.read; audited.")
            .RequirePermission(Permissions.PaymentsRead).Produces<byte[]>(contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        group.MapGet("/service-requests/{id:guid}/payments", ForRequestAsync).WithTags("Payments").RequireTenant().WithName("ListServiceRequestPayments")
            .WithSummary("The fee of a request: its payments with tries and refunds. For whoever may see the request.")
            .RequireAuthorization().Produces<IReadOnlyList<PaymentResponse>>();
        return group;
    }

    // ---- settings ----

    internal static async Task<IResult> GetSettingsAsync(TenantSettingsStore store, CurrentUser user, CancellationToken cancellationToken) =>
        TypedResults.Ok(new PaymentSettingsResponse(
            await store.GetAsync(PaymentSettings.Enabled, cancellationToken),
            user.Has(Permissions.PaymentsManage) ? await store.GetAsync(PaymentSettings.UnpaidExpiryHours, cancellationToken) : null));

    internal static async Task<IResult> PutSettingsAsync(PaymentSettingsRequest request, TenantSettingsStore store, ZelvoDbContext db, IAuditLog audit, CurrentUser user, CancellationToken cancellationToken)
    {
        var hours = request.UnpaidExpiryHours ?? await store.GetAsync(PaymentSettings.UnpaidExpiryHours, cancellationToken);
        if (!PaymentSettings.UnpaidExpiryHours.IsValid(hours))
        {
            return UserEndpoints.Validation("unpaidExpiryHours", "Use 1 to 2160 hours.");
        }

        if (request.Enabled == true && !await db.PaymentGateways.AnyAsync(g => g.Enabled, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "no-gateway", "Connect a payment gateway first.");
        }

        await store.SetAsync(PaymentSettings.Enabled, request.Enabled ?? false, cancellationToken);
        await store.SetAsync(PaymentSettings.UnpaidExpiryHours, hours, cancellationToken);
        audit.Add(AuditCategory.Admin, "payments.settings.changed", "settings", "payments",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["enabled"] = request.Enabled ?? false, ["unpaidExpiryHours"] = hours });
        await db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(store, user, cancellationToken);
    }

    // ---- gateways ----

    internal static async Task<IResult> ListGatewaysAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.PaymentGateways.AsNoTracking().OrderBy(g => g.Name).ToListAsync(cancellationToken)).Select(GatewayResponse.From).ToList());

    internal static async Task<IResult> CreateGatewayAsync(GatewayRequest request, ZelvoDbContext db, PaymentService payments, PaymentGatewayClient client, IAuditLog audit, CancellationToken cancellationToken)
    {
        var credentials = Clean(request.Credentials);
        if (Problems(request, client, credentials.Keys) is { } problem)
        {
            return problem;
        }

        if (await db.PaymentGateways.AnyAsync(g => g.Name == request.Name!.Trim(), cancellationToken))
        {
            return UserEndpoints.Validation("name", "Another gateway has this name.");
        }

        var gateway = new PaymentGateway
        {
            Name = request.Name!.Trim(),
            MerchantId = string.IsNullOrWhiteSpace(request.MerchantId) ? null : request.MerchantId.Trim(),
            Connector = GatewayConnector.Write(request.Connector!),
            Enabled = request.Enabled ?? true,
            Mode = GatewayMode.Test,
            IsDefault = request.IsDefault == true || !await db.PaymentGateways.AnyAsync(cancellationToken),
            CredentialNames = [.. credentials.Keys.Order(StringComparer.Ordinal)],
            EncryptedCredentials = credentials.Count == 0 ? null : await payments.ProtectAsync(credentials, cancellationToken),
        };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (gateway.IsDefault)
        {
            await db.PaymentGateways.Where(g => g.IsDefault).ExecuteUpdateAsync(u => u.SetProperty(g => g.IsDefault, false), cancellationToken);
        }

        db.PaymentGateways.Add(gateway);
        audit.Add(AuditCategory.Admin, "payments.gateway.created", "payment-gateway", gateway.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = gateway.Name, ["credentials"] = gateway.CredentialNames });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/payments/gateways/{gateway.Id}", GatewayResponse.From(gateway));
    }

    internal static async Task<IResult> UpdateGatewayAsync(
        Guid id, GatewayRequest request, ZelvoDbContext db, PaymentService payments, PaymentGatewayClient client, IAuditLog audit, CancellationToken cancellationToken)
    {
        var gateway = await db.PaymentGateways.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (gateway is null)
        {
            return FormsCommonNotFound();
        }

        var existing = await payments.CredentialsAsync(gateway, cancellationToken);
        var given = Clean(request.Credentials);
        // A connector pointing at another host needs its credentials again, so a changed address cannot receive the stored ones.
        var before = Hosts(GatewayConnector.Read(gateway.Connector));
        var after = request.Connector is null ? before : Hosts(request.Connector);
        if (!after.SetEquals(before) && given.Count == 0 && existing.Count > 0)
        {
            return UserEndpoints.Validation("credentials", "Enter the credentials again for the new gateway address.");
        }

        var credentials = given.Count > 0 ? given : existing;
        foreach (var removed in request.RemoveCredentials ?? [])
        {
            credentials.Remove(removed);
        }

        var merged = request with { Name = request.Name ?? gateway.Name, Connector = request.Connector ?? GatewayConnector.Read(gateway.Connector) };
        if (Problems(merged, client, credentials.Keys) is { } problem)
        {
            return problem;
        }

        if (request.Mode == GatewayMode.Live && gateway.Mode != GatewayMode.Live && gateway.TestPaymentSucceededAt is null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "test-payment-required", "Make a successful test payment in sandbox mode before going live.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (request.IsDefault == true && !gateway.IsDefault)
        {
            await db.PaymentGateways.Where(g => g.IsDefault).ExecuteUpdateAsync(u => u.SetProperty(g => g.IsDefault, false), cancellationToken);
            gateway.IsDefault = true;
        }

        var connectorChanged = request.Connector is not null && GatewayConnector.Write(request.Connector) != GatewayConnector.Write(GatewayConnector.Read(gateway.Connector));
        gateway.Name = merged.Name!.Trim();
        gateway.MerchantId = request.MerchantId is null ? gateway.MerchantId : string.IsNullOrWhiteSpace(request.MerchantId) ? null : request.MerchantId.Trim();
        gateway.Connector = GatewayConnector.Write(merged.Connector!);
        gateway.Enabled = request.Enabled ?? gateway.Enabled;
        gateway.Mode = request.Mode ?? gateway.Mode;
        gateway.CredentialNames = [.. credentials.Keys.Order(StringComparer.Ordinal)];
        gateway.EncryptedCredentials = credentials.Count == 0 ? null : await payments.ProtectAsync(credentials, cancellationToken);
        gateway.UpdatedAt = DateTimeOffset.UtcNow;
        // A changed connector or going back to test mode needs a new test payment before live mode.
        if (connectorChanged)
        {
            gateway.TestPaymentSucceededAt = null;
            gateway.Mode = GatewayMode.Test;
        }

        audit.Add(AuditCategory.Admin, "payments.gateway.changed", "payment-gateway", gateway.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["credentialsChanged"] = given.Count > 0 || (request.RemoveCredentials?.Count ?? 0) > 0,
            ["mode"] = gateway.Mode.ToString(),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(GatewayResponse.From(gateway));
    }

    internal static async Task<IResult> DeleteGatewayAsync(Guid id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var gateway = await db.PaymentGateways.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (gateway is null)
        {
            return FormsCommonNotFound();
        }

        if (await db.Payments.AnyAsync(p => p.GatewayId == id && !p.IsTest, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "gateway-in-use", "Payments were made through this gateway; turn it off instead.");
        }

        await db.Payments.Where(p => p.GatewayId == id).ExecuteDeleteAsync(cancellationToken);
        db.PaymentGateways.Remove(gateway);
        audit.Add(AuditCategory.Admin, "payments.gateway.deleted", "payment-gateway", id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = gateway.Name });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> TestConnectionAsync(Guid id, ZelvoDbContext db, PaymentService payments, PaymentGatewayClient client, CancellationToken cancellationToken)
    {
        var gateway = await db.PaymentGateways.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (gateway is null)
        {
            return FormsCommonNotFound();
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var test = new Payment { GatewayId = id, AmountAed = 1m, IsTest = true };
        var attempt = new PaymentAttempt { PaymentId = test.Id, GatewayId = id, Reference = "ZPTEST" + PaymentService.NewReference()[2..12] };
        var answer = await client.SendAsync(GatewayConnector.Read(gateway.Connector).Status, payments.Values(gateway, test, attempt, LocalizedText.English, "Zelvo connection test"),
            await payments.CredentialsAsync(gateway, cancellationToken), cancellationToken);
        // Any HTTP answer shows the gateway is reachable and the address right; an unknown test reference is expected to be refused.
        return TypedResults.Ok(new TestConnectionResponse(answer.StatusCode > 0, answer.StatusCode, answer.StatusCode > 0 ? null : answer.Error,
            (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    internal static async Task<IResult> TestPaymentAsync(Guid id, ZelvoDbContext db, PaymentService payments, CurrentUser user, CancellationToken cancellationToken)
    {
        var gateway = await db.PaymentGateways.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (gateway is null)
        {
            return FormsCommonNotFound();
        }

        if (gateway.Mode != GatewayMode.Test)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "gateway-live", "Test payments are made in test mode.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var payment = new Payment { GatewayId = id, AmountAed = 1m, IsTest = true, Timing = PaymentTiming.OnSubmission };
        db.Payments.Add(payment);
        var (attempt, redirect, error) = await payments.StartAttemptAsync(payment, user.Account?.Id, user.Account?.PreferredLanguage ?? LocalizedText.English, "Zelvo test payment", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return error is null
            ? TypedResults.Ok(new PayResponse(payment.Id, attempt.Reference, redirect!))
            : ApiProblem.Create(StatusCodes.Status502BadGateway, "gateway-unavailable", $"The gateway did not start the payment ({error}).");
    }

    // ---- payments ----

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, PaymentState? state, Guid? gatewayId, DateOnly? from, DateOnly? to, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = Period(db.Payments.AsNoTracking().Where(p => !p.IsTest), from, to);
        if (state is { } s)
        {
            query = query.Where(p => p.State == s);
        }

        if (gatewayId is { } g)
        {
            query = query.Where(p => p.GatewayId == g);
        }

        var ordered = query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<PaymentResponse>(await ResponsesAsync(db, rows, staff: true, cancellationToken), page.Number, page.Size, total));
    }

    private static IQueryable<Payment> Period(IQueryable<Payment> query, DateOnly? from, DateOnly? to)
    {
        if (from is { } f)
        {
            var start = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(p => p.CreatedAt >= start || p.PaidAt >= start);
        }

        if (to is { } t)
        {
            var end = new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(p => p.CreatedAt < end);
        }

        return query;
    }

    internal static async Task<IResult> ExportAsync(DateOnly? from, DateOnly? to, string? format, ZelvoDbContext db, IAuditLog audit, IInstallationWatermark watermark, CancellationToken cancellationToken)
    {
        var tabular = format == "csv" ? TabularFormat.Csv : TabularFormat.Xlsx;
        var rows = await Period(db.Payments.AsNoTracking().Where(p => !p.IsTest), from, to).OrderBy(p => p.CreatedAt).Take(50_000).ToListAsync(cancellationToken);
        var responses = await ResponsesAsync(db, rows, staff: true, cancellationToken);
        string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        var content = TabularFile.Write(tabular,
            ["receipt", "request", "service", "requester", "amountAed", "refundedAed", "state", "timing", "gateway", "transactionId", "createdAt", "paidAt"],
            responses.Select(p => (IReadOnlyList<string?>)[
                p.ReceiptNumber, p.Request?.Number, p.Service?.Code, p.Payer?.DisplayName, Amount(p.AmountAed), Amount(p.RefundedAed), p.State.ToString(), p.Timing.ToString(),
                p.Gateway, p.TransactionId, p.CreatedAt.ToString("O", CultureInfo.InvariantCulture), p.PaidAt?.ToString("O", CultureInfo.InvariantCulture)]),
            watermark.Current);
        await audit.RecordAsync(AuditCategory.View, "payments.exported", "payments", "export",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["rows"] = rows.Count, ["from"] = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["to"] = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, cancellationToken);
        return TypedResults.File(content, TabularFile.ContentType(tabular), $"payments.{TabularFile.Extension(tabular)}");
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var payment = await VisibleAsync(db, user, id, cancellationToken);
        return payment is null ? FormsCommonNotFound() : TypedResults.Ok((await ResponsesAsync(db, [payment], Staff(user), cancellationToken)).Single());
    }

    internal static async Task<IResult> ForRequestAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (await ServiceRequestEndpoints.FindVisibleAsync(db, user, id, cancellationToken) is null)
        {
            return FormsCommonNotFound();
        }

        var rows = await db.Payments.AsNoTracking().Where(p => p.TicketId == id).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(await ResponsesAsync(db, rows, Staff(user), cancellationToken));
    }

    internal static async Task<IResult> ReceiptAsync(Guid id, ZelvoDbContext db, CurrentUser user, Data.Tenancy.ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var payment = await VisibleAsync(db, user, id, cancellationToken);
        if (payment is null || payment.ReceiptNumber is null)
        {
            return FormsCommonNotFound();
        }

        var response = (await ResponsesAsync(db, [payment], staff: false, cancellationToken)).Single();
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantContext.TenantId).Select(t => new { t.Id, t.Name }).FirstAsync(cancellationToken);
        var lines = JsonSerializer.Deserialize<List<FeeLine>>(payment.Breakdown, JsonSerializerOptions.Web) ?? [];
        return TypedResults.Ok(new ReceiptResponse(
            payment.ReceiptNumber, LocalizedTextDto.From(tenant.Name), response.Request, response.Service, response.Payer, payment.AmountAed, payment.RefundedAed, "AED",
            [.. lines.Select(l => new ReceiptLine(LocalizedTextDto.From(l.Name), l.AmountAed))], payment.TransactionId, payment.PaidAt!.Value, response.Gateway));
    }

    internal static async Task<IResult> PayAsync(
        Guid id, PayRequest? request, ZelvoDbContext db, CurrentUser user, PaymentService payments, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == id && !p.IsTest, cancellationToken);
        var ticket = payment?.TicketId is { } ticketId ? await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken) : null;
        var me = user.Account!.Id;
        if (payment is null || ticket is null || (ticket.RequesterId != me && ticket.CreatedById != me))
        {
            return FormsCommonNotFound();
        }

        if (payment.State != PaymentState.AwaitingPayment)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "payment-not-due", "This payment is not waiting to be paid.");
        }

        var gateway = await db.PaymentGateways.AsNoTracking().SingleAsync(g => g.Id == payment.GatewayId, cancellationToken);
        if (!gateway.Enabled)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "gateway-off", "Online payment is not available right now. Try again later.");
        }

        var language = request?.Language is LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (attempt, redirect, error) = await payments.StartAttemptAsync(payment, me, language, $"{ticket.Number} {ticket.Title}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return error is null
            ? TypedResults.Ok(new PayResponse(payment.Id, attempt.Reference, redirect!))
            : ApiProblem.Create(StatusCodes.Status502BadGateway, "gateway-unavailable", "The payment gateway did not answer. Try again in a moment.");
    }

    internal static async Task<IResult> ConfirmAsync(string reference, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var attempt = await db.PaymentAttempts.SingleOrDefaultAsync(a => a.Reference == reference, cancellationToken);
        var payment = attempt is null ? null : await db.Payments.AsNoTracking().SingleAsync(p => p.Id == attempt.PaymentId, cancellationToken);
        var ticket = payment?.TicketId is { } ticketId ? await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken) : null;
        var me = user.Account!.Id;
        var allowed = attempt is not null && (attempt.StartedById == me || ticket?.RequesterId == me || user.Has(Permissions.PaymentsRead) || (payment!.IsTest && user.Has(Permissions.PaymentsManage)));
        if (!allowed)
        {
            return FormsCommonNotFound();
        }

        await ConfirmAndContinueAsync(http.RequestServices, attempt!, cancellationToken);
        var saved = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == attempt!.PaymentId, cancellationToken);
        return TypedResults.Ok((await ResponsesAsync(db, [saved], Staff(user), cancellationToken)).Single());
    }

    /// <summary>Confirms with the gateway in its own transaction, then moves a released request on (approvals or the service desk) and its bundle.</summary>
    internal static async Task<ConfirmOutcome> ConfirmAndContinueAsync(IServiceProvider services, PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ZelvoDbContext>();
        var payments = services.GetRequiredService<PaymentService>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var outcome = await payments.ConfirmAsync(attempt, cancellationToken);
        var released = payments.PaidAndReleased;
        if (released is not null)
        {
            var now = DateTimeOffset.UtcNow;
            await services.GetRequiredService<ServiceRequestCreator>().AfterPaymentAsync(released, now, cancellationToken);
            await services.GetRequiredService<Zelvo.Tickets.Sla.SlaTracker>().UpdateAsync([released], now, cancellationToken);
            await Zelvo.Tickets.RequestSearch.IndexAsync(db, services.GetRequiredService<Zelvo.Search.ISearchIndexer>(), [released], cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    internal static async Task<IResult> CallbackAsync(string reference, HttpContext http, ZelvoDbContext db, PaymentService payments, IAuditLog audit, CancellationToken cancellationToken)
    {
        // Small bodies only; never logged.
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > 64 * 1024 || reference.Length > PaymentAttempt.ReferenceMaxLength)
        {
            return TypedResults.Ok();
        }

        var attempt = await db.PaymentAttempts.SingleOrDefaultAsync(a => a.Reference == reference, cancellationToken);
        if (attempt is null)
        {
            return TypedResults.Ok();
        }

        var gateway = await db.PaymentGateways.AsNoTracking().SingleAsync(g => g.Id == attempt.GatewayId, cancellationToken);
        var signature = GatewayConnector.Read(gateway.Connector).Callback.Signature;
        if (signature.Algorithm != SignatureAlgorithm.None)
        {
            var credentials = await payments.CredentialsAsync(gateway, cancellationToken);
            if (signature.SecretName is not { } name || !credentials.TryGetValue(name, out var key)
                || !Templates.Verify(signature, key, buffer.ToArray(), http.Request.Headers[signature.Header ?? string.Empty].ToString()))
            {
                await audit.RecordAsync(AuditCategory.Change, "payments.callback.rejected", "payment", attempt.PaymentId.ToString(),
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = reference, ["reason"] = "signature" }, cancellationToken);
                return TypedResults.Ok();
            }
        }

        await audit.RecordAsync(AuditCategory.Change, "payments.callback.received", "payment", attempt.PaymentId.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = reference }, cancellationToken);
        // Whatever the notification says, the result comes from the status API.
        await ConfirmAndContinueAsync(http.RequestServices, attempt, cancellationToken);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> RefundAsync(
        Guid id, RefundRequest request, ZelvoDbContext db, CurrentUser user, PaymentService payments, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var payment = await db.Payments.FromSql($"SELECT *, xmin FROM payments WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (payment is null || payment.IsTest)
        {
            return FormsCommonNotFound();
        }

        if (payment.State is not (PaymentState.Paid or PaymentState.PartlyRefunded))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "payment-not-paid", "Only paid payments are refunded.");
        }

        var amount = request.AmountAed ?? payment.AmountAed - payment.RefundedAed;
        if (amount <= 0 || decimal.Round(amount, 2) != amount || amount > payment.AmountAed - payment.RefundedAed)
        {
            return UserEndpoints.Validation("amountAed", "Refund at most what is left of the payment, with at most two decimals.");
        }

        var refund = await payments.RefundAsync(payment, amount, request.Reason!.Trim(), user.Account?.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (refund.State == RefundState.Failed)
        {
            return ApiProblem.Create(StatusCodes.Status502BadGateway, "refund-failed", "The gateway did not refund the payment; nothing was refunded.");
        }

        return TypedResults.Ok((await ResponsesAsync(db, [payment], staff: true, cancellationToken)).Single());
    }

    // ---- reconciliation ----

    internal static async Task<IResult> ReconciliationsAsync(Guid? gatewayId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.PaymentReconciliations.AsNoTracking();
        if (gatewayId is { } g)
        {
            query = query.Where(r => r.GatewayId == g);
        }

        var rows = await query.OrderByDescending(r => r.Day).Take(90).ToListAsync(cancellationToken);
        var names = await db.PaymentGateways.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        return TypedResults.Ok(rows.Select(r => ReconciliationResponse.From(r, names.GetValueOrDefault(r.GatewayId), withLines: false)).ToList());
    }

    internal static async Task<IResult> RunReconciliationAsync(RunReconciliationRequest request, ZelvoDbContext db, PaymentReconciler reconciler, IAuditLog audit, CancellationToken cancellationToken)
    {
        var gateway = await db.PaymentGateways.AsNoTracking().SingleOrDefaultAsync(g => g.Id == request.GatewayId, cancellationToken);
        if (gateway is null)
        {
            return FormsCommonNotFound();
        }

        var day = await reconciler.ReconcileAsync(gateway, request.Day!.Value, cancellationToken);
        await audit.RecordAsync(AuditCategory.Change, "payments.reconciled", "payment-reconciliation", day.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["day"] = day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, cancellationToken);
        return TypedResults.Ok(ReconciliationResponse.From(day, gateway.Name, withLines: true));
    }

    internal static async Task<IResult> SettlementFileAsync(Guid id, IFormFile? file, ZelvoDbContext db, PaymentReconciler reconciler, IAuditLog audit, CancellationToken cancellationToken)
    {
        var day = await db.PaymentReconciliations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (day is null)
        {
            return FormsCommonNotFound();
        }

        if (file is null || TabularFile.FormatOf(file.FileName) is not { } format || file.Length > 10 * 1024 * 1024)
        {
            return UserEndpoints.Validation("file", "Send the settlement file as CSV or Excel (.xlsx), at most 10 MB.");
        }

        List<string[]> rows;
        try
        {
            await using var stream = file.OpenReadStream();
            rows = TabularFile.Read(format, stream, 20_000);
        }
        catch (TabularFileException exception)
        {
            return UserEndpoints.Validation("file", exception.Message);
        }

        var gateway = await db.PaymentGateways.AsNoTracking().SingleAsync(g => g.Id == day.GatewayId, cancellationToken);
        var problem = await reconciler.ImportAsync(gateway, day, rows, cancellationToken);
        if (problem is not null)
        {
            return UserEndpoints.Validation("file", problem);
        }

        await audit.RecordAsync(AuditCategory.Change, "payments.settlement.imported", "payment-reconciliation", day.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["rows"] = rows.Count - 1 }, cancellationToken);
        return TypedResults.Ok(ReconciliationResponse.From(day, gateway.Name, withLines: true));
    }

    internal static async Task<IResult> ReconciliationExportAsync(Guid id, string? format, ZelvoDbContext db, IAuditLog audit, IInstallationWatermark watermark, CancellationToken cancellationToken)
    {
        var day = await db.PaymentReconciliations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (day is null)
        {
            return FormsCommonNotFound();
        }

        var tabular = format == "csv" ? TabularFormat.Csv : TabularFormat.Xlsx;
        var lines = JsonSerializer.Deserialize<List<ReconciliationLine>>(day.Lines, JsonSerializerOptions.Web) ?? [];
        var content = TabularFile.Write(tabular, ["result", "reference", "receipt", "transactionId", "zelvoAmountAed", "gatewayAmountAed", "zelvoState", "gatewayStatus"],
            lines.Select(l => (IReadOnlyList<string?>)[l.Result.ToString(), l.Reference, l.Receipt, l.TransactionId,
                l.ZelvoAmountAed?.ToString("0.00", CultureInfo.InvariantCulture), l.GatewayAmountAed?.ToString("0.00", CultureInfo.InvariantCulture), l.ZelvoState, l.GatewayStatus]),
            watermark.Current);
        await audit.RecordAsync(AuditCategory.View, "payments.reconciliation.exported", "payment-reconciliation", id.ToString(), null, cancellationToken);
        return TypedResults.File(content, TabularFile.ContentType(tabular), $"reconciliation-{day.Day:yyyy-MM-dd}.{TabularFile.Extension(tabular)}");
    }

    // ---- helpers ----

    private static bool Staff(CurrentUser user) => user.Has(Permissions.PaymentsRead) || user.Has(Permissions.RequestsRead);

    private static async Task<Payment?> VisibleAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && !p.IsTest, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        return user.Has(Permissions.PaymentsRead) || await ServiceRequestEndpoints.FindVisibleAsync(db, user, payment.TicketId!.Value, cancellationToken) is not null ? payment : null;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult FormsCommonNotFound() => Forms.FormsCommon.NotFound("payment");

    private static Dictionary<string, string> Clean(Dictionary<string, string>? credentials) =>
        (credentials ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Key) && !string.IsNullOrEmpty(c.Value))
            .ToDictionary(c => c.Key.Trim(), c => c.Value, StringComparer.Ordinal);

    private static HashSet<string> Hosts(GatewayConnector connector) =>
        new[] { connector.Create.Url, connector.Status.Url, connector.Refund?.Url }.OfType<string>()
            .Select(u => Uri.TryCreate(Templates.Render(u, Templates.Sample, url: true), UriKind.Absolute, out var uri) ? uri.Authority : u)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? Problems(GatewayRequest request, PaymentGatewayClient client, IReadOnlyCollection<string> credentials)
    {
        var problems = new List<(string Field, string Code, string Message)>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > PaymentGateway.NameMaxLength)
        {
            problems.Add(("name", "name-required", "Name the gateway (at most 100 characters)."));
        }

        if (request.Connector is null)
        {
            problems.Add(("connector", "connector-required", "Map the gateway's API."));
        }
        else
        {
            problems.AddRange(request.Connector.Check(client.Policy, credentials));
        }

        if (credentials.Any(c => c.Length > 60 || !c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
        {
            problems.Add(("credentials", "credential-name-invalid", "Credential names are letters, digits, - and _ (at most 60)."));
        }

        return problems.Count == 0 ? null : TypedResults.ValidationProblem(
            problems.GroupBy(p => p.Field).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray(), StringComparer.Ordinal),
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "validation-failed",
                ["problems"] = problems.Select(p => new { field = p.Field, code = p.Code, message = p.Message }).ToList(),
            });
    }

    internal static async Task<List<PaymentResponse>> ResponsesAsync(ZelvoDbContext db, IReadOnlyList<Payment> payments, bool staff, CancellationToken cancellationToken)
    {
        var ids = payments.Select(p => p.Id).ToList();
        var attempts = (await db.PaymentAttempts.AsNoTracking().Where(a => ids.Contains(a.PaymentId)).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken)).ToLookup(a => a.PaymentId);
        var refunds = (await db.PaymentRefunds.AsNoTracking().Where(r => ids.Contains(r.PaymentId)).OrderBy(r => r.CreatedAt).ToListAsync(cancellationToken)).ToLookup(r => r.PaymentId);
        var ticketIds = payments.Select(p => p.TicketId).OfType<Guid>().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var services = await db.ServiceRequests.AsNoTracking().Where(r => ticketIds.Contains(r.TicketId))
            .Join(db.CatalogServices.AsNoTracking(), r => r.ServiceId, s => s.Id, (r, s) => new { r.TicketId, s.Id, s.Code, s.Name })
            .ToDictionaryAsync(x => x.TicketId, cancellationToken);
        var people = tickets.Values.Select(t => t.RequesterId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var gateways = await db.PaymentGateways.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        return [.. payments.Select(p =>
        {
            var ticket = p.TicketId is { } t ? tickets.GetValueOrDefault(t) : null;
            var service = p.TicketId is { } st && services.TryGetValue(st, out var s) ? new ServiceRefResponse(s.Id, s.Code, LocalizedTextDto.From(s.Name)) : null;
            var lines = JsonSerializer.Deserialize<List<FeeLine>>(p.Breakdown, JsonSerializerOptions.Web) ?? [];
            return new PaymentResponse(
                p.Id, ticket is null ? null : new RequestRef(ticket.Id, ticket.Number, ticket.Title), service,
                ticket is null ? null : new Incidents.PersonResponse(ticket.RequesterId, names.GetValueOrDefault(ticket.RequesterId)),
                p.AmountAed, p.RefundedAed, "AED", [.. lines.Select(l => new ReceiptLine(LocalizedTextDto.From(l.Name), l.AmountAed))],
                p.Timing, p.State, gateways.GetValueOrDefault(p.GatewayId), p.ReceiptNumber, p.TransactionId, p.ExpiresAt, p.PaidAt, p.CreatedAt,
                [.. attempts[p.Id].Select(a => new AttemptResponse(a.Reference, a.State, staff ? a.GatewayStatus : null, a.CreatedAt, a.EndedAt))],
                staff ? [.. refunds[p.Id].Select(r => new RefundResponse(r.Id, r.AmountAed, r.Reason, r.State, r.CreatedAt))]
                    : [.. refunds[p.Id].Where(r => r.State != RefundState.Failed).Select(r => new RefundResponse(r.Id, r.AmountAed, r.Reason, r.State, r.CreatedAt))]);
        })];
    }
}

// ---- requests and responses ----

/// <summary>Settings → Payments.</summary>
internal sealed record PaymentSettingsResponse(bool Enabled, int? UnpaidExpiryHours);

/// <summary>Changes to Settings → Payments.</summary>
internal sealed record PaymentSettingsRequest(bool? Enabled, int? UnpaidExpiryHours);

/// <summary>A gateway to connect or change.</summary>
internal sealed record GatewayRequest
{
    /// <summary>Name, for example "AjmanPay".</summary>
    [StringLength(PaymentGateway.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>The merchant id.</summary>
    [StringLength(PaymentGateway.MerchantIdMaxLength)]
    public string? MerchantId { get; init; }

    /// <summary>The connector.</summary>
    public GatewayConnector? Connector { get; init; }

    /// <summary>Credentials name → value (write only).</summary>
    public Dictionary<string, string>? Credentials { get; init; }

    /// <summary>Credentials to forget.</summary>
    public List<string>? RemoveCredentials { get; init; }

    /// <summary>Usable by services.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The default gateway.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>Test or live (live needs a successful test payment).</summary>
    public GatewayMode? Mode { get; init; }
}

/// <summary>A gateway, without credential values.</summary>
internal sealed record GatewayResponse(
    Guid Id, string Name, string? MerchantId, GatewayMode Mode, bool Enabled, bool IsDefault, GatewayConnector Connector, IReadOnlyList<string> CredentialNames,
    DateTimeOffset? TestPaymentSucceededAt, DateTimeOffset UpdatedAt)
{
    public static GatewayResponse From(PaymentGateway g) =>
        new(g.Id, g.Name, g.MerchantId, g.Mode, g.Enabled, g.IsDefault, GatewayConnector.Read(g.Connector), g.CredentialNames, g.TestPaymentSucceededAt, g.UpdatedAt);
}

/// <summary>What "Test connection" found.</summary>
internal sealed record TestConnectionResponse(bool Reachable, int StatusCode, string? Error, int Milliseconds);

/// <summary>Paying in the browser: send the payer to <see cref="RedirectUrl"/>.</summary>
internal sealed record PayResponse(Guid PaymentId, string Reference, string RedirectUrl);

/// <summary>Paying: the page's language.</summary>
internal sealed record PayRequest(string? Language);

/// <summary>A refund.</summary>
internal sealed record RefundRequest
{
    /// <summary>Amount; all that is left when left out.</summary>
    public decimal? AmountAed { get; init; }

    /// <summary>Why.</summary>
    [Required]
    [StringLength(PaymentRefund.ReasonMaxLength, MinimumLength = 2)]
    public string? Reason { get; init; }
}

/// <summary>Reconcile a gateway's day.</summary>
internal sealed record RunReconciliationRequest
{
    /// <summary>The gateway.</summary>
    [Required]
    public Guid? GatewayId { get; init; }

    /// <summary>The day (UTC).</summary>
    [Required]
    public DateOnly? Day { get; init; }
}

/// <summary>The request a payment is for.</summary>
internal sealed record RequestRef(Guid Id, string Number, string Title);

/// <summary>A line of a fee.</summary>
internal sealed record ReceiptLine(LocalizedTextDto Name, decimal AmountAed);

/// <summary>A try at paying.</summary>
internal sealed record AttemptResponse(string Reference, PaymentAttemptState State, string? GatewayStatus, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt);

/// <summary>A refund.</summary>
internal sealed record RefundResponse(Guid Id, decimal AmountAed, string Reason, RefundState State, DateTimeOffset CreatedAt);

/// <summary>A payment.</summary>
internal sealed record PaymentResponse(
    Guid Id, RequestRef? Request, ServiceRefResponse? Service, Incidents.PersonResponse? Payer, decimal AmountAed, decimal RefundedAed, string Currency,
    IReadOnlyList<ReceiptLine> Lines, PaymentTiming Timing, PaymentState State, string? Gateway, string? ReceiptNumber, string? TransactionId,
    DateTimeOffset? ExpiresAt, DateTimeOffset? PaidAt, DateTimeOffset CreatedAt, IReadOnlyList<AttemptResponse> Attempts, IReadOnlyList<RefundResponse> Refunds);

/// <summary>The e-receipt.</summary>
internal sealed record ReceiptResponse(
    string ReceiptNumber, LocalizedTextDto Organisation, RequestRef? Request, ServiceRefResponse? Service, Incidents.PersonResponse? Payer,
    decimal AmountAed, decimal RefundedAed, string Currency, IReadOnlyList<ReceiptLine> Lines, string? TransactionId, DateTimeOffset PaidAt, string? Gateway);
