using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Forms;

/// <summary>
/// Forms (task C1, module 2): built in Settings → Forms with fields, rules and field sets, saved as
/// drafts and published as versions that tickets use. Administrators (<c>forms.manage</c>) change
/// them; everyone signed in can get the form they are to fill (<c>/forms/resolve</c>). Every change
/// is audited.
/// </summary>
internal static class FormEndpoints
{
    public static RouteGroupBuilder MapFormEndpoints(this RouteGroupBuilder group)
    {
        var forms = group.MapGroup("/forms").WithTags("Forms").RequireTenant();

        forms.MapGet("/", ListAsync).WithName("ListForms")
            .WithSummary("Forms by English name, with their state and where they are used. Needs forms.manage.")
            .RequirePermission(Permissions.FormsManage).Produces<IReadOnlyList<FormSummaryResponse>>();
        forms.MapPost("/", CreateAsync).WithName("CreateForm")
            .WithSummary("Add a form as a draft. Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        forms.MapGet("/resolve", ResolveAsync).WithName("ResolveForm")
            .WithSummary("The published form a new ticket of the record type and category uses (the category's, the nearest category's above it, or the record type's default), with its fields and the requester's defaults. Every signed-in user; staff who log incidents for others may name the requester. Answers null when no form applies.")
            .RequireAuthorization().Produces<ResolvedFormResponse>();
        forms.MapGet("/people", PeopleAsync).WithName("SearchFormPeople")
            .WithSummary("Active users by name for user picker fields (at least 2 characters, at most 20 results; names only). Every signed-in user.")
            .RequireAuthorization().Produces<IReadOnlyList<PersonRef>>();
        forms.MapGet("/{id:guid}", GetAsync).WithName("GetForm")
            .WithSummary("A form with its draft, the draft's fields with field sets expanded, the problems that stop it from being published, and where it is used. Needs forms.manage.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>();
        forms.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateForm")
            .WithSummary("Change a form's key, name or description (If-Match optional). Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>().ProducesValidationProblem();
        forms.MapPut("/{id:guid}/draft", SaveDraftAsync).WithName("SaveFormDraft")
            .WithSummary("Replace the draft's elements (If-Match optional). Drafts may have problems; they are listed and stop publishing. Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>().ProducesValidationProblem();
        forms.MapPost("/{id:guid}/publish", PublishAsync).WithName("PublishForm")
            .WithSummary("Publish the draft as a new version; new tickets use it at once, tickets already sent keep theirs. Refused with the problems while there are any. Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>();
        forms.MapPost("/{id:guid}/discard", DiscardAsync).WithName("DiscardFormDraft")
            .WithSummary("Throw away the draft's changes and start again from the published version. Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>();
        forms.MapPost("/{id:guid}/retire", RetireAsync).WithName("RetireForm")
            .WithSummary("Stop using a published form: no new tickets use it (their record type's default or no form applies instead). Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>();
        forms.MapPost("/{id:guid}/reactivate", ReactivateAsync).WithName("ReactivateForm")
            .WithSummary("Use a retired form again with its last published version. Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>();
        forms.MapPost("/{id:guid}/copy", CopyAsync).WithName("CopyForm")
            .WithSummary("Make a new draft form from this form's draft. Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces<FormResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        forms.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteForm")
            .WithSummary("Delete a form that was never published. Published forms are retired instead. Needs forms.manage; audited.")
            .RequirePermission(Permissions.FormsManage).Produces(StatusCodes.Status204NoContent);
        forms.MapGet("/{id:guid}/versions", VersionsAsync).WithName("ListFormVersions")
            .WithSummary("Published versions, newest first. Needs forms.manage.")
            .RequirePermission(Permissions.FormsManage).Produces<IReadOnlyList<FormVersionSummary>>();
        forms.MapGet("/{id:guid}/versions/{number:int}", VersionAsync).WithName("GetFormVersion")
            .WithSummary("One published version's elements. Needs forms.manage.")
            .RequirePermission(Permissions.FormsManage).Produces<FormVersionResponse>();
        return group;
    }

    internal static async Task<Ok<List<FormSummaryResponse>>> ListAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var forms = await db.Forms.AsNoTracking().ToListAsync(cancellationToken);
        var versions = await db.FormVersions.AsNoTracking().Where(v => forms.Select(f => f.PublishedVersionId).Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        var bindings = await BindingsAsync(db, null, cancellationToken);
        return TypedResults.Ok(forms.OrderBy(f => f.Name.En ?? f.Name.Ar, StringComparer.OrdinalIgnoreCase).Select(f =>
        {
            var version = f.PublishedVersionId is { } v ? versions.GetValueOrDefault(v) : null;
            var count = FormJson.ReadForm(f.DraftSchema)?.Elements.Count ?? 0;
            return new FormSummaryResponse(f.Id, f.Key, LocalizedTextDto.From(f.Name), LocalizedTextDto.From(f.Description), f.State,
                f.HasUnpublishedChanges, version?.Number, version?.PublishedAt, count, bindings[f.Id].ToList(), f.UpdatedAt);
        }).ToList());
    }

    internal static async Task<Results<Ok<FormResponse>, ProblemHttpResult>> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var form = await db.Forms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        return form is null ? FormsCommon.NotFound("form") : TypedResults.Ok(await ResponseAsync(http, db, form, cancellationToken));
    }

    internal static async Task<Results<Created<FormResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateFormRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var key = request.Key!.Trim();
        if (!FormsCommon.KeyPattern().IsMatch(key))
        {
            return UserEndpoints.Validation("key", "Use small letters, digits and dashes, for example laptop-problem.");
        }

        var name = FormsCommon.Clean(request.Name)!;
        var description = FormsCommon.Clean(request.Description) ?? new(null, null);
        if (FormsCommon.CheckNames(name, description) is { } invalid)
        {
            return invalid;
        }

        var schema = request.Schema is null ? FormJson.Write(new FormSchema()) : FormsCommon.Canonical(request.Schema, FormJson.ReadForm);
        if (schema is null)
        {
            return UserEndpoints.Validation("schema", "The elements are not in the expected form, or are too large.");
        }

        if (await db.Forms.AnyAsync(f => f.Key == key, cancellationToken))
        {
            return FormsCommon.KeyTaken();
        }

        var form = new Form { Key = key, Name = name, Description = description, DraftSchema = schema, CreatedById = user.Account?.Id };
        db.Forms.Add(form);
        await db.SaveChangesAsync(cancellationToken);
        var saved = await db.Forms.AsNoTracking().SingleAsync(f => f.Id == form.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/forms/{form.Id}", await ResponseAsync(http, db, saved, cancellationToken));
    }

    internal static async Task<Results<Ok<FormResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateFormRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (form is null)
        {
            return FormsCommon.NotFound("form");
        }

        if (!FormsCommon.Matches(http, form.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        var name = FormsCommon.Clean(request.Name);
        var description = FormsCommon.Clean(request.Description);
        if (FormsCommon.CheckNames(name, description) is { } invalid)
        {
            return invalid;
        }

        if (request.Key?.Trim() is { } key && key != form.Key)
        {
            if (!FormsCommon.KeyPattern().IsMatch(key))
            {
                return UserEndpoints.Validation("key", "Use small letters, digits and dashes, for example laptop-problem.");
            }

            if (await db.Forms.AnyAsync(f => f.Key == key && f.Id != id, cancellationToken))
            {
                return FormsCommon.KeyTaken();
            }

            form.Key = key;
        }

        form.Name = name ?? form.Name;
        form.Description = description ?? form.Description;
        form.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FormResponse>, ValidationProblem, ProblemHttpResult>> SaveDraftAsync(
        Guid id, SaveDraftRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (form is null)
        {
            return FormsCommon.NotFound("form");
        }

        if (!FormsCommon.Matches(http, form.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (form.State == FormState.Retired)
        {
            return FormsCommon.State("Reactivate the form before changing it.");
        }

        var schema = FormsCommon.Canonical(request.Schema, FormJson.ReadForm);
        if (schema is null)
        {
            return UserEndpoints.Validation("schema", "The elements are not in the expected form, or are too large.");
        }

        if (schema != form.DraftSchema)
        {
            form.DraftSchema = schema;
            form.HasUnpublishedChanges = form.PublishedVersionId is not { } published
                || schema != await db.FormVersions.Where(v => v.Id == published).Select(v => v.Schema).SingleAsync(cancellationToken);
            form.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FormResponse>, ProblemHttpResult>> PublishAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (form is null)
        {
            return FormsCommon.NotFound("form");
        }

        if (!FormsCommon.Matches(http, form.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (form.State == FormState.Retired)
        {
            return FormsCommon.State("Reactivate the form before publishing it.");
        }

        if (!form.HasUnpublishedChanges && form.State == FormState.Published)
        {
            return FormsCommon.State("There are no changes to publish.");
        }

        var schema = FormJson.ReadForm(form.DraftSchema) ?? new FormSchema();
        var (_, problems) = await FormsCommon.ResolveAsync(db, schema, cancellationToken);
        if (problems.Count > 0)
        {
            return FormsCommon.Problems("form-has-problems", "Fix the form's problems before publishing it.", problems);
        }

        var number = (await db.FormVersions.Where(v => v.FormId == id).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
        var version = new FormVersion { FormId = id, Number = number, Schema = form.DraftSchema, PublishedById = user.Account?.Id };
        db.FormVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        form.PublishedVersionId = version.Id;
        form.State = FormState.Published;
        form.HasUnpublishedChanges = false;
        form.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "forms.form.published", "form", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = number, ["key"] = form.Key });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FormResponse>, ProblemHttpResult>> DiscardAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (form is null)
        {
            return FormsCommon.NotFound("form");
        }

        if (form.PublishedVersionId is not { } published)
        {
            return FormsCommon.State("The form was never published; delete it instead.");
        }

        form.DraftSchema = await db.FormVersions.Where(v => v.Id == published).Select(v => v.Schema).SingleAsync(cancellationToken);
        form.HasUnpublishedChanges = false;
        form.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<FormResponse>, ProblemHttpResult>> RetireAsync(
        Guid id, HttpContext http, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken) =>
        await SetStateAsync(id, FormState.Published, FormState.Retired, "forms.form.retired", "Only published forms can be retired.", http, db, audit, cancellationToken);

    internal static async Task<Results<Ok<FormResponse>, ProblemHttpResult>> ReactivateAsync(
        Guid id, HttpContext http, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken) =>
        await SetStateAsync(id, FormState.Retired, FormState.Published, "forms.form.reactivated", "Only retired forms can be reactivated.", http, db, audit, cancellationToken);

    private static async Task<Results<Ok<FormResponse>, ProblemHttpResult>> SetStateAsync(
        Guid id, FormState from, FormState to, string action, string refused, HttpContext http, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (form is null)
        {
            return FormsCommon.NotFound("form");
        }

        if (form.State != from)
        {
            return FormsCommon.State(refused);
        }

        form.State = to;
        form.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, action, "form", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ReloadAsync(http, db, id, cancellationToken));
    }

    internal static async Task<Results<Created<FormResponse>, ValidationProblem, ProblemHttpResult>> CopyAsync(
        Guid id, CopyFormRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var source = await db.Forms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (source is null)
        {
            return FormsCommon.NotFound("form");
        }

        return await CreateAsync(new CreateFormRequest
        {
            Key = request.Key,
            Name = request.Name ?? new LocalizedTextInput { En = source.Name.En, Ar = source.Name.Ar },
            Description = new UpdateDescription { En = source.Description.En, Ar = source.Description.Ar },
            Schema = JsonNode.Parse(source.DraftSchema) as JsonObject,
        }, http, db, user, cancellationToken);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (form is null)
        {
            return FormsCommon.NotFound("form");
        }

        if (form.PublishedVersionId is not null || await db.FormVersions.AnyAsync(v => v.FormId == id, cancellationToken))
        {
            return FormsCommon.State("Published forms are kept for the tickets that used them; retire it instead.");
        }

        db.FormBindings.RemoveRange(await db.FormBindings.Where(b => b.FormId == id).ToListAsync(cancellationToken));
        db.Forms.Remove(form);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<List<FormVersionSummary>>, ProblemHttpResult>> VersionsAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Forms.AnyAsync(f => f.Id == id, cancellationToken))
        {
            return FormsCommon.NotFound("form");
        }

        var versions = await db.FormVersions.AsNoTracking().Where(v => v.FormId == id).OrderByDescending(v => v.Number)
            .Select(v => new { v.Number, v.PublishedAt, v.PublishedById }).ToListAsync(cancellationToken);
        var names = await PeopleNamesAsync(db, versions.Where(v => v.PublishedById is not null).Select(v => v.PublishedById!.Value), cancellationToken);
        return TypedResults.Ok(versions.Select(v => new FormVersionSummary(v.Number, v.PublishedAt,
            v.PublishedById is { } p ? new PersonRef(p, names.GetValueOrDefault(p)) : null)).ToList());
    }

    internal static async Task<Results<Ok<FormVersionResponse>, ProblemHttpResult>> VersionAsync(Guid id, int number, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var version = await db.FormVersions.AsNoTracking().SingleOrDefaultAsync(v => v.FormId == id && v.Number == number, cancellationToken);
        return version is null
            ? FormsCommon.NotFound("form-version")
            : TypedResults.Ok(new FormVersionResponse(version.Number, version.PublishedAt, FormsCommon.Node(version.Schema)));
    }

    internal static async Task<Results<Ok<ResolvedFormResponse>, ValidationProblem, ProblemHttpResult>> ResolveAsync(
        [AsParameters] ResolveFormQuery query, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var recordType = query.RecordType ?? TicketType.Incident;
        if (user.Account is null && query.RequesterId is null)
        {
            // Integrations get the form without defaults.
            var form = await FormResolver.ForTicketAsync(db, recordType, query.CategoryId, cancellationToken);
            return TypedResults.Ok(await ResolvedResponseAsync(db, form, [], query.Language, cancellationToken));
        }

        UserAccount? requester = user.Account;
        if (query.RequesterId is { } requesterId && requesterId != user.Account?.Id)
        {
            if (!user.Has(Permissions.IncidentsCreate))
            {
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff who log incidents for others may name the requester.");
            }

            requester = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == requesterId && u.Status == UserStatus.Active, cancellationToken);
            if (requester is null)
            {
                return UserEndpoints.Validation("requesterId", "The requester is not an active user of this organisation.");
            }
        }

        var resolved = await FormResolver.ForTicketAsync(db, recordType, query.CategoryId, cancellationToken);
        var defaults = resolved is null ? [] : await FormSubmission.DefaultsForAsync(db, user, audit, resolved.PlainFields, requester, query.Language, cancellationToken);
        return TypedResults.Ok(await ResolvedResponseAsync(db, resolved, defaults, query.Language, cancellationToken));
    }

    internal static async Task<Results<Ok<List<PersonRef>>, ValidationProblem>> PeopleAsync(
        [AsParameters] PeopleQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var ids = (query.Ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Take(50).ToList();
        if (ids.Count > 0)
        {
            return TypedResults.Ok(await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
                .Select(u => new PersonRef(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
        }

        var q = query.Q?.Trim() ?? string.Empty;
        if (q.Length < 2)
        {
            return UserEndpoints.Validation("q", "Type at least 2 characters of the name.");
        }

        var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return TypedResults.Ok(await db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active && u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern))
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(20)
            .Select(u => new PersonRef(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
    }

    internal static async Task<ResolvedFormResponse> ResolvedResponseAsync(ZelvoDbContext db, ResolvedForm? form, JsonObject defaults, string? language, CancellationToken cancellationToken)
    {
        if (form is null)
        {
            return new ResolvedFormResponse(null, [], new JsonObject(), FormReferences.Empty);
        }

        var references = await FormReferences.LoadAsync(db, form.PlainFields, defaults, [], language, cancellationToken);
        return new ResolvedFormResponse(
            new FormRef(form.Form.Id, form.Form.Key, LocalizedTextDto.From(form.Form.Name), LocalizedTextDto.From(form.Form.Description), form.Version.Id, form.Version.Number),
            [.. form.Fields.Select(f => JsonSerializer.SerializeToNode(f, FormJson.Options)!)], defaults, references);
    }

    internal static async Task<FormResponse> ReloadAsync(HttpContext http, ZelvoDbContext db, Guid id, CancellationToken cancellationToken) =>
        await ResponseAsync(http, db, await db.Forms.AsNoTracking().SingleAsync(f => f.Id == id, cancellationToken), cancellationToken);

    private static async Task<FormResponse> ResponseAsync(HttpContext http, ZelvoDbContext db, Form form, CancellationToken cancellationToken)
    {
        var schema = FormJson.ReadForm(form.DraftSchema) ?? new FormSchema();
        var (fields, problems) = await FormsCommon.ResolveAsync(db, schema, cancellationToken);
        var version = form.PublishedVersionId is { } v ? await db.FormVersions.AsNoTracking().SingleAsync(x => x.Id == v, cancellationToken) : null;
        var bindings = await BindingsAsync(db, form.Id, cancellationToken);
        FormsCommon.SetETag(http, form.Version);
        return new FormResponse(form.Id, form.Key, LocalizedTextDto.From(form.Name), LocalizedTextDto.From(form.Description), form.State,
            form.HasUnpublishedChanges, version?.Number, version?.PublishedAt, FormsCommon.Node(form.DraftSchema),
            [.. fields.Select(f => JsonSerializer.SerializeToNode(f, FormJson.Options)!)], problems, [.. bindings[form.Id]], form.UpdatedAt, form.Version);
    }

    /// <summary>Where each form is used, by form.</summary>
    private static async Task<ILookup<Guid, FormBindingRef>> BindingsAsync(ZelvoDbContext db, Guid? formId, CancellationToken cancellationToken)
    {
        var bindings = await db.FormBindings.AsNoTracking().Where(b => formId == null || b.FormId == formId).ToListAsync(cancellationToken);
        var categories = new TreeIndex<Data.Directory.Category>(bindings.Any(b => b.CategoryId is not null) ? await db.Categories.AsNoTracking().ToListAsync(cancellationToken) : []);
        return bindings.ToLookup(b => b.FormId, b => new FormBindingRef(b.Id, b.RecordType, categories.RefOf(b.CategoryId)));
    }

    internal static async Task<Dictionary<Guid, string?>> PeopleNamesAsync(ZelvoDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }
}

/// <summary>Which form to resolve.</summary>
public sealed class ResolveFormQuery
{
    /// <summary>The record type (default Incident).</summary>
    public TicketType? RecordType { get; init; }

    /// <summary>The ticket's category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Staff: the person the ticket is for (default: the caller).</summary>
    public Guid? RequesterId { get; init; }

    /// <summary>Language of department names in the defaults (<c>en</c> or <c>ar</c>).</summary>
    [RegularExpression("^(en|ar)$")]
    public string? Language { get; init; }
}

/// <summary>People to find.</summary>
public sealed class PeopleQuery
{
    /// <summary>Part of the name.</summary>
    [MaxLength(100)]
    public string? Q { get; init; }

    /// <summary>Or: ids separated by commas (at most 50), to show names.</summary>
    [MaxLength(2000)]
    public string? Ids { get; init; }
}

/// <summary>Where a form is used.</summary>
internal sealed record FormBindingRef(Guid Id, TicketType RecordType, NodeRef? Category);

/// <summary>A form in the list.</summary>
internal sealed record FormSummaryResponse(
    Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, FormState State, bool HasUnpublishedChanges,
    int? PublishedVersion, DateTimeOffset? PublishedAt, int ElementCount, IReadOnlyList<FormBindingRef> UsedBy, DateTimeOffset UpdatedAt);

/// <summary>A form with its draft.</summary>
/// <param name="Draft">The draft's elements (<c>{"elements": [...]}</c>).</param>
/// <param name="DraftFields">The draft's fields with field sets expanded as published now.</param>
/// <param name="Problems">What stops the draft from being published.</param>
/// <param name="ETag">Version for If-Match (also in the ETag header).</param>
internal sealed record FormResponse(
    Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, FormState State, bool HasUnpublishedChanges,
    int? PublishedVersion, DateTimeOffset? PublishedAt, JsonNode? Draft, IReadOnlyList<JsonNode> DraftFields, IReadOnlyList<FormProblem> Problems,
    IReadOnlyList<FormBindingRef> UsedBy, DateTimeOffset UpdatedAt, uint ETag);

/// <summary>A published version's elements.</summary>
internal sealed record FormVersionResponse(int Number, DateTimeOffset PublishedAt, JsonNode? Schema);

/// <summary>A form to fill.</summary>
internal sealed record FormRef(Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, Guid VersionId, int VersionNumber);

/// <summary>The form a new ticket uses.</summary>
/// <param name="Form">The form; null when no form applies.</param>
/// <param name="Fields">Its fields in order (field sets expanded), each with <c>field</c> and <c>fieldSetId</c>.</param>
/// <param name="Defaults">Default answers by field key (profile values first).</param>
/// <param name="References">Names of users and paths of locations the defaults refer to.</param>
internal sealed record ResolvedFormResponse(FormRef? Form, IReadOnlyList<JsonNode> Fields, JsonObject Defaults, FormReferences References);
