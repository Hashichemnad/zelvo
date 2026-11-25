using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Ai.Features;

/// <summary>
/// Before each save: queues AI jobs for the changes, in the same transaction, so a job exists if
/// and only if its change was saved. A new incident gets triage, duplicate and sentiment jobs; a
/// requester's public message gets a sentiment job. Only features that are on are queued.
/// </summary>
public sealed class AiJobInterceptor(ITenantContext tenantContext) : SaveChangesInterceptor, IZelvoSaveChangesInterceptor
{
    private bool _collecting;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        QueueAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await QueueAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task QueueAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (_collecting || context is not ZelvoDbContext db || tenantContext.TenantId is not { } tenantId)
        {
            return;
        }

        db.ChangeTracker.DetectChanges();
        var incidents = db.ChangeTracker.Entries<Ticket>()
            .Where(e => e.State == EntityState.Added && e.Entity.Type == TicketType.Incident).Select(e => e.Entity).ToList();
        var messages = db.ChangeTracker.Entries<TicketComment>()
            .Where(e => e.State == EntityState.Added && !e.Entity.ByStaff && e.Entity.Visibility == CommentVisibility.Public).Select(e => e.Entity).ToList();
        if (incidents.Count == 0 && messages.Count == 0)
        {
            return;
        }

        _collecting = true;
        try
        {
            var on = await db.AiFeatureSettings.AsNoTracking().Where(f => f.Enabled).Select(f => f.Feature).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (on.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            void Queue(AiJobKind kind, Guid ticketId, Guid? commentId) =>
                db.AiJobs.Add(new AiJob { TenantId = tenantId, Kind = kind, TicketId = ticketId, CommentId = commentId, CreatedAt = now, NextAttemptAt = now });

            foreach (var ticket in incidents)
            {
                if (on.Contains(AiFeatures.Triage))
                {
                    Queue(AiJobKind.Triage, ticket.Id, null);
                }

                if (on.Contains(AiFeatures.Duplicates))
                {
                    Queue(AiJobKind.Duplicates, ticket.Id, null);
                }

                if (on.Contains(AiFeatures.Sentiment))
                {
                    Queue(AiJobKind.Sentiment, ticket.Id, null);
                }
            }

            if (on.Contains(AiFeatures.Sentiment) && messages.Count > 0)
            {
                // Only incidents: the workspace shows sentiment for them (service requests follow with C4).
                var ticketIds = messages.Select(m => m.TicketId).Distinct().ToList();
                var known = incidents.Select(t => t.Id).ToHashSet();
                var incidentIds = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id) && t.Type == TicketType.Incident)
                    .Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var message in messages.Where(m => incidentIds.Contains(m.TicketId) || known.Contains(m.TicketId)))
                {
                    Queue(AiJobKind.Sentiment, message.TicketId, message.Id);
                }
            }
        }
        finally
        {
            _collecting = false;
        }
    }
}

/// <summary>
/// Worker job: runs due AI jobs tenant by tenant. Each job holds a lease while it runs, so
/// several workers can share the queue. When the AI is off or the limit is reached, the job is
/// done without a call; a failed provider call is tried again later (up to <see cref="MaxAttempts"/>).
/// Changes are made with the system as actor (<c>ai.&lt;kind&gt;</c>).
/// </summary>
public sealed partial class AiJobRunner(IServiceScopeFactory scopes, AiOptions options, ILogger<AiJobRunner> logger, TimeProvider time) : BackgroundService
{
    /// <summary>Attempts before a job is given up.</summary>
    public const int MaxAttempts = 4;

    private const int BatchSize = 20;

