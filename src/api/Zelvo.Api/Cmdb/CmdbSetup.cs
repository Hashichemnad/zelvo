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
        return services;
    }

    public static RouteGroupBuilder MapZelvoCmdbEndpoints(this RouteGroupBuilder group) =>
        group.MapCmdbSettingsEndpoints().MapCmdbItemEndpoints().MapCmdbTicketEndpoints();
}
