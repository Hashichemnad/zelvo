using Microsoft.AspNetCore.Http.HttpResults;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Localization;

/// <summary>Missing-translation report for administrators (LANG-5).</summary>
internal static class TranslationEndpoints
{
    public static RouteGroupBuilder MapTranslationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/translations/missing", ListMissingAsync)
            .WithTags("Translations")
            .WithName("ListMissingTranslations")
            .WithSummary("Bilingual fields with a language still to translate, across all record types.")
            .RequireTenant()
            .RequirePermission(Permissions.TranslationsRead)
            .ProducesValidationProblem();
        return group;
    }

    internal static async Task<Ok<PageResult<MissingTranslation>>> ListMissingAsync(
        [AsParameters] PageQuery paging,
        ZelvoDbContext db,
        IEnumerable<IMissingTranslationSource> sources,
        CancellationToken cancellationToken)
    {
        var all = new List<MissingTranslation>();
        foreach (var source in sources)
        {
            all.AddRange(await source.FindAsync(db, cancellationToken));
        }

        var ordered = all
            .OrderBy(m => m.EntityType, StringComparer.Ordinal)
            .ThenBy(m => m.EntityId, StringComparer.Ordinal)
            .ThenBy(m => m.Field, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok(new PageResult<MissingTranslation>(
            ordered.Skip(paging.Skip).Take(paging.Size).ToList(), paging.Number, paging.Size, ordered.Count));
    }
}
