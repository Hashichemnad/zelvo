using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Payments;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Payments;

/// <summary>How a line of a reconciliation came out.</summary>
public enum ReconciliationResult
{
    /// <summary>Zelvo and the gateway agree.</summary>
    Matched,

    /// <summary>The gateway's status or amount differ from Zelvo's.</summary>
    Mismatched,

    /// <summary>The gateway could not be asked, or does not know the payment.</summary>
    NotFoundAtGateway,

    /// <summary>In the settlement file but not known to Zelvo.</summary>
    UnknownToZelvo,
}

/// <summary>A line of a reconciliation.</summary>
internal sealed record ReconciliationLine(
    ReconciliationResult Result, string? Reference, string? Receipt, string? TransactionId, decimal? ZelvoAmountAed, decimal? GatewayAmountAed, string? ZelvoState, string? GatewayStatus);

/// <summary>A reconciliation.</summary>
internal sealed record ReconciliationResponse(
    Guid Id, Guid GatewayId, string? Gateway, DateOnly Day, int Matched, int Mismatched, int UnknownToZelvo, decimal TotalAed, DateTimeOffset? FileImportedAt,
    DateTimeOffset UpdatedAt, IReadOnlyList<ReconciliationLine>? Lines)
{
    public static ReconciliationResponse From(PaymentReconciliation r, string? gateway, bool withLines) =>
        new(r.Id, r.GatewayId, gateway, r.Day, r.Matched, r.Mismatched, r.UnknownToZelvo, r.TotalAed, r.FileImportedAt, r.UpdatedAt,
            withLines ? JsonSerializer.Deserialize<List<ReconciliationLine>>(r.Lines, JsonSerializerOptions.Web) : null);
}

