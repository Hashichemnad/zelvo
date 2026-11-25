using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Privacy;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Privacy;

/// <summary>Which records a retention period reaches (task F5).</summary>
internal sealed class PrivacyRetention(ZelvoDbContext db)
{
    /// <summary>Closed tickets of <paramref name="type"/> closed more than <paramref name="days"/> ago and not anonymised yet.</summary>
    public IQueryable<Ticket> DueTickets(TicketType type, int days, DateTimeOffset now)
    {
        var before = now.AddDays(-days);
        return db.Tickets.AsNoTracking().Where(t => t.Type == type && t.State == TicketState.Closed && t.ClosedAt != null && t.ClosedAt < before && t.AnonymisedAt == null);
    }

    /// <summary>
    /// Requester accounts unused for more than <paramref name="days"/>: no role but requester, no
    /// ticket that is not closed, no open data subject request, and not a system account.
    /// </summary>
    public IQueryable<UserAccount> DueAccounts(int days, DateTimeOffset now)
    {
        var before = now.AddDays(-days);
        return db.Users.AsNoTracking()
            .Where(u => u.ErasedAt == null && !u.Subject.StartsWith("system:") && (u.LastSeenAt ?? u.CreatedAt) < before)
            .Where(u => !u.Roles.Any(r => r.Role!.Key != BuiltInRoles.Requester))
            .Where(u => !db.Tickets.Any(t => t.RequesterId == u.Id && t.State != TicketState.Closed))
            .Where(u => !db.Tickets.Any(t => t.RequesterId == u.Id && t.ClosedAt != null && t.ClosedAt >= before))
            .Where(u => !db.DataSubjectRequests.Any(r => r.SubjectId == u.Id && (r.State == DataSubjectRequestState.Received || r.State == DataSubjectRequestState.InProgress)));
    }
}

/// <summary>
/// The privacy jobs (task F5), in the API (erasure deletes the person's Keycloak user): once an
/// hour (<c>Privacy:Jobs:Interval</c>; off with <c>Privacy:Jobs:Enabled=false</c>) they
/// anonymise closed tickets whose retention period ended and erase requester accounts unused for
/// longer than the tenant allows, at most 200 of each per tenant and run, each audited. One API
/// instance per tenant at a time (advisory lock).
/// </summary>
internal sealed partial class PrivacyJobs(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PrivacyJobs> logger, TimeProvider time) : BackgroundService
{
    private const int Batch = 200;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Privacy:Jobs:Enabled", true))
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
                await Task.Delay(configuration.GetValue("Privacy:Jobs:Interval", TimeSpan.FromHours(1)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tenant's retention work (tests call it directly); returns how many records were anonymised or erased.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "privacy.jobs", "Privacy jobs");
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await LockAsync(db, tenantId, "SELECT pg_try_advisory_lock(hashtext('zelvo.privacy-jobs'), hashtext(@tenant))", cancellationToken))
            {
                return 0;
            }

            var settings = await services.GetRequiredService<TenantSettingsStore>().GetAsync(PrivacySettings.Retention, cancellationToken);
            var retention = services.GetRequiredService<PrivacyRetention>();
            var eraser = services.GetRequiredService<PersonalDataEraser>();
            var changed = 0;
            foreach (var (type, days) in settings.TicketDays.OrderBy(p => p.Key))
            {
                var ids = await retention.DueTickets(type, days, now).OrderBy(t => t.ClosedAt).Select(t => t.Id).Take(Batch).ToListAsync(cancellationToken);
                if (ids.Count > 0)
                {
                    changed += (await eraser.AnonymiseTicketsAsync(ids, $"retention:{type}:{days}", cancellationToken)).Tickets;
                }
            }

            if (settings.InactiveAccountDays is { } accountDays)
            {
                var ids = await retention.DueAccounts(accountDays, now).OrderBy(u => u.LastSeenAt ?? u.CreatedAt).Select(u => u.Id).Take(Batch).ToListAsync(cancellationToken);
                foreach (var id in ids)
                {
                    var person = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, cancellationToken);
                    await eraser.ErasePersonAsync(person, $"retention:inactive:{accountDays}", null, cancellationToken);
                    changed++;
                }
            }

            return changed;
        }
        finally
        {
            await LockAsync(db, tenantId, "SELECT pg_advisory_unlock(hashtext('zelvo.privacy-jobs'), hashtext(@tenant))", CancellationToken.None);
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
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_privacy_job_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The privacy jobs failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Registers privacy in the API.</summary>
internal static class PrivacySetup
{
    public static IServiceCollection AddZelvoPrivacy(this IServiceCollection services)
    {
        services.AddScoped<PersonalDataEraser>();
        services.AddScoped<PersonalDataExport>();
        services.AddScoped<PrivacyRetention>();
        services.AddSingleton<PrivacyJobs>();
        services.AddHostedService(sp => sp.GetRequiredService<PrivacyJobs>());
        return services;
    }
}
