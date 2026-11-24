using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Forms;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Forms;

/// <summary>
/// Reusable field sets (task C1, module 2: "a change updates every form that uses it"). Forms refer
/// to a field set; publishing a new version changes every published form that uses it at once.
/// Publishing or retiring is refused when it would break a published form (a field key used twice,
/// or a rule looking at a field that would be gone). Needs <c>forms.manage</c>; audited.
/// </summary>
internal static class FieldSetEndpoints
{
    public static RouteGroupBuilder MapFieldSetEndpoints(this RouteGroupBuilder group)
    {
        var sets = group.MapGroup("/field-sets").WithTags("Forms").RequireTenant().RequirePermission(Permissions.FormsManage);
        sets.MapGet("/", ListAsync).WithName("ListFieldSets")
            .WithSummary("Field sets by English name, with the forms that use them. Needs forms.manage.")
            .Produces<IReadOnlyList<FieldSetResponse>>();
        sets.MapPost("/", CreateAsync).WithName("CreateFieldSet")
            .WithSummary("Add a field set as a draft. Needs forms.manage; audited.")
            .Produces<FieldSetResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        sets.MapGet("/{id:guid}", GetAsync).WithName("GetFieldSet")
            .WithSummary("A field set with its draft, its published fields, its problems and the forms that use it. Needs forms.manage.")
            .Produces<FieldSetResponse>();
        sets.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateFieldSet")
            .WithSummary("Change a field set's key, name or description (If-Match optional). Needs forms.manage; audited.")
            .Produces<FieldSetResponse>().ProducesValidationProblem();
        sets.MapPut("/{id:guid}/draft", SaveDraftAsync).WithName("SaveFieldSetDraft")
            .WithSummary("Replace the draft's fields (If-Match optional). Needs forms.manage; audited.")
            .Produces<FieldSetResponse>().ProducesValidationProblem();
        sets.MapPost("/{id:guid}/publish", PublishAsync).WithName("PublishFieldSet")
            .WithSummary("Publish the draft: every published form that uses the field set shows the new fields at once. Refused with the problems, including those it would cause in forms. Needs forms.manage; audited.")
            .Produces<FieldSetResponse>();
        sets.MapPost("/{id:guid}/retire", RetireAsync).WithName("RetireFieldSet")
            .WithSummary("Take the field set out of every form that uses it; refused while a form's rules look at its fields. Needs forms.manage; audited.")
            .Produces<FieldSetResponse>();
        sets.MapPost("/{id:guid}/reactivate", ReactivateAsync).WithName("ReactivateFieldSet")
            .WithSummary("Put a retired field set back into the forms that use it. Needs forms.manage; audited.")
            .Produces<FieldSetResponse>();
        sets.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteFieldSet")
            .WithSummary("Delete a field set that was never published and that no form uses. Needs forms.manage; audited.")
            .Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<Ok<List<FieldSetResponse>>> ListAsync(HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var sets = await db.FieldSets.AsNoTracking().ToListAsync(cancellationToken);
        var usage = await UsageAsync(db, cancellationToken);
        var list = new List<FieldSetResponse>();
        foreach (var set in sets.OrderBy(s => s.Name.En ?? s.Name.Ar, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(await ResponseAsync(db, set, usage, cancellationToken));
        }

        return TypedResults.Ok(list);
    }

    internal static async Task<Results<Ok<FieldSetResponse>, ProblemHttpResult>> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (set is null)
        {
            return FormsCommon.NotFound("field-set");
        }

        FormsCommon.SetETag(http, set.Version);
        return TypedResults.Ok(await ResponseAsync(db, set, await UsageAsync(db, cancellationToken), cancellationToken));
    }

    internal static async Task<Results<Created<FieldSetResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateFormRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var key = request.Key!.Trim();
        if (!FormsCommon.KeyPattern().IsMatch(key))
        {
            return UserEndpoints.Validation("key", "Use small letters, digits and dashes, for example requester-details.");
        }

        var name = FormsCommon.Clean(request.Name)!;
        var description = FormsCommon.Clean(request.Description) ?? new(null, null);
        if (FormsCommon.CheckNames(name, description) is { } invalid)
        {
            return invalid;
        }

        var schema = request.Schema is null ? FormJson.Write(new FieldSetSchema()) : FormsCommon.Canonical(request.Schema, FormJson.ReadFieldSet);
        if (schema is null)
        {
            return UserEndpoints.Validation("schema", "The fields are not in the expected form, or are too large.");
        }

        if (await db.FieldSets.AnyAsync(s => s.Key == key, cancellationToken))
        {
            return FormsCommon.KeyTaken();
        }

        var set = new FieldSet { Key = key, Name = name, Description = description, DraftSchema = schema, CreatedById = user.Account?.Id };
        db.FieldSets.Add(set);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/field-sets/{set.Id}", await ReloadAsync(http, db, set.Id, cancellationToken));
    }

