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
        // Live chat changes reach agents through the agent workspace's live updates (task B4).
        services.AddZelvoChannels(configuration);
        services.AddSingleton<IMissingTranslationSource, ChatMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoChannels(this RouteGroupBuilder group)
    {
        group.MapMailboxEndpoints();
        group.MapChatEndpoints();
        group.MapMessagingEndpoints();
        group.MapMessagingHooks();
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
