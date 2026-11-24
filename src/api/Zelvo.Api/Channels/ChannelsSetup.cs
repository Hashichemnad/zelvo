using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Channels;
using Zelvo.Data;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Channels;

/// <summary>Registers email and live chat (module 6, task E2) in the API.</summary>
internal static class ChannelsSetup
{
    public static IServiceCollection AddZelvoApiChannels(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoChannels(configuration);
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 4 * 1024;
        });
        services.AddSingleton<ChatSignals>();
        services.AddHostedService(sp => sp.GetRequiredService<ChatSignals>());
        services.AddSingleton<IMissingTranslationSource, ChatMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoChannels(this RouteGroupBuilder group)
    {
        group.MapTicketMessageEndpoints();
        group.MapMailboxEndpoints();
        group.MapChatEndpoints();
        // Signals only (see ChatHub); anyone may connect, joining groups needs a watch key.
        group.MapHub<ChatHub>("/chat/hub").RequireTenant().AllowAnonymousAccess();
        return group;
    }
}

/// <summary>Chat widgets with a name or greeting in one language only (LANG-5).</summary>
internal sealed class ChatMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        foreach (var widget in await db.ChatWidgets.AsNoTracking().ToListAsync(cancellationToken))
        {
            foreach (var (field, value) in new[] { ("name", widget.Name), ("greeting", widget.Greeting) })
            {
                if (value.HasAnyValue && !value.IsComplete)
                {
                    missing.Add(new MissingTranslation("chat_widget", widget.Id.ToString(), field, value.MissingLanguages));
                }
            }
        }

        return missing;
    }
}
