using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zelvo.Api.Demo;

/// <summary>Registers demo mode: the seeding framework, task D's seeders and "Reset demo".</summary>
internal static class DemoServiceCollectionExtensions
{
    public static IServiceCollection AddZelvoDemo(this IServiceCollection services)
    {
        services.TryAddSingleton<DemoSeeding>();
        services.TryAddScoped<DemoReset>();
        services.TryAddSingleton<DemoResetJobs>();
        // Task G2: the discovery seeder reconciles its sample records like a real source (also in migrate).
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<Cmdb.CmdbReconciler>();
        services.AddDemoSeeder<DemoOrganisationSeeder>();
        services.AddDemoSeeder<DemoUsersSeeder>();
        services.AddDemoSeeder<DemoIncidentsSeeder>();
        services.AddDemoSeeder<DemoDirectorySeeder>();
        services.AddDemoSeeder<DemoKnowledgeSeeder>();
        services.AddDemoSeeder<DemoSlaSeeder>();
        services.AddDemoSeeder<DemoNotificationsSeeder>();
        services.AddDemoSeeder<DemoFormsSeeder>();
        services.AddDemoSeeder<DemoWorkspaceSeeder>();
        services.AddDemoSeeder<DemoChannelsSeeder>();
        services.AddDemoSeeder<DemoServiceCatalogSeeder>();
        services.AddDemoSeeder<DemoCustomerVoiceSeeder>();
        services.AddDemoSeeder<DemoAiSeeder>();
        services.AddDemoSeeder<DemoBrandingSeeder>();
        services.AddDemoSeeder<DemoWorkflowsSeeder>();
        services.AddDemoSeeder<DemoFulfilmentSeeder>();
        services.AddDemoSeeder<DemoReportingSeeder>();
        services.AddDemoSeeder<DemoBundlesSeeder>();
        services.AddDemoSeeder<DemoPaymentsSeeder>();
        services.AddDemoSeeder<DemoMessagingSeeder>();
        services.AddDemoSeeder<DemoConnectorsSeeder>();
        services.AddDemoSeeder<DemoPrivacySeeder>();
        services.AddDemoSeeder<DemoCmdbSeeder>();
        services.AddDemoSeeder<DemoDiscoverySeeder>();
        services.AddDemoSeeder<DemoAssetsSeeder>();
        services.AddDemoSeeder<DemoPlainLanguageRulesSeeder>();
        services.AddDemoSeeder<DemoWellbeingSeeder>();
        services.AddDemoSeeder<DemoProblemsSeeder>();
        services.AddDemoSeeder<DemoChangesSeeder>();
        services.AddDemoSeeder<DemoRecordTypesSeeder>();
        services.AddDemoSeeder<DemoAppBuilderSeeder>();
        services.AddDemoSeeder<DemoProblemDetectionSeeder>();
        return services;
    }

    /// <summary>Adds a module's sample data to the demo.</summary>
    public static IServiceCollection AddDemoSeeder<TSeeder>(this IServiceCollection services)
        where TSeeder : class, IDemoSeeder
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDemoSeeder, TSeeder>());
        return services;
    }
}
