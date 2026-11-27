using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Records;

/// <summary>Registers custom record types (module 20, task K2) in the API.</summary>
internal static class RecordSetup
{
    public static IServiceCollection AddZelvoRecords(this IServiceCollection services)
    {
        services.AddSingleton<IMissingTranslationSource, RecordMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoRecordEndpoints(this RouteGroupBuilder group) =>
        group.MapRecordTypeEndpoints().MapRecordEndpoints();
}

/// <summary>Record type names and state names that miss a language (LANG-5).</summary>
internal sealed class RecordMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        foreach (var type in await db.RecordTypes.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!type.Name.IsComplete)
            {
                missing.Add(new MissingTranslation("record_type", type.Id.ToString(), "name", type.Name.MissingLanguages));
            }

            foreach (var state in RecordStates.Read(type.States))
            {
                var name = state.Name.ToText();
                if (!name.IsComplete)
                {
                    missing.Add(new MissingTranslation("record_type", type.Id.ToString(), $"states.{state.Key}", name.MissingLanguages));
                }
            }
        }

        return missing;
    }
}
