using Zelvo.Data.Localization;
using Zelvo.Search;

namespace Zelvo.Api.Knowledge;

/// <summary>Registers the knowledge base (module 7, task D1) in the API.</summary>
internal static class KnowledgeSetup
{
    public static IServiceCollection AddZelvoKnowledge(this IServiceCollection services)
    {
        services.AddSearchType(KnowledgeSearch.Type, requiredPermission: null);
        services.AddScoped<ISearchDocumentSource, KnowledgeSearchSource>();
        services.AddSingleton<IMissingTranslationSource, KnowledgeMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoKnowledgeEndpoints(this RouteGroupBuilder group) =>
        group.MapKnowledgeEndpoints().MapKnowledgePortalEndpoints();
}
