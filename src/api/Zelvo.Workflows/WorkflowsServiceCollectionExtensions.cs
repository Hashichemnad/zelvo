using Microsoft.Extensions.DependencyInjection;
using Zelvo.Data;
using Zelvo.Workflows.Rules;

namespace Zelvo.Workflows;

/// <summary>Registers workflows for the API and the worker.</summary>
public static class WorkflowsServiceCollectionExtensions
{
    /// <summary>
    /// The workflow rule interceptor, which runs rules with every save. Call after
    /// <c>AddZelvoData</c> and before notifications and integrations, so that changes made by rules
    /// are notified and sent as webhooks like any other.
    /// </summary>
    public static IServiceCollection AddZelvoWorkflowRules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(d => d.ImplementationType == typeof(WorkflowRuleInterceptor)))
        {
            services.AddScoped<Notifications.PendingNotifications>();
            services.AddZelvoSaveChangesInterceptor<WorkflowRuleInterceptor>();
        }

        return services;
    }
}
