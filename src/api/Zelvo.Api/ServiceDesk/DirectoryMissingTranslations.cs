using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;

namespace Zelvo.Api.ServiceDesk;

/// <summary>
/// Reports directory names that miss a language (LANG-5): categories, locations, skills, groups
/// (name and, when it has one, description) and assignment rules.
/// </summary>
internal sealed class DirectoryMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        void Add(string type, Guid id, string field, LocalizedText text)
        {
            if (!text.IsComplete)
            {
                missing.Add(new MissingTranslation(type, id.ToString(), field, text.MissingLanguages));
            }
        }

        foreach (var c in await db.Categories.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("category", c.Id, "name", c.Name);
        foreach (var l in await db.Locations.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("location", l.Id, "name", l.Name);
        foreach (var s in await db.Skills.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("skill", s.Id, "name", s.Name);
        foreach (var g in await db.SupportGroups.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            Add("support_group", g.Id, "name", g.Name);
            if (g.Description.HasAnyValue)
            {
                Add("support_group", g.Id, "description", g.Description);
            }
        }

        foreach (var r in await db.AssignmentRules.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)) Add("assignment_rule", r.Id, "name", r.Name);
        return missing;
    }
}
