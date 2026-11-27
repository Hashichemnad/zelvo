using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Problems;

/// <summary>Registers problem management (module 16, task H1) in the API.</summary>
internal static class ProblemSetup
{
    public static IServiceCollection AddZelvoProblems(this IServiceCollection services)
    {
        services.AddSingleton<IMissingTranslationSource, ProblemMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoProblemEndpoints(this RouteGroupBuilder group) =>
        group.MapProblemEndpoints().MapKnownErrorEndpoints().MapProblemSettingsEndpoints();
}

/// <summary>Known errors and fishbone categories that miss a language (LANG-5).</summary>
internal sealed class ProblemMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        var errors = await db.KnownErrors.AsNoTracking().Where(e => e.Status == Data.Problems.KnownErrorStatus.Active)
            .Select(e => new { e.Id, e.Title, e.Workaround }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var error in errors)
        {
            Add(missing, "known_error", error.Id.ToString(), "title", error.Title);
            Add(missing, "known_error", error.Id.ToString(), "workaround", error.Workaround);
        }

        var json = await db.TenantSettings.AsNoTracking().Where(s => s.Key == ProblemSettings.Problems.Key).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (json is not null)
        {
            ProblemSettingsValue? value = null;
            try
            {
                value = JsonSerializer.Deserialize<ProblemSettingsValue>(json, TicketSettingKeys.Json);
            }
            catch (JsonException)
            {
            }

            foreach (var category in value?.IsValid() == true ? value.FishboneCategories : [])
            {
                Add(missing, "problem_settings", "fishboneCategories", category.Key, category.Name.ToText());
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
