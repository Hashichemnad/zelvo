using Microsoft.EntityFrameworkCore;
using Zelvo.Data;

namespace Zelvo.Tickets.Forms;

/// <summary>
/// Task K3 (module 20): dropdown and radio fields whose options come from a data list. When a
/// published form is put together for filling in, each such field gets the list's active items as
/// its options (in the list's order), so the ticket keeps them with its copy of the fields and the
/// answers are checked like any other options. A list that no longer exists gives no options.
/// </summary>
public static class DataListOptions
{
    /// <summary>The fields with their data lists' items as options.</summary>
    public static async Task<List<ResolvedField>> FillAsync(ZelvoDbContext db, IReadOnlyList<ResolvedField> fields, CancellationToken cancellationToken)
    {
        var keys = fields.SelectMany(f => Keys(f.Field)).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return [.. fields];
        }

        var items = await db.DataLists.AsNoTracking().Where(l => keys.Contains(l.Key))
            .Join(db.DataListItems.AsNoTracking().Where(i => i.Active), l => l.Id, i => i.DataListId, (l, i) => new { List = l.Key, i.Key, i.Label, i.Position })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var options = items.GroupBy(i => i.List, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<FieldOption>)[.. g.OrderBy(i => i.Position).ThenBy(i => i.Key, StringComparer.Ordinal).Take(FormDefinitionCheck.MaxOptions).Select(i => new FieldOption(i.Key, i.Label))],
            StringComparer.Ordinal);
        return [.. fields.Select(f => f with { Field = Fill(f.Field, options) })];
    }

    /// <summary>The data lists a field (or its table columns) takes options from.</summary>
    public static IEnumerable<string> Keys(FormField field) =>
        (field.DataList is { } key ? [key] : Enumerable.Empty<string>()).Concat((field.Columns ?? []).SelectMany(Keys));

    private static FormField Fill(FormField field, IReadOnlyDictionary<string, IReadOnlyList<FieldOption>> options) => field with
    {
        Options = field.DataList is { } key ? options.GetValueOrDefault(key) ?? [] : field.Options,
        Columns = field.Columns is null ? null : [.. field.Columns.Select(c => Fill(c, options))],
    };
}
