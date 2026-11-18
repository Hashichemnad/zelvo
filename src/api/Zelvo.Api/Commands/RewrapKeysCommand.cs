using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Secrets;

namespace Zelvo.Api.Commands;

/// <summary>
/// <c>dotnet Zelvo.Api.dll rewrap-keys [--retire-active]</c>: wraps every tenant's data keys again
/// with the current key-encryption key (<c>Secrets</c> configuration), after a new master key file
/// was configured, after moving to Vault, or after the Transit key was rotated in Vault.
/// <c>--retire-active</c> also starts a new data key per tenant for new secrets. Uses the
/// application connection (<c>ConnectionStrings:Postgres</c>), tenant by tenant, so row-level
/// security applies; tenants are listed with the owner connection.
/// </summary>
internal static partial class RewrapKeysCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var retireActive = args.Contains("--retire-active", StringComparer.Ordinal);
        var builder = Host.CreateApplicationBuilder(args.Where(a => a != "--retire-active").ToArray());
        var app = builder.Configuration.GetConnectionString("Postgres");
        var owner = builder.Configuration.GetConnectionString("PostgresOwner");
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(owner))
        {
            Console.Error.WriteLine("ConnectionStrings:Postgres and ConnectionStrings:PostgresOwner are required.");
            return 2;
        }

        builder.Services.AddZelvoData(app);
        builder.Services.AddZelvoSecrets(builder.Configuration);
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Zelvo.RewrapKeys");

        List<Tenant> tenants;
        await using (var db = new ZelvoDbContext(DataServiceCollectionExtensions.CreateOptions(owner)))
        {
            tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
        }

        var failed = 0;
        foreach (var tenant in tenants)
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id, tenant.Slug);
            scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "rewrap-keys", "rewrap-keys");
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<DataKeyRewrap>().RewrapAsync(retireActive, CancellationToken.None);
                LogTenant(logger, tenant.Slug, result.Rewrapped, result.Unchanged);
            }
            catch (KeyEncryptionKeyException ex)
            {
                failed++;
                LogFailed(logger, tenant.Slug, ex.Message);
            }
        }

        return failed == 0 ? 0 : 1;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {Slug}: {Rewrapped} data keys rewrapped, {Unchanged} already current.")]
    private static partial void LogTenant(ILogger logger, string slug, int rewrapped, int unchanged);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tenant {Slug}: data keys could not be rewrapped: {Reason}")]
    private static partial void LogFailed(ILogger logger, string slug, string reason);
}
