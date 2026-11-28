using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets.Forms;

/// <summary>A published form as people fill it: its own fields and those of its field sets as published now.</summary>
/// <param name="Form">The form.</param>
/// <param name="Version">The published version.</param>
/// <param name="Fields">Fields in order.</param>
public sealed record ResolvedForm(Form Form, FormVersion Version, IReadOnlyList<ResolvedField> Fields)
{
    /// <summary>The fields without where they came from.</summary>
    public IReadOnlyList<FormField> PlainFields => [.. Fields.Select(f => f.Field)];
}

/// <summary>Finds which form a ticket uses and puts its fields together.</summary>
public static class FormResolver
{
    /// <summary>
    /// Expands a form's elements: its own fields, and each field set as published now (field sets
    /// never published, or retired, add nothing). <paramref name="fieldSets"/> gives each field
    /// set's published fields.
    /// </summary>
    public static List<ResolvedField> Expand(FormSchema schema, IReadOnlyDictionary<Guid, IReadOnlyList<FormField>> fieldSets)
    {
        var fields = new List<ResolvedField>();
        foreach (var element in schema.Elements)
        {
            if (element.Type == FormElementType.Field && element.Field is { } field)
            {
                fields.Add(new ResolvedField(field, null));
            }
            else if (element.Type == FormElementType.FieldSet && element.FieldSetId is { } id && fieldSets.TryGetValue(id, out var setFields))
            {
                fields.AddRange(setFields.Select(f => new ResolvedField(f, id)));
            }
        }

        return fields;
    }

    /// <summary>Field sets a form schema refers to.</summary>
    public static IEnumerable<Guid> FieldSetIds(FormSchema schema) =>
        schema.Elements.Where(e => e.Type == FormElementType.FieldSet && e.FieldSetId is not null).Select(e => e.FieldSetId!.Value).Distinct();

    /// <summary>The published fields of the given field sets (retired and never published ones are left out).</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<FormField>>> PublishedFieldSetsAsync(
        ZelvoDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        var rows = await db.FieldSets.AsNoTracking()
            .Where(s => wanted.Contains(s.Id) && s.State == FormState.Published)
            .Join(db.FieldSetVersions, s => s.PublishedVersionId, v => v.Id, (s, v) => new { s.Id, v.Schema })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => (IReadOnlyList<FormField>)(FormJson.ReadFieldSet(r.Schema)?.Fields ?? []));
    }

    /// <summary>A form's published version with its field sets as published now; <see langword="null"/> unless the form is published.</summary>
    public static async Task<ResolvedForm?> PublishedAsync(ZelvoDbContext db, Guid formId, CancellationToken cancellationToken)
    {
        var form = await db.Forms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == formId, cancellationToken).ConfigureAwait(false);
        if (form is not { State: FormState.Published, PublishedVersionId: { } versionId })
        {
            return null;
        }

        var version = await db.FormVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
        var schema = FormJson.ReadForm(version.Schema) ?? new FormSchema();
        var sets = await PublishedFieldSetsAsync(db, FieldSetIds(schema), cancellationToken).ConfigureAwait(false);

        // Task K3: options of fields that take them from a data list.
        return new ResolvedForm(form, version, await DataListOptions.FillAsync(db, Expand(schema, sets), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The form for a new ticket of <paramref name="recordType"/> in <paramref name="categoryId"/>:
    /// the binding of the category or the nearest category above it, else the record type's
    /// default. Bindings to forms that are not published are skipped.
    /// </summary>
    public static async Task<ResolvedForm?> ForTicketAsync(ZelvoDbContext db, TicketType recordType, Guid? categoryId, CancellationToken cancellationToken, Guid? customRecordTypeId = null)
    {
        // Task K2: a custom record type has bindings of its own.
        var bindings = await db.FormBindings.AsNoTracking().Where(b => b.RecordType == recordType && b.CustomRecordTypeId == customRecordTypeId)
            .Join(db.Forms.Where(f => f.State == FormState.Published), b => b.FormId, f => f.Id, (b, f) => new { b.CategoryId, b.FormId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (bindings.Count == 0)
        {
            return null;
        }

        var chain = new List<Guid?>();
        if (categoryId is { } start)
        {
            var parents = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, cancellationToken).ConfigureAwait(false);
            Guid? current = start;
            while (current is { } id && parents.ContainsKey(id) && chain.Count < Data.Directory.TreeNode.MaxDepth)
            {
                chain.Add(id);
                current = parents[id];
            }
        }

        chain.Add(null);
        foreach (var category in chain)
        {
            if (bindings.FirstOrDefault(b => b.CategoryId == category) is { } binding)
            {
                return await PublishedAsync(db, binding.FormId, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Default answers for <paramref name="requester"/>: profile values where the field asks for
    /// one and the profile has it, else the field's fixed default.
    /// </summary>
    public static async Task<JsonObject> DefaultsAsync(ZelvoDbContext db, IReadOnlyList<FormField> fields, UserAccount? requester, string language, CancellationToken cancellationToken)
    {
        var defaults = new JsonObject();
        string? department = null;
        if (requester?.DepartmentId is { } departmentId && fields.Any(f => f.ProfileDefault == ProfileSource.Department))
        {
            var name = await db.Departments.AsNoTracking().Where(d => d.Id == departmentId).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            department = name?.GetOrFallback(language);
        }

        foreach (var field in fields)
        {
            JsonNode? profile = field.ProfileDefault switch
            {
                ProfileSource.DisplayName => requester?.DisplayName,
                ProfileSource.Email => requester?.Email,
                ProfileSource.Phone => requester?.Phone,
                ProfileSource.EmiratesId => requester?.EmiratesId,
                ProfileSource.EmployeeId => requester?.EmployeeId,
                ProfileSource.Department => department,
                ProfileSource.Manager => requester?.ManagerId?.ToString(),
                ProfileSource.Location => requester?.LocationId?.ToString(),
                _ => null,
            };
            var value = profile ?? field.DefaultValue?.DeepClone();
            if (value is not null)
            {
                defaults[field.Key] = value;
            }
        }

        return defaults;
    }
}
