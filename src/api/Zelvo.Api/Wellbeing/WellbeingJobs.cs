using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Wellbeing;

/// <summary>
/// Works out agents' wellbeing figures every hour (module 21, task K4) for tenants that turned
/// wellbeing on (<c>zelvo_wellbeing_job_tenants()</c>), under an advisory lock per tenant so
/// several API instances never do it twice: yesterday's and today's figures, alerts raised and
/// ended, and figures older than 90 days removed.
/// </summary>
internal sealed partial class WellbeingJobs(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<WellbeingJobs> logger, TimeProvider time) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Wellbeing:Jobs:Enabled", true))
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
                await Task.Delay(configuration.GetValue("Wellbeing:Jobs:Interval", TimeSpan.FromHours(1)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tenant's run (tests call it directly); returns how many agents were looked at, or -1 when another instance holds the lock.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "wellbeing.jobs", "Wellbeing jobs");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await LockAsync(db, tenantId, "SELECT pg_try_advisory_lock(hashtext('zelvo.wellbeing-jobs'), hashtext(@tenant))", cancellationToken))
            {
                return -1;
            }

            try
            {
                return await scope.ServiceProvider.GetRequiredService<WellbeingCalculator>().RunAsync(now, cancellationToken);
            }
            finally
            {
                await LockAsync(db, tenantId, "SELECT pg_advisory_unlock(hashtext('zelvo.wellbeing-jobs'), hashtext(@tenant))", CancellationToken.None);
            }
        }
        finally
        {
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
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_wellbeing_job_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The wellbeing jobs failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Registers agent wellbeing in the API.</summary>
internal static class WellbeingSetup
{
    public static IServiceCollection AddZelvoWellbeing(this IServiceCollection services)
    {
        services.AddScoped<WellbeingCalculator>();
        services.AddSingleton<WellbeingJobs>();
        services.AddHostedService(sp => sp.GetRequiredService<WellbeingJobs>());
        return services;
    }
}
