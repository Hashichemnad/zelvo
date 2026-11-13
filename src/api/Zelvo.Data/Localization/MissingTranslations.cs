namespace Zelvo.Data.Localization;

/// <summary>A bilingual field of one record with a language still to translate (LANG-5).</summary>
/// <param name="EntityType">Record type, for example <c>tenant</c> or <c>catalog_item</c>.</param>
/// <param name="EntityId">Record id.</param>
/// <param name="Field">Field name, for example <c>name</c>.</param>
/// <param name="MissingLanguages">Languages without a value.</param>
public sealed record MissingTranslation(string EntityType, string EntityId, string Field, IReadOnlyList<string> MissingLanguages);

/// <summary>
/// Finds records of one type with missing translations, for the administrator's report. Each
/// module with bilingual fields registers one; queries run under row-level security, so they only
/// see the current tenant.
/// </summary>
public interface IMissingTranslationSource
{
    /// <summary>Records with at least one bilingual field missing a language.</summary>
    Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken);
}