    internal static async Task<Results<Ok<FieldSetResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateFormRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (set is null)
        {
            return FormsCommon.NotFound("field-set");
        }

        if (!FormsCommon.Matches(http, set.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        var name = FormsCommon.Clean(request.Name);
        var description = FormsCommon.Clean(request.Description);
        if (FormsCommon.CheckNames(name, description) is { } invalid)
        {
            return invalid;
        }

        if (request.Key?.Trim() is { } key && key != set.Key)
        {
            if (!FormsCommon.KeyPattern().IsMatch(key))
            {
                return UserEndpoints.Validation("key", "Use small letters, digits and dashes, for example requester-details.");
            }

            if (await db.FieldSets.AnyAsync(s => s.Key == key && s.Id != id, cancellationToken))
            {
                return FormsCommon.KeyTaken();
            }

            set.Key = key;
        }

        set.Name = name ?? set.Name;
        set.Description = description ?? set.Description;
        set.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FieldSetResponse>, ValidationProblem, ProblemHttpResult>> SaveDraftAsync(
        Guid id, SaveDraftRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (set is null)
        {
            return FormsCommon.NotFound("field-set");
        }

        if (!FormsCommon.Matches(http, set.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (set.State == FormState.Retired)
        {
            return FormsCommon.State("Reactivate the field set before changing it.");
        }

        var schema = FormsCommon.Canonical(request.Schema, FormJson.ReadFieldSet);
        if (schema is null)
        {
            return UserEndpoints.Validation("schema", "The fields are not in the expected form, or are too large.");
        }

        if (schema != set.DraftSchema)
        {
            set.DraftSchema = schema;
            set.HasUnpublishedChanges = set.PublishedVersionId is not { } published
                || schema != await db.FieldSetVersions.Where(v => v.Id == published).Select(v => v.Schema).SingleAsync(cancellationToken);
            set.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FieldSetResponse>, ProblemHttpResult>> PublishAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (set is null)
        {
            return FormsCommon.NotFound("field-set");
        }

        if (!FormsCommon.Matches(http, set.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (set.State == FormState.Retired)
        {
            return FormsCommon.State("Reactivate the field set before publishing it.");
        }

        if (!set.HasUnpublishedChanges && set.State == FormState.Published)
        {
            return FormsCommon.State("There are no changes to publish.");
        }

        var fields = FormJson.ReadFieldSet(set.DraftSchema)?.Fields ?? [];
        var problems = FormDefinitionCheck.CheckFieldSet(fields);
        if (fields.Count == 0)
        {
            problems.Add(new(null, "form-empty", "Add at least one field."));
        }

        if (problems.Count > 0)
        {
            return FormsCommon.Problems("field-set-has-problems", "Fix the field set's problems before publishing it.", problems);
        }

        if (await FormConflictsAsync(db, id, fields, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var number = (await db.FieldSetVersions.Where(v => v.FieldSetId == id).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
        var version = new FieldSetVersion { FieldSetId = id, Number = number, Schema = set.DraftSchema, PublishedById = user.Account?.Id };
        db.FieldSetVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        set.PublishedVersionId = version.Id;
        set.State = FormState.Published;
        set.HasUnpublishedChanges = false;
        set.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "forms.field-set.published", "field_set", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = number, ["key"] = set.Key });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FieldSetResponse>, ProblemHttpResult>> RetireAsync(
        Guid id, HttpContext http, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (set is null)
        {
            return FormsCommon.NotFound("field-set");
        }

        if (set.State != FormState.Published)
        {
            return FormsCommon.State("Only published field sets can be retired.");
        }

        if (await FormConflictsAsync(db, id, null, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        set.State = FormState.Retired;
        set.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "forms.field-set.retired", "field_set", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FieldSetResponse>, ProblemHttpResult>> ReactivateAsync(
        Guid id, HttpContext http, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (set is null)
        {
            return FormsCommon.NotFound("field-set");
        }

        if (set.State != FormState.Retired)
        {
            return FormsCommon.State("Only retired field sets can be reactivated.");
        }

        var published = await db.FieldSetVersions.Where(v => v.Id == set.PublishedVersionId).Select(v => v.Schema).SingleAsync(cancellationToken);
        if (await FormConflictsAsync(db, id, FormJson.ReadFieldSet(published)?.Fields ?? [], cancellationToken) is { } conflict)
        {
            return conflict;
        }

        set.State = FormState.Published;
        set.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "forms.field-set.reactivated", "field_set", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (set is null)
        {
            return FormsCommon.NotFound("field-set");
        }

        if (set.PublishedVersionId is not null)
        {
            return FormsCommon.State("Published field sets are kept; retire it instead.");
        }

        if ((await UsageAsync(db, cancellationToken))[id].Any())
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "in-use", "Forms use this field set; remove it from them first.");
        }

        db.FieldSets.Remove(set);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Checks every published form that uses the field set with <paramref name="fields"/> in its
    /// place (<see langword="null"/>: without it). A refusal names each form and its problems.
    /// </summary>
    private static async Task<ProblemHttpResult?> FormConflictsAsync(ZelvoDbContext db, Guid setId, IReadOnlyList<FormField>? fields, CancellationToken cancellationToken)
    {
        var forms = await db.Forms.AsNoTracking().Where(f => f.State == FormState.Published)
            .Join(db.FormVersions, f => f.PublishedVersionId, v => v.Id, (f, v) => new { f.Id, f.Key, f.Name, v.Schema })
            .ToListAsync(cancellationToken);
        var conflicts = new List<object>();
        var problems = new List<FormProblem>();
        foreach (var form in forms)
        {
            var schema = FormJson.ReadForm(form.Schema) ?? new FormSchema();
            var ids = FormResolver.FieldSetIds(schema).ToList();
            if (!ids.Contains(setId))
            {
                continue;
            }

            var sets = await FormResolver.PublishedFieldSetsAsync(db, ids, cancellationToken);
            if (fields is null)
            {
                sets.Remove(setId);
            }
            else
            {
                sets[setId] = fields;
            }

            var found = FormDefinitionCheck.Check([.. FormResolver.Expand(schema, sets).Select(f => f.Field)]);
            if (found.Count > 0)
            {
                conflicts.Add(new { formId = form.Id, key = form.Key, name = LocalizedTextDto.From(form.Name), problems = found });
                problems.AddRange(found);
            }
        }

        return conflicts.Count == 0
            ? null
            : FormsCommon.Problems("field-set-breaks-forms", "This would break published forms that use the field set; change them first.", problems, conflicts);
    }

    /// <summary>Forms whose draft or published version uses each field set.</summary>
    private static async Task<ILookup<Guid, FormUseRef>> UsageAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var forms = await db.Forms.AsNoTracking()
            .Select(f => new { f.Id, f.Key, f.Name, f.State, f.DraftSchema, Published = db.FormVersions.Where(v => v.Id == f.PublishedVersionId).Select(v => v.Schema).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        return forms.SelectMany(f =>
        {
            var ids = FormResolver.FieldSetIds(FormJson.ReadForm(f.DraftSchema) ?? new FormSchema());
            if (f.Published is { } published)
            {
                ids = ids.Concat(FormResolver.FieldSetIds(FormJson.ReadForm(published) ?? new FormSchema()));
            }

            return ids.Distinct().Select(id => (id, use: new FormUseRef(f.Id, f.Key, LocalizedTextDto.From(f.Name), f.State)));
        }).ToLookup(x => x.id, x => x.use);
    }

    private static async Task<FieldSetResponse> ReloadAsync(HttpContext http, ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var set = await db.FieldSets.AsNoTracking().SingleAsync(s => s.Id == id, cancellationToken);
        FormsCommon.SetETag(http, set.Version);
        return await ResponseAsync(db, set, await UsageAsync(db, cancellationToken), cancellationToken);
    }

    private static async Task<FieldSetResponse> ResponseAsync(ZelvoDbContext db, FieldSet set, ILookup<Guid, FormUseRef> usage, CancellationToken cancellationToken)
    {
        var version = set.PublishedVersionId is { } v ? await db.FieldSetVersions.AsNoTracking().SingleAsync(x => x.Id == v, cancellationToken) : null;
        var draft = FormJson.ReadFieldSet(set.DraftSchema)?.Fields ?? [];
        var problems = FormDefinitionCheck.CheckFieldSet(draft);
        if (draft.Count == 0)
        {
            problems.Add(new(null, "form-empty", "Add at least one field."));
        }

        return new FieldSetResponse(set.Id, set.Key, LocalizedTextDto.From(set.Name), LocalizedTextDto.From(set.Description), set.State,
            set.HasUnpublishedChanges, version?.Number, version?.PublishedAt, FormsCommon.Node(set.DraftSchema),
            version is null ? null : FormsCommon.Node(version.Schema), problems, [.. usage[set.Id]], set.UpdatedAt, set.Version);
    }
}

/// <summary>A form that uses a field set.</summary>
internal sealed record FormUseRef(Guid Id, string Key, LocalizedTextDto Name, FormState State);

/// <summary>A field set.</summary>
/// <param name="Draft">The draft's fields (<c>{"fields": [...]}</c>).</param>
/// <param name="Published">The published fields, if published.</param>
/// <param name="Problems">What stops the draft from being published.</param>
/// <param name="UsedBy">Forms whose draft or published version uses it.</param>
/// <param name="ETag">Version for If-Match (also in the ETag header).</param>
internal sealed record FieldSetResponse(
    Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, FormState State, bool HasUnpublishedChanges,
    int? PublishedVersion, DateTimeOffset? PublishedAt, JsonNode? Draft, JsonNode? Published, IReadOnlyList<FormProblem> Problems,
    IReadOnlyList<FormUseRef> UsedBy, DateTimeOffset UpdatedAt, uint ETag);
