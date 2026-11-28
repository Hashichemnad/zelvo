using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Apps;

/// <summary>Registers the low-code app builder (module 20, task K3) in the API.</summary>
internal static class AppSetup
{
    public static IServiceCollection AddZelvoAppBuilder(this IServiceCollection services)
    {
        services.AddSingleton<IMissingTranslationSource, AppMissingTranslations>();
        return services;
    }
}

/// <summary>Names of apps, their entries and data lists, and labels of items that miss a language (LANG-5).</summary>
internal sealed class AppMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        foreach (var app in await db.Apps.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!app.Name.IsComplete)
            {
                missing.Add(new MissingTranslation("app", app.Id.ToString(), "name", app.Name.MissingLanguages));
            }

            var definition = AppDefinition.Read(app.Definition);
            foreach (var (field, text) in definition.ListViews.Select(v => ($"listViews.{v.Key}", v.Name))
                .Concat(definition.MenuEntries.Select(e => ($"menuEntries.{e.Key}", e.Label)))
                .Concat(definition.PortalEntries.Select(e => ($"portalEntries.{e.Key}", e.Label))))
            {
                var value = text.ToValue();
                if (value.HasAnyValue && !value.IsComplete)
                {
                    missing.Add(new MissingTranslation("app", app.Id.ToString(), field, value.MissingLanguages));
                }
            }
        }

        foreach (var list in await db.DataLists.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!list.Name.IsComplete)
            {
                missing.Add(new MissingTranslation("data_list", list.Id.ToString(), "name", list.Name.MissingLanguages));
            }
        }

        foreach (var item in await db.DataListItems.AsNoTracking().Where(i => i.Active && (i.Label.En == null || i.Label.Ar == null)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            missing.Add(new MissingTranslation("data_list_item", item.Id.ToString(), "label", item.Label.MissingLanguages));
        }

        return missing;
    }
}
