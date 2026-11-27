using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Integrations;

namespace Zelvo.Tickets.Sla;

/// <summary>
/// Worker job: SLA timers (module 8, task B3). Every few seconds it looks at the clocks whose next
/// threshold or breach has come, tenant by tenant, and lets <see cref="SlaTracker"/> bring them up
/// to date: thresholds reached are recorded once as alerts (audited, sent as webhook events,
/// delivered by notifications in B5), breaches marked, and the next look scheduled. Changes to
/// policies and calendars ask for a look at every open clock of the tenant.
/// </summary>
public sealed partial class SlaTimerJob(IServiceScopeFactory scopes, ILogger<SlaTimerJob> logger, TimeProvider time) : BackgroundService
{
    private const int BatchSize = 100;

    /// <summary>How often the job runs.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Looks at every clock due by <paramref name="now"/> (default: the current time); returns how many tickets were updated.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken, DateTimeOffset? now = null)
    {
        var at = now ?? time.GetUtcNow();
        var updated = 0;
        foreach (var (tenantId, slug) in await TenantsAsync(at, cancellationToken).ConfigureAwait(false))
        {
            updated += await RunTenantAsync(tenantId, slug, at, cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>Looks at one tenant's due clocks.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var updated = 0;
        int count;
        do
        {
            count = await RunBatchAsync(tenantId, slug, now, cancellationToken).ConfigureAwait(false);
            updated += count;
        }
        while (count == BatchSize);
        return updated;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<List<(Guid Id, string Slug)>> TenantsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_tenants_with_due_slas(@due)", connection);
        command.Parameters.AddWithValue("due", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    private async Task<int> RunBatchAsync(Guid tenantId, string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "sla.timer", "SLA timer");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var tracker = scope.ServiceProvider.GetRequiredService<SlaTracker>();

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        // Tickets first, then their clocks (the tracker's order); tickets someone is saving right now are skipped.
        var tickets = await db.Tickets
            .FromSql($"""
                SELECT *, xmin FROM tickets
                WHERE id IN (SELECT ticket_id FROM ticket_slas WHERE next_check_at <= {now})
                ORDER BY id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tickets.Count == 0)
        {
            return 0;
        }

        var ids = tickets.Select(t => t.Id).ToList();
        await db.Incidents.Where(i => ids.Contains(i.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);
        await db.CustomerVoiceRecords.Where(v => ids.Contains(v.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);
        await db.Problems.Where(p => ids.Contains(p.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);
        await db.CustomRecords.Where(r => ids.Contains(r.TicketId)).LoadAsync(cancellationToken).ConfigureAwait(false);
        await tracker.UpdateAsync(tickets, now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogUpdated(logger, tickets.Count, slug);
        return tickets.Count;
    }

    /// <summary>
    /// Asks for a look at every running or paused clock of the current tenant (after a change to
    /// policies, calendars, customer groups or group calendars), so the change applies within moments.
    /// </summary>
    public static Task<int> RecheckAllAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlAsync($"UPDATE ticket_slas SET next_check_at = now() WHERE state <> 'Stopped'", cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Updating SLA timers failed; trying again shortly.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated the SLA timers of {Count} tickets of tenant {Tenant}.")]
    private static partial void LogUpdated(ILogger logger, int count, string tenant);
}

/// <summary>
/// Webhook events of SLAs: <c>ticket.sla.threshold</c> for every threshold a ticket reaches and
/// <c>ticket.sla.breached</c> when a target is passed. Payloads hold ids and numbers only.
/// </summary>
public sealed class SlaWebhookEvents : IWebhookEventSource
{
    /// <summary>A ticket reached an escalation threshold of an SLA target.</summary>
    public const string Threshold = WebhookEventTypes.TicketSlaThreshold;

    /// <summary>A ticket passed an SLA target.</summary>
    public const string Breached = WebhookEventTypes.TicketSlaBreached;

    /// <inheritdoc />
    public Task<IReadOnlyList<WebhookEvent>> CollectAsync(ZelvoDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var events = new List<WebhookEvent>();
        foreach (var alert in context.ChangeTracker.Entries<SlaAlert>().Where(e => e.State == EntityState.Added).Select(e => e.Entity))
        {
            var ticket = context.ChangeTracker.Entries<Ticket>().FirstOrDefault(t => t.Entity.Id == alert.TicketId)?.Entity;
            var data = new SlaEventData(alert.TicketId, ticket?.Number, ticket?.Type, alert.Metric, alert.Percent, alert.ReachedAt);
            events.Add(new WebhookEvent(Threshold, data));
            if (alert.Percent == 100)
            {
                events.Add(new WebhookEvent(Breached, data));
            }
        }

        return Task.FromResult<IReadOnlyList<WebhookEvent>>(events);
    }
}

/// <summary><c>data</c> of SLA events.</summary>
/// <param name="TicketId">The ticket (<c>GET /api/v1/incidents/{id}</c> for incidents).</param>
/// <param name="Number">Its number, for example <c>INC-000123</c>.</param>
/// <param name="RecordType">Its record type.</param>
/// <param name="Metric">Response or Resolution.</param>
/// <param name="Percent">Share of the target reached; 100 is the breach.</param>
/// <param name="ReachedAt">When it was reached.</param>
public sealed record SlaEventData(Guid TicketId, string? Number, TicketType? RecordType, SlaMetric Metric, int Percent, DateTimeOffset ReachedAt);

/// <summary>Ready-made weekly patterns for new calendars (GOV-7). Names are translated by the web app from the key.</summary>
/// <param name="Key">Stable key.</param>
/// <param name="Periods">Working periods of the week.</param>
public sealed record CalendarTemplate(string Key, IReadOnlyList<(DayOfWeek Day, WorkingPeriod Period)> Periods)
{
    private static (DayOfWeek, WorkingPeriod) P(DayOfWeek day, int startHour, int startMinute, int endHour, int endMinute) =>
        (day, new WorkingPeriod(startHour * 60 + startMinute, endHour * 60 + endMinute));

    /// <summary>
    /// UAE federal government: Monday–Thursday 07:30–15:30, Friday 07:30–12:00, Saturday and Sunday
    /// off (the 2022 working week).
    /// </summary>
    public static CalendarTemplate UaeGovernment { get; } = new("uae-government",
    [
        P(DayOfWeek.Monday, 7, 30, 15, 30), P(DayOfWeek.Tuesday, 7, 30, 15, 30), P(DayOfWeek.Wednesday, 7, 30, 15, 30),
        P(DayOfWeek.Thursday, 7, 30, 15, 30), P(DayOfWeek.Friday, 7, 30, 12, 0),
    ]);

    /// <summary>UAE private sector: Monday–Friday 09:00–18:00, Saturday and Sunday off.</summary>
    public static CalendarTemplate UaePrivate { get; } = new("uae-private",
    [
        P(DayOfWeek.Monday, 9, 0, 18, 0), P(DayOfWeek.Tuesday, 9, 0, 18, 0), P(DayOfWeek.Wednesday, 9, 0, 18, 0),
        P(DayOfWeek.Thursday, 9, 0, 18, 0), P(DayOfWeek.Friday, 9, 0, 18, 0),
    ]);

    /// <summary>Around the clock, every day.</summary>
    public static CalendarTemplate AllDay { get; } = new("all-day",
        [.. Enum.GetValues<DayOfWeek>().Select(d => (d, new WorkingPeriod(0, BusinessCalendarPeriod.MinutesPerDay)))]);

    /// <summary>Every template.</summary>
    public static IReadOnlyList<CalendarTemplate> All { get; } = [UaeGovernment, UaePrivate, AllDay];
}
