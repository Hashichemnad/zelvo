using Zelvo.Api.Integrations.Bulk;
using Zelvo.Data.Localization;
using Zelvo.Search;

namespace Zelvo.Api.Catalog;

/// <summary>Registers the service catalog (task C2, module 2) in the API.</summary>
internal static class CatalogSetup
{
    public static IServiceCollection AddZelvoCatalog(this IServiceCollection services)
    {
        // Readers of each service document follow its access rules; no permission is needed to search.
        services.AddSearchType(CatalogSearch.Type, requiredPermission: null);
        services.AddScoped<ISearchDocumentSource, CatalogSearchSource>();
        services.AddSingleton<IMissingTranslationSource, CatalogMissingTranslations>();
        services.AddScoped<IBulkType, ServicesBulkType>();
        // Task C5: one way to make requests, for the portal, bundles and proactive services.
        services.AddScoped<ServiceRequestCreator>();
        services.AddScoped<BundleProgress>();
        services.AddScoped<ProactiveEngine>();
        services.AddSingleton<CatalogJobs>();
        services.AddHostedService(sp => sp.GetRequiredService<CatalogJobs>());
        return services;
    }

    public static RouteGroupBuilder MapZelvoCatalogEndpoints(this RouteGroupBuilder group) =>
        group.MapCatalogEndpoints().MapServiceEndpoints().MapServiceTemplateEndpoints().MapCatalogBrowseEndpoints().MapServiceRequestEndpoints().MapRequestTaskEndpoints()
            .MapProactiveEndpoints();
}
