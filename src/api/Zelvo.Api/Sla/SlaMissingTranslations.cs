using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Sla;

/// <summary>Reports SLA names that miss a language (LANG-5): calendars, holidays, customer groups and policies.</summary>
internal sealed class SlaMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        void Add(string type, Guid id, LocalizedText text)
        {
            if (!text.IsComplete)
            {
                missing.Add(new MissingTranslation(type, id.ToString(), "name", text.MissingLanguages));
            }
        }

        foreach (var c in await db.BusinessCalendars.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("business_calendar", c.Id, c.Name);
        foreach (var h in await db.BusinessCalendarHolidays.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("business_calendar_holiday", h.Id, h.Name);
        foreach (var g in await db.CustomerGroups.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("customer_group", g.Id, g.Name);
        foreach (var p in await db.SlaPolicies.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("sla_policy", p.Id, p.Name);
        return missing;
    }
}
