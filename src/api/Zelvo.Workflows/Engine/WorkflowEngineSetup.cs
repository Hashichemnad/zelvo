using Elsa.Extensions;
using Elsa.Features.Services;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zelvo.Workflows.Engine;

/// <summary>
/// Embeds Elsa Workflows 3 (product owner decision 2026-11-13, confirmed by the C3 spike) with
/// PostgreSQL persistence in the schema <c>Elsa</c> of Zelvo's database. Only the engine is used:
/// no Elsa Studio and no Elsa HTTP API; Zelvo's own bilingual builder edits the rules.
/// </summary>
public static class WorkflowEngineSetup
{
    /// <summary>Elsa's schema.</summary>
    public const string Schema = "Elsa";

    /// <summary>
    /// Adds the engine for the application role. Elsa's connections set the tenant from
    /// <see cref="ElsaTenant"/>; its migrations are not run here (the <c>migrate</c> step runs them
    /// with the owner's connection, <see cref="MigrateAsync"/>).
    /// </summary>
    public static IServiceCollection AddZelvoWorkflowEngine(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddElsa(services, connectionString, runMigrations: false);
        // Elsa starts in the background, so the API starts while the database is briefly away.
        // Elsa's graceful drain is not needed either: approval steps run to their next wait within
        // the request that moves them, so nothing is left running when the host stops.
        foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
            && (d.ImplementationType == typeof(Elsa.Common.Multitenancy.HostedServices.ActivateTenants)
                || d.ImplementationType == typeof(Elsa.Workflows.Runtime.HostedServices.DrainOrchestratorHostedService))).ToList())
        {
            services.Remove(hosted);
        }

        services.AddSingleton<ElsaActivation>();
        services.AddHostedService(sp => sp.GetRequiredService<ElsaActivation>());
        services.AddSingleton<IApprovalEngine, ElsaApprovalEngine>();
        return services;
    }

    /// <summary>
    /// Creates or upgrades Elsa's tables with the owner's connection, then calls
    /// <c>zelvo_secure_elsa_tables()</c> (migration <c>Workflows</c>), which adds the tenant column,
    /// forced row-level security and the application role's grants to every Elsa table. Safe to
    /// run repeatedly.
    /// </summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        AddElsa(builder.Services, ownerConnectionString, runMigrations: true);
        using (var host = builder.Build())
        {
            // Elsa applies its migrations while it starts.
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT zelvo_secure_elsa_tables()", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddElsa(IServiceCollection services, string connectionString, bool runMigrations)
    {
        var module = services.CreateModule();
        module.UseWorkflows(workflows => workflows.WithDefaultWorkflowExecutionPipeline().WithDefaultActivityExecutionPipeline());
        module.UseWorkflowManagement(management => management.UseEntityFrameworkCore(ef => Configure(ef, connectionString, runMigrations)));
        module.UseWorkflowRuntime(runtime =>
        {
            runtime.UseEntityFrameworkCore(ef => Configure(ef, connectionString, runMigrations));
            // Elsa's default lock is a file under the working directory, which Zelvo's read-only,
            // non-root containers cannot write; PostgreSQL advisory locks also hold across instances.
            runtime.DistributedLockProvider = _ => new Medallion.Threading.Postgres.PostgresDistributedSynchronizationProvider(connectionString);
        });
        module.AddActivity<ApprovalLevels>();
        module.AddWorkflow<ApprovalWorkflow>();
        module.Apply();
    }

    private static void Configure<TFeature, TContext>(PersistenceFeatureBase<TFeature, TContext> ef, string connectionString, bool runMigrations)
        where TFeature : PersistenceFeatureBase<TFeature, TContext>
        where TContext : ElsaDbContextBase
    {
        ef.UsePostgreSql(connectionString);
        var inner = ef.DbContextOptionsBuilder;
        ef.DbContextOptionsBuilder = (sp, options) =>
        {
            inner?.Invoke(sp, options);
            options.AddInterceptors(new ElsaTenantConnectionInterceptor());
        };
        ef.RunMigrations = runMigrations;
    }
}
