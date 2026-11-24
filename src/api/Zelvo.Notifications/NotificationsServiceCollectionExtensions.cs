using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Data;

namespace Zelvo.Notifications;

/// <summary>Registers notifications for the API and the worker.</summary>
public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// What both hosts need: installation options, the interceptor that queues notifications with
    /// each change and the sources of tickets, SLA alerts and knowledge, the composer, the processor
    /// and the SMTP sender. Needs <c>AddZelvoData</c>.
    /// </summary>
    public static IServiceCollection AddZelvoNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ => NotificationOptions.From(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        services.TryAddScoped<NotificationComposer>();
        services.TryAddScoped<NotificationProcessor>();
        services.TryAddScoped<PendingNotifications>();
        if (!services.Any(d => d.ImplementationType == typeof(NotificationInterceptor)))
        {
            services.AddZelvoSaveChangesInterceptor<NotificationInterceptor>();
            services.AddScoped<INotificationSource, IncidentNotificationSource>();
            services.AddScoped<INotificationSource, SlaNotificationSource>();
            services.AddScoped<INotificationSource, KnowledgeNotificationSource>();
            services.AddScoped<INotificationSource, CommentNotificationSource>();
            services.AddScoped<INotificationSource, VoiceNotificationSource>();
            services.AddScoped<INotificationSource, SentimentNotificationSource>();
            services.AddScoped<INotificationSource, ApprovalNotificationSource>();
            services.AddScoped<INotificationSource, PendingNotificationSource>();
        }

        return services;
    }

    /// <summary>The worker side: rendering and sending, and removal of old notifications.</summary>
    public static IServiceCollection AddZelvoNotificationJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoNotifications(configuration);
        services.TryAddSingleton<NotificationDispatcher>();
        services.TryAddSingleton<NotificationPurge>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<NotificationPurge>());
        return services;
    }
}
