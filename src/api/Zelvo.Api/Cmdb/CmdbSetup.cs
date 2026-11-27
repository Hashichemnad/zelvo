using Zelvo.Data.Localization;
using Zelvo.Search;

namespace Zelvo.Api.Cmdb;

/// <summary>Registers the CMDB (module 18, task G1) in the API.</summary>
internal static class CmdbSetup
{
    public static IServiceCollection AddZelvoCmdb(this IServiceCollection services)
    {
        services.AddSearchType(CmdbSearch.Type, requiredPermission: Identity.Permissions.CmdbRead);
        services.AddScoped<ISearchDocumentSource, CmdbSearchSource>();
        services.AddSingleton<IMissingTranslationSource, CmdbMissingTranslations>();
        // Task G2: import, discovery and reconciliation.
        services.AddScoped<CmdbReconciler>();
        services.AddScoped<CmdbMerger>();
        services.AddScoped<CmdbDiscoveryRunner>();
        services.AddScoped<Integrations.Bulk.IBulkType, ConfigurationItemsBulkType>();
        services.AddSingleton<CmdbDiscoveryJobs>();
        services.AddHostedService(sp => sp.GetRequiredService<CmdbDiscoveryJobs>());
        return services;
    }

    public static RouteGroupBuilder MapZelvoCmdbEndpoints(this RouteGroupBuilder group) =>
        group.MapCmdbSettingsEndpoints().MapCmdbItemEndpoints().MapCmdbTicketEndpoints().MapCmdbDiscoveryEndpoints();
}
