using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Commands;

/// <summary>
/// <c>dotnet Zelvo.Api.dll migrate</c>: applies database migrations with the owner's connection
/// (<c>ConnectionStrings:PostgresOwner</c>), creates the bootstrap tenant when
/// <c>Tenancy:Bootstrap:Slug</c> is set, gives every tenant its built-in roles, and, when
/// <c>Identity:Provisioning:AdminUser</c> is set, sets up each tenant's Keycloak realm. Runs before
/// the API starts (a one-off Compose service).
/// </summary>
internal static partial class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddHttpClient();
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

        Tenant? bootstrap = null;
        var slug = configuration["Tenancy:Bootstrap:Slug"];
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var name = new LocalizedText(configuration["Tenancy:Bootstrap:NameEn"], configuration["Tenancy:Bootstrap:NameAr"]);
            var (tenant, created) = await TenantProvisioning.EnsureTenantAsync(owner, slug, name);
            bootstrap = tenant;
            LogTenant(logger, tenant.Slug, tenant.Id, created);
        }

        var tenants = await AllTenantsAsync(owner);
        foreach (var tenant in tenants)
        {
            await IdentityProvisioning.EnsureTenantIdentityAsync(owner, tenant);
        }

        LogRoles(logger, tenants.Count);

        var identity = configuration.GetSection(IdentityOptions.Section).Get<IdentityOptions>() ?? new IdentityOptions();
        var provisioning = configuration.GetSection(RealmProvisioningOptions.Section).Get<RealmProvisioningOptions>() ?? new RealmProvisioningOptions();
        if (string.IsNullOrEmpty(provisioning.AdminUser) || string.IsNullOrEmpty(provisioning.AdminPassword))
        {
            LogNoKeycloak(logger);
            return 0;
        }

        var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        var admin = KeycloakAdminClient.ForAdministrator(http, identity.BackchannelUrl, provisioning.AdminUser, provisioning.AdminPassword);
        var realms = new KeycloakRealmProvisioner(admin, identity, provisioning);

        await WaitForKeycloakAsync(realms, logger);
        foreach (var tenant in tenants)
        {
            var settings = await IdentitySettingsOfAsync(owner, tenant);
            await realms.EnsureRealmAsync(tenant, settings, CancellationToken.None);
            LogRealm(logger, tenant.Slug);
        }

        if (bootstrap is not null && !string.IsNullOrEmpty(provisioning.DevUsersPassword))
        {
            foreach (var (local, roleKey, firstName, lastName) in DevelopmentUsers.All)
            {
                var email = $"{local}@{provisioning.DevUsersDomain}";
                var subject = await realms.EnsureUserAsync(bootstrap, email, firstName, lastName, provisioning.DevUsersPassword, CancellationToken.None);
                await IdentityProvisioning.EnsureUserAsync(owner, bootstrap, subject, email, $"{firstName} {lastName}", [roleKey]);
            }

            LogDevUsers(logger, provisioning.DevUsersDomain);
        }

        return 0;
    }

    private static async Task WaitForKeycloakAsync(KeycloakRealmProvisioner realms, ILogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await realms.EnsureProvisionerAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (attempt < 30 && ex is HttpRequestException or KeycloakAdminException)
            {
                LogWaiting(logger, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(4));
            }
        }
    }

    private static async Task<List<Tenant>> AllTenantsAsync(string owner)
    {
        await using var db = new ZelvoDbContext(DataServiceCollectionExtensions.CreateOptions(owner));
        return await db.Tenants.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
    }

    private static async Task<Zelvo.Data.Identity.IdentitySettings> IdentitySettingsOfAsync(string owner, Tenant tenant)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenant.Id, tenant.Slug);
        await using var db = new ZelvoDbContext(DataServiceCollectionExtensions.CreateOptions(
            owner, new TenantConnectionInterceptor(tenantContext), new AuditSaveChangesInterceptor(tenantContext, new AuditContext())));
        return await db.IdentitySettings.AsNoTracking().SingleAsync(s => s.TenantId == tenant.Id);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "ConnectionStrings:PostgresOwner is required to run migrations.")]
    private static partial void LogOwnerMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied.")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap tenant {Slug} ({TenantId}) ready; created now: {Created}.")]
    private static partial void LogTenant(ILogger logger, string slug, Guid tenantId, bool created);

    [LoggerMessage(Level = LogLevel.Information, Message = "Built-in roles ready for {Count} tenants.")]
    private static partial void LogRoles(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity:Provisioning:AdminUser is not set: Keycloak realms were not set up.")]
    private static partial void LogNoKeycloak(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for Keycloak (attempt {Attempt}): {Reason}")]
    private static partial void LogWaiting(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak realm of tenant {Slug} ready.")]
    private static partial void LogRealm(ILogger logger, string slug);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Development users ready (one per persona, @{Domain}). Never set Identity:Provisioning:DevUsersPassword in production.")]
    private static partial void LogDevUsers(ILogger logger, string domain);
}
