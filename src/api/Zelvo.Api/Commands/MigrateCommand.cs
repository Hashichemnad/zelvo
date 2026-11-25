using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Demo;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Api.Commands;

/// <summary>
/// <c>dotnet Zelvo.Api.dll migrate</c>: applies database migrations with the owner's connection
/// (<c>ConnectionStrings:PostgresOwner</c>), creates the bootstrap tenant when
/// <c>Tenancy:Bootstrap:Slug</c> is set, gives every tenant its built-in roles, and, when
/// <c>Identity:Provisioning:AdminUser</c> is set, sets up each tenant's Keycloak realm. Runs before
/// the API starts (a one-off Compose service).
/// In a demo installation (demo license) it creates the sample organisations instead of the
/// bootstrap tenant, refuses a database that holds any other tenant, adds the demo sign-in step to
/// their realms and runs the demo seeders that have not run yet.
/// </summary>
internal static partial class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddHttpClient();
        builder.Services.AddZelvoLicensing();
        using var host = builder.Build();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Zelvo.Migrate");

        var owner = configuration.GetConnectionString("PostgresOwner");
        if (string.IsNullOrWhiteSpace(owner))
        {
            LogOwnerMissing(logger);
            return 2;
        }

        var license = host.Services.GetRequiredService<ILicenseService>();
        var licenseStatus = await license.VerifyAsync();
        if (!licenseStatus.IsValid)
        {
            LogLicenseRefused(logger, licenseStatus.Reason ?? "unknown");
            return 3;
        }

        var demo = license.IsDemo();

        await DatabaseMigrator.MigrateAsync(owner);
        await Zelvo.Workflows.Engine.WorkflowEngineSetup.MigrateAsync(owner);
        LogMigrated(logger);

        Tenant? bootstrap = null;
        var slug = configuration["Tenancy:Bootstrap:Slug"];
        if (demo)
        {
            foreach (var organisation in DemoCatalog.Organisations)
            {
                var (tenant, created) = await TenantProvisioning.EnsureTenantAsync(owner, organisation.Slug, organisation.Name);
                LogTenant(logger, tenant.Slug, tenant.Id, created);
            }

            // Demo data is never mixed with real data: a demo installation has its own database.
            var others = string.Join(", ", (await AllTenantsAsync(owner)).Where(t => DemoCatalog.Find(t.Slug) is null).Select(t => t.Slug));
            if (others.Length > 0)
            {
                LogDemoMixed(logger, others);
                return 4;
            }
        }
        else if (!string.IsNullOrWhiteSpace(slug))
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
            if (demo && DemoCatalog.Find(tenant.Slug) is { } organisation)
            {
                await realms.EnsureDemoSignInAsync(identity.RealmFor(tenant.Slug),
                    organisation.Users.Select(organisation.UsernameOf).ToList(), CancellationToken.None);
            }

            LogRealm(logger, tenant.Slug);
        }

        if (demo)
        {
            return await SeedDemoAsync(configuration, identity, admin, tenants, logger);
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

    /// <summary>Runs the demo seeders that have not run yet, as the application role (row-level security applies).</summary>
    private static async Task<int> SeedDemoAsync(
        IConfiguration configuration, IdentityOptions identity, IKeycloakAdmin admin, IReadOnlyList<Tenant> tenants, ILogger logger)
    {
        var app = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(app))
        {
            LogDemoNeedsAppConnection(logger);
            return 2;
        }

        await using var provider = DemoServices(configuration, app, identity, admin);
        var seeding = provider.GetRequiredService<DemoSeeding>();
        foreach (var tenant in tenants)
        {
            if (DemoCatalog.Find(tenant.Slug) is { } organisation)
            {
                var ran = await seeding.SeedPendingAsync(organisation, tenant, CancellationToken.None);
                var names = ran.Count == 0 ? "none" : string.Join(", ", ran);
                LogDemoSeeded(logger, tenant.Slug, names);
            }
        }

        return 0;
    }

    /// <summary>The services the demo seeders run with in <c>migrate</c> (no web host, no worker).</summary>
    internal static ServiceProvider DemoServices(IConfiguration configuration, string appConnection, IdentityOptions identity, IKeycloakAdmin admin)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddZelvoData(appConnection);
        services.AddScoped<TenantSettingsStore>();
        // Sample records go to the search index through the outbox, like any change.
        services.AddScoped<Zelvo.Search.ISearchIndexer, Zelvo.Search.SearchIndexer>();
        services.AddSingleton(admin);
        services.AddSingleton(Options.Create(identity));
        services.AddZelvoDemo();
        return services.BuildServiceProvider();
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

    [LoggerMessage(Level = LogLevel.Critical, Message = "License verification failed; migrate refuses to run: {Reason}")]
    private static partial void LogLicenseRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "This is a demo installation, but the database also holds tenants that are not sample organisations ({Slugs}). Demo data is never mixed with real data: give the demo installation its own database.")]
    private static partial void LogDemoMixed(ILogger logger, string slugs);

    [LoggerMessage(Level = LogLevel.Error, Message = "ConnectionStrings:Postgres (the application role) is required to seed the demo.")]
    private static partial void LogDemoNeedsAppConnection(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo sample data of {Slug} ready; seeders run now: {Seeders}.")]
    private static partial void LogDemoSeeded(ILogger logger, string slug, string seeders);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied.")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {Slug} ({TenantId}) ready; created now: {Created}.")]
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
