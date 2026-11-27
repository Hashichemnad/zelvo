namespace Zelvo.Api.Assets;

/// <summary>Registers asset management (module 18, task G3) in the API.</summary>
internal static class AssetSetup
{
    public static IServiceCollection AddZelvoAssets(this IServiceCollection services)
    {
        services.AddScoped<AssetExpiryAlerts>();
        services.AddSingleton<AssetJobs>();
        services.AddHostedService(sp => sp.GetRequiredService<AssetJobs>());
        return services;
    }

    public static RouteGroupBuilder MapZelvoAssetEndpoints(this RouteGroupBuilder group) => group.MapAssetEndpoints().MapAssetContractEndpoints();
}
