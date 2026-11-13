using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Localization;

namespace Zelvo.Data.Tenancy;

/// <summary>Reports the tenant's own name when one language is missing.</summary>
public sealed class TenantMissingTranslations : IMissingTranslationSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Name.En == null || t.Name.Ar == null)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return tenants
            .Select(t => new MissingTranslation("tenant", t.Id.ToString(), "name", t.Name.MissingLanguages))
            .ToList();
    }
}
