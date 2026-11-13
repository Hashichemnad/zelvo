using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Commands;

/// <summary>
/// <c>dotnet Zelvo.Api.dll migrate</c>: applies database migrations with the owner's connection
/// (<c>ConnectionStrings:PostgresOwner</c>), then creates the bootstrap tenant when
/// <c>Tenancy:Bootstrap:Slug</c> is set. Runs before the API starts (a one-off Compose service).
/// </summary>
internal static partial class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        using var host = builder.Build();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Zelvo.Migrate");

        var owner = configuration.GetConnectionString("PostgresOwner");
        if (string.IsNullOrWhiteSpace(owner))
        {
            LogOwnerMissing(logger);
            return 2;
        }

        await DatabaseMigrator.MigrateAsync(owner);
        LogMigrated(logger);

        var slug = configuration["Tenancy:Bootstrap:Slug"];
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var name = new LocalizedText(configuration["Tenancy:Bootstrap:NameEn"], configuration["Tenancy:Bootstrap:NameAr"]);
            var (tenant, created) = await TenantProvisioning.EnsureTenantAsync(owner, slug, name);
            LogTenant(logger, tenant.Slug, tenant.Id, created);
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "ConnectionStrings:PostgresOwner is required to run migrations.")]
    private static partial void LogOwnerMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied.")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap tenant {Slug} ({TenantId}) ready; created now: {Created}.")]
    private static partial void LogTenant(ILogger logger, string slug, Guid tenantId, bool created);
}
