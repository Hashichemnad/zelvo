using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Forms;
using Zelvo.Data.Localization;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Forms;

/// <summary>
/// Reports forms and field sets that miss a language (LANG-5: "every field label and option in
/// English and Arabic"): names, descriptions, and each field's label, help text, placeholder and
/// options in the draft (what will be published next), table columns included.
/// </summary>
internal sealed class FormMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        void Add(string type, Guid id, string field, LocalizedText? text)
        {
            if (text is not null && text.HasAnyValue && !text.IsComplete)
            {
                missing.Add(new MissingTranslation(type, id.ToString(), field, text.MissingLanguages));
            }
        }

        void Fields(string type, Guid id, IEnumerable<FormField> fields, string prefix)
        {
            foreach (var field in fields)
            {
                var path = $"{prefix}{field.Key}";
                Add(type, id, $"{path}.label", field.Label);
                Add(type, id, $"{path}.help", field.Help);
                Add(type, id, $"{path}.placeholder", field.Placeholder);
                foreach (var option in field.Options ?? [])
                {
                    Add(type, id, $"{path}.options.{option.Value}", option.Label);
                }

                Fields(type, id, field.Columns ?? [], $"{path}.");
            }
        }

        foreach (var form in await db.Forms.AsNoTracking().Where(f => f.State != FormState.Retired).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            Add("form", form.Id, "name", form.Name);
            Add("form", form.Id, "description", form.Description);
            var schema = FormJson.ReadForm(form.DraftSchema) ?? new FormSchema();
            Fields("form", form.Id, schema.Elements.Where(e => e.Field is not null).Select(e => e.Field!), "fields.");
        }

        foreach (var set in await db.FieldSets.AsNoTracking().Where(s => s.State != FormState.Retired).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            Add("field_set", set.Id, "name", set.Name);
            Add("field_set", set.Id, "description", set.Description);
            Fields("field_set", set.Id, FormJson.ReadFieldSet(set.DraftSchema)?.Fields ?? [], "fields.");
        }

        return missing;
    }
}
