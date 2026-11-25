using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Voice;

/// <summary>Registers customer voice (module 3, task C6) in the API.</summary>
internal static class VoiceSetup
{
    public static IServiceCollection AddZelvoCustomerVoice(this IServiceCollection services)
    {
        services.AddSingleton<IMissingTranslationSource, VoiceMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoCustomerVoiceEndpoints(this RouteGroupBuilder group) =>
        group.MapVoiceReportEndpoints().MapVoiceEndpoints().MapVoicePublicEndpoints().MapVoiceSettingsEndpoints();
}

/// <summary>Names in the customer voice settings that miss a language (LANG-5): severities, root causes and the happiness meter's wording.</summary>
internal sealed class VoiceMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.TenantSettings.AsNoTracking()
            .Where(s => s.Key == VoiceSettings.Voice.Key || s.Key == VoiceSettings.Happiness.Key)
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var missing = new List<MissingTranslation>();
        if (rows.TryGetValue(VoiceSettings.Voice.Key, out var voiceJson) && Read<VoiceSettingsValue>(voiceJson) is { } voice && voice.IsValid())
        {
            for (var i = 0; i < voice.Severities.Count; i++)
            {
                Add(missing, "customer_voice_settings", "severities", $"{i + 1}", voice.Severities[i].Name);
            }

            foreach (var cause in voice.RootCauses)
            {
                Add(missing, "customer_voice_settings", "rootCauses", cause.Key, cause.Name);
            }
        }

        if (rows.TryGetValue(VoiceSettings.Happiness.Key, out var happinessJson) && Read<HappinessSettingsValue>(happinessJson) is { } happiness && happiness.IsValid())
        {
            Add(missing, "happiness_settings", "question", "ticket", happiness.Question);
            Add(missing, "happiness_settings", "question", "portal", happiness.PortalQuestion);
            for (var i = 0; i < happiness.Labels.Count; i++)
            {
                Add(missing, "happiness_settings", "labels", $"{i + 1}", happiness.Labels[i]);
            }
        }

        return missing;
    }

    private static void Add(List<MissingTranslation> missing, string type, string id, string field, SettingText text)
    {
        var value = text.ToText();
        if (!value.IsComplete)
        {
            missing.Add(new MissingTranslation(type, id, field, value.MissingLanguages));
        }
    }

    private static T? Read<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, TicketSettingKeys.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