    /// <summary>Runs every due job now; returns how many ran.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var ran = 0;
        foreach (var (tenantId, slug) in await TenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            int count;
            do
            {
                count = await RunBatchAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
                ran += count;
            }
            while (count == BatchSize);
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var before = time.GetUtcNow() - options.JobRetention;
            await db.Database.SqlQuery<long>($"SELECT zelvo_purge_ai_jobs({before}) AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
        }

        return ran;
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
                await Task.Delay(options.JobInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<List<(Guid, string)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_ai_job_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    private async Task<int> RunBatchAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        List<AiJob> jobs;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var now = time.GetUtcNow();
            var lease = now.AddMinutes(5);
            jobs = await db.AiJobs.FromSql($"""
                UPDATE ai_jobs SET lease_until = {lease}, attempts = attempts + 1
                WHERE id IN (
                    SELECT id FROM ai_jobs
                    WHERE status = 'Pending' AND next_attempt_at <= {now} AND (lease_until IS NULL OR lease_until < {now})
                    ORDER BY next_attempt_at, id
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var job in jobs.OrderBy(j => j.Id))
        {
            string? error = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
                scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, $"ai.{job.Kind.ToString().ToLowerInvariant()}", "AI");
                await RunAsync(scope.ServiceProvider, job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                LogJobFailed(logger, exception, job.Id, job.Kind);
            }

            await FinishAsync(tenantId, slug, job, error, cancellationToken).ConfigureAwait(false);
        }

        return jobs.Count;
    }

    // Provider failures (not "AI off" or the limit) throw, so the job is tried again later.
    private static async Task RunAsync(IServiceProvider services, AiJob job, CancellationToken cancellationToken)
    {
        var gateway = services.GetRequiredService<IAiGateway>();
        var feature = job.Kind switch
        {
            AiJobKind.Triage => AiFeatures.Triage,
            AiJobKind.Duplicates => AiFeatures.Duplicates,
            _ => AiFeatures.Sentiment,
        };
        var availability = (await gateway.AvailabilityAsync(cancellationToken).ConfigureAwait(false)).Single(a => a.Feature == feature);
        if (!availability.Available)
        {
            return;
        }

        AiResult? call;
        switch (job.Kind)
        {
            case AiJobKind.Triage:
                var triage = services.GetRequiredService<TicketTriage>();
                await triage.SuggestAsync(job.TicketId, cancellationToken).ConfigureAwait(false);
                call = triage.LastCall;
                break;
            case AiJobKind.Duplicates:
                var duplicates = services.GetRequiredService<DuplicateFinder>();
                await duplicates.FindAsync(job.TicketId, cancellationToken).ConfigureAwait(false);
                call = duplicates.LastCall;
                break;
            default:
                var sentiment = services.GetRequiredService<SentimentReader>();
                await sentiment.ReadAsync(job.TicketId, job.CommentId, cancellationToken).ConfigureAwait(false);
                call = sentiment.LastCall;
                break;
        }

        // The provider failed (not "AI off", the limit or an answer that did not fit): try again later.
        if (call is { Succeeded: false, ErrorCode: not ("ai-off" or "limit-reached" or "invalid-json") })
        {
            throw new AiJobRetryException($"{call.ErrorCode}: {call.Error}");
        }
    }

    private async Task FinishAsync(Guid tenantId, string slug, AiJob job, string? error, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var now = time.GetUtcNow();
        var status = error is null ? AiJobStatus.Done : job.Attempts >= MaxAttempts ? AiJobStatus.Failed : AiJobStatus.Pending;
        // Waits of 1, 5 and 30 minutes between attempts.
        var next = now + (job.Attempts switch { <= 1 => TimeSpan.FromMinutes(1), 2 => TimeSpan.FromMinutes(5), _ => TimeSpan.FromMinutes(30) });
        var message = error is null ? null : AiText.Cut(error, AiJob.ErrorMaxLength);
        DateTimeOffset? finished = status == AiJobStatus.Pending ? null : now;
        await db.AiJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(set => set
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.LastError, message)
            .SetProperty(j => j.NextAttemptAt, next)
            .SetProperty(j => j.FinishedAt, finished), cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Running AI jobs failed; trying again shortly.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI job {Id} ({Kind}) failed; it is tried again later.")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, long id, AiJobKind kind);
}

/// <summary>An AI job's provider call failed and the job should be tried again.</summary>
public sealed class AiJobRetryException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AiJobRetryException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public AiJobRetryException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public AiJobRetryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
