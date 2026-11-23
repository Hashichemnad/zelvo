using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Data;

namespace Zelvo.Integrations;

/// <summary>Registers integrations for the API and the worker.</summary>
public static class IntegrationsServiceCollectionExtensions
{
    /// <summary>
    /// What both hosts need: installation options, the outgoing-request policy, the webhook sender,
    /// delivery attempts and the interceptor that queues webhook events with each change. Needs
    /// <c>AddZelvoData</c> and <c>AddZelvoSecrets</c>.
    /// </summary>
    public static IServiceCollection AddZelvoIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ => IntegrationOptions.From(configuration));
        services.TryAddSingleton(sp => new OutboundAddressPolicy(sp.GetRequiredService<IntegrationOptions>().Webhooks));
        services.TryAddSingleton(sp => new WebhookSender(sp.GetRequiredService<IntegrationOptions>().Webhooks, sp.GetRequiredService<OutboundAddressPolicy>()));
        services.TryAddScoped<WebhookDeliveryService>();
        if (!services.Any(d => d.ImplementationType == typeof(WebhookEventInterceptor)))
        {
            services.AddZelvoSaveChangesInterceptor<WebhookEventInterceptor>();
        }

        return services;
    }

    /// <summary>Makes a module's changes raise webhook events.</summary>
    public static IServiceCollection AddWebhookEventSource<T>(this IServiceCollection services)
        where T : class, IWebhookEventSource
    {
        services.AddScoped<IWebhookEventSource, T>();
        return services;
    }

    /// <summary>The worker side: webhook delivery with retries, and removal of old integration records.</summary>
    public static IServiceCollection AddZelvoIntegrationJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoIntegrations(configuration);
        services.TryAddSingleton<WebhookDispatcher>();
        services.TryAddSingleton<IntegrationRecordsPurge>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<IntegrationRecordsPurge>());
        return services;
    }
}
