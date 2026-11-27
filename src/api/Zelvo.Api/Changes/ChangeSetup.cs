using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Changes;

/// <summary>Registers change management (module 17, task H3) in the API.</summary>
internal static class ChangeSetup
{
    public static IServiceCollection AddZelvoChanges(this IServiceCollection services)
    {
        services.AddSingleton<IMissingTranslationSource, ChangeMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoChangeEndpoints(this RouteGroupBuilder group) =>
        group.MapChangeCalendarEndpoints().MapChangeEndpoints().MapChangeTemplateEndpoints().MapChangeSettingsEndpoints().MapChangeBoardEndpoints().MapChangeWhatIfEndpoints();
}

/// <summary>Standard change templates, blackout periods and questionnaire wording that miss a language (LANG-5).</summary>
internal sealed class ChangeMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        foreach (var template in await db.ChangeTemplates.AsNoTracking().Where(t => t.Active).Select(t => new { t.Id, t.Name }).ToListAsync(cancellationToken))
        {
            Add(missing, "change_template", template.Id.ToString(), "name", template.Name);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var blackout in await db.ChangeBlackouts.AsNoTracking().Where(b => b.EndsAt > now).Select(b => new { b.Id, b.Name }).ToListAsync(cancellationToken))
        {
            Add(missing, "change_blackout", blackout.Id.ToString(), "name", blackout.Name);
        }

        var json = await db.TenantSettings.AsNoTracking().Where(s => s.Key == ChangeSettings.Changes.Key).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken);
        if (json is not null)
        {
            ChangeSettingsValue? value = null;
            try
            {
                value = System.Text.Json.JsonSerializer.Deserialize<ChangeSettingsValue>(json, Zelvo.Tickets.TicketSettingKeys.Json);
            }
            catch (System.Text.Json.JsonException)
            {
            }

            foreach (var question in value?.IsValid() == true ? value.Questions.Where(q => q.Active) : [])
            {
                Add(missing, "change_settings", "questions", question.Key, question.Text);
                foreach (var option in question.Options)
                {
                    Add(missing, "change_settings", "questions", $"{question.Key}.{option.Key}", option.Text);
                }
            }
        }

        return missing;
    }

    private static void Add(List<MissingTranslation> missing, string type, string id, string field, LocalizedText text)
    {
        if (!text.IsComplete)
        {
            missing.Add(new MissingTranslation(type, id, field, text.MissingLanguages));
        }
    }
}