/// <summary>
/// The daily reconciliation (task E8; product owner decision 2026-11-25): every try with a
/// transaction of the day is asked of the gateway's status API and compared with Zelvo's state and
/// amount; the gateway's settlement file, when imported, adds the transactions Zelvo does not know.
/// </summary>
internal sealed class PaymentReconciler(ZelvoDbContext db, PaymentService payments, PaymentGatewayClient client)
{
    public async Task<PaymentReconciliation> ReconcileAsync(PaymentGateway gateway, DateOnly day, CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddDays(1);
        var connector = GatewayConnector.Read(gateway.Connector);
        var credentials = await payments.CredentialsAsync(gateway, cancellationToken);
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.GatewayId == gateway.Id && a.CreatedAt >= start && a.CreatedAt < end && (a.State == PaymentAttemptState.Paid || a.TransactionId != null))
            .Join(db.Payments.AsNoTracking().Where(p => !p.IsTest), a => a.PaymentId, p => p.Id, (a, p) => new { Attempt = a, Payment = p })
            .OrderBy(x => x.Attempt.CreatedAt).Take(5000).ToListAsync(cancellationToken);
        var lines = new List<ReconciliationLine>();
        foreach (var x in attempts)
        {
            var answer = await client.SendAsync(connector.Status, payments.Values(gateway, x.Payment, x.Attempt, LocalizedText.English, string.Empty), credentials, cancellationToken);
            var status = answer.Values.GetValueOrDefault("status");
            decimal? amount = decimal.TryParse(answer.Values.GetValueOrDefault("amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
                ? (connector.StatusAmountInMinorUnits ? a / 100m : a) : null;
            var gatewayPaid = status is not null && connector.StatusValues.Paid.Contains(status, StringComparer.OrdinalIgnoreCase);
            var zelvoPaid = x.Attempt.State == PaymentAttemptState.Paid;
            var result = !answer.Ok ? ReconciliationResult.NotFoundAtGateway
                : gatewayPaid == zelvoPaid && (amount is null || amount == x.Payment.AmountAed) ? ReconciliationResult.Matched
                : ReconciliationResult.Mismatched;
            lines.Add(new ReconciliationLine(result, x.Attempt.Reference, zelvoPaid ? x.Payment.ReceiptNumber : null, x.Attempt.TransactionId,
                x.Payment.AmountAed, amount, zelvoPaid ? "Paid" : x.Attempt.State.ToString(), status));
        }

        var row = await db.PaymentReconciliations.SingleOrDefaultAsync(r => r.GatewayId == gateway.Id && r.Day == day, cancellationToken);
        if (row is null)
        {
            row = new PaymentReconciliation { GatewayId = gateway.Id, Day = day };
            db.PaymentReconciliations.Add(row);
        }
        else
        {
            // Lines from a settlement file imported before are kept.
            lines.AddRange((JsonSerializer.Deserialize<List<ReconciliationLine>>(row.Lines, JsonSerializerOptions.Web) ?? []).Where(l => l.Result == ReconciliationResult.UnknownToZelvo));
        }

        Summarise(row, lines);
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    /// <summary>Matches a settlement file's lines (header first) to the day's lines; returns a problem, or null.</summary>
    public async Task<string?> ImportAsync(PaymentGateway gateway, PaymentReconciliation row, List<string[]> rows, CancellationToken cancellationToken)
    {
        var columns = GatewayConnector.Read(gateway.Connector).Settlement;
        if (columns is null)
        {
            return "The gateway's connector names no settlement file columns.";
        }

        if (rows.Count == 0)
        {
            return "The file is empty.";
        }

        var header = rows[0].Select(h => h.Trim()).ToList();
        int Column(string? name) => name is null ? -1 : header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        var (reference, transaction, amountColumn, status) = (Column(columns.Reference), Column(columns.TransactionId), Column(columns.Amount), Column(columns.Status));
        if (reference < 0 || amountColumn < 0)
        {
            return $"The file needs the columns '{columns.Reference}' and '{columns.Amount}'.";
        }

        var lines = JsonSerializer.Deserialize<List<ReconciliationLine>>(row.Lines, JsonSerializerOptions.Web) ?? [];
        lines.RemoveAll(l => l.Result == ReconciliationResult.UnknownToZelvo);
        var known = await db.PaymentAttempts.AsNoTracking().Where(a => a.GatewayId == gateway.Id).Select(a => a.Reference).ToListAsync(cancellationToken);
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        foreach (var line in rows.Skip(1))
        {
            string? Cell(int i) => i >= 0 && i < line.Length ? line[i].Trim() : null;
            var refValue = Cell(reference);
            decimal? amount = decimal.TryParse(Cell(amountColumn), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? (columns.AmountInMinorUnits ? a / 100m : a) : null;
            if (string.IsNullOrEmpty(refValue) || !knownSet.Contains(refValue))
            {
                lines.Add(new ReconciliationLine(ReconciliationResult.UnknownToZelvo, refValue, null, Cell(transaction), null, amount, null, Cell(status)));
                continue;
            }

            // A known line whose amount differs from the status API's view is marked.
            var index = lines.FindIndex(l => l.Reference == refValue);
            if (index >= 0 && amount is not null && lines[index].ZelvoAmountAed != amount)
            {
                lines[index] = lines[index] with { Result = ReconciliationResult.Mismatched, GatewayAmountAed = amount };
            }
        }

        row.FileImportedAt = DateTimeOffset.UtcNow;
        Summarise(row, lines);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    private static void Summarise(PaymentReconciliation row, List<ReconciliationLine> lines)
    {
        row.Matched = lines.Count(l => l.Result == ReconciliationResult.Matched);
        row.Mismatched = lines.Count(l => l.Result is ReconciliationResult.Mismatched or ReconciliationResult.NotFoundAtGateway);
        row.UnknownToZelvo = lines.Count(l => l.Result == ReconciliationResult.UnknownToZelvo);
        row.TotalAed = lines.Where(l => l.Result == ReconciliationResult.Matched && l.ZelvoState == "Paid").Sum(l => l.ZelvoAmountAed ?? 0);
        row.Lines = JsonSerializer.Serialize(lines, JsonSerializerOptions.Web);
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Payments' background work (task E8), in the API (releasing a paid request starts its approvals
/// on the embedded engine): asks the gateway about tries whose payer did not come back and whose
/// notification did not arrive, closes requests not paid in time, asks for fees due on completion,
/// and reconciles yesterday once per gateway. Every minute (<c>Payments:Jobs:Interval</c>); off
/// with <c>Payments:Jobs:Enabled=false</c>. One API instance per tenant at a time.
/// </summary>
internal sealed partial class PaymentJobs(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PaymentJobs> logger, TimeProvider time) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Payments:Jobs:Enabled", true))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var (id, slug) in await TenantsAsync(stoppingToken))
                {
                    await RunTenantAsync(id, slug, time.GetUtcNow(), stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(configuration.GetValue("Payments:Jobs:Interval", TimeSpan.FromMinutes(1)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tenant's payment work (tests call it directly); returns how many things changed.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "payments.jobs", "Payment jobs");
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await LockAsync(db, tenantId, "SELECT pg_try_advisory_lock(hashtext('zelvo.payment-jobs'), hashtext(@tenant))", cancellationToken))
            {
                return 0;
            }

            var changed = 0;
            var payments = services.GetRequiredService<PaymentService>();

            // Tries whose result did not come back: asked again from two minutes on, for two days.
            var stale = await db.PaymentAttempts.Where(a => a.State == PaymentAttemptState.Redirected && a.CreatedAt < now.AddMinutes(-2) && a.CreatedAt > now.AddDays(-2)
                    && (a.CheckedAt == null || a.CheckedAt < now.AddMinutes(-2)))
                .OrderBy(a => a.CreatedAt).Take(200).ToListAsync(cancellationToken);
            foreach (var attempt in stale)
            {
                if (await PaymentEndpoints.ConfirmAndContinueAsync(services, attempt, cancellationToken) != ConfirmOutcome.Pending)
                {
                    changed++;
                }
            }

            // Requests not paid in time are closed with the reason.
            var expired = await db.Payments.Where(p => p.State == PaymentState.AwaitingPayment && !p.IsTest && p.ExpiresAt != null && p.ExpiresAt <= now)
                .Select(p => p.Id).Take(200).ToListAsync(cancellationToken);
            var bundles = services.GetRequiredService<Catalog.BundleProgress>();
            foreach (var id in expired)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var payment = await db.Payments.FromSql($"SELECT *, xmin FROM payments WHERE id = {id} FOR UPDATE").SingleAsync(cancellationToken);
                if (payment.State != PaymentState.AwaitingPayment)
                {
                    continue;
                }

                await payments.ExpireAsync(payment, cancellationToken);
                var ticket = await db.Tickets.SingleAsync(t => t.Id == payment.TicketId, cancellationToken);
                await services.GetRequiredService<Zelvo.Tickets.Sla.SlaTracker>().UpdateAsync([ticket], now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await bundles.AfterPartChangedAsync(ticket.Id, cancellationToken);
                changed++;
            }

            // Fees due on completion, for requests resolved by tasks, sign-offs or elsewhere.
            var resolved = await db.Tickets.Where(t => t.Type == TicketType.ServiceRequest && t.State == TicketState.Resolved && t.ResolvedAt > now.AddDays(-30)
                    && !db.Payments.Any(p => p.TicketId == t.Id))
                .OrderBy(t => t.ResolvedAt).Take(200).ToListAsync(cancellationToken);
            foreach (var ticket in resolved)
            {
                if (await payments.DueOnCompletionAsync(ticket, cancellationToken) is not null)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    changed++;
                }
            }

            // Yesterday's reconciliation, once per gateway.
            var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
            var reconciler = services.GetRequiredService<PaymentReconciler>();
            foreach (var gateway in await db.PaymentGateways.AsNoTracking().Where(g => !db.PaymentReconciliations.Any(r => r.GatewayId == g.Id && r.Day == yesterday)).ToListAsync(cancellationToken))
            {
                await reconciler.ReconcileAsync(gateway, yesterday, cancellationToken);
                changed++;
            }

            return changed;
        }
        finally
        {
            await LockAsync(db, tenantId, "SELECT pg_advisory_unlock(hashtext('zelvo.payment-jobs'), hashtext(@tenant))", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> LockAsync(ZelvoDbContext db, Guid tenantId, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("tenant", tenantId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<List<(Guid Id, string Slug)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_payment_job_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The payment jobs failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Registers payments in the API.</summary>
internal static class PaymentSetup
{
    public static IServiceCollection AddZelvoPayments(this IServiceCollection services, IConfiguration configuration)
    {
        var options = PaymentOptions.From(configuration);
        services.AddSingleton(options);
        services.AddSingleton(_ => new PaymentGatewayClient(new Zelvo.Integrations.OutboundAddressPolicy(options.Network)));
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentReconciler>();
        services.AddSingleton<PaymentJobs>();
        services.AddHostedService(sp => sp.GetRequiredService<PaymentJobs>());
        return services;
    }
}
