using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Forms;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Forms;

/// <summary>
/// Which form each record type and category uses (task C1, module 2: "each category can ask its
/// own questions"). A binding without a category is the record type's default; a binding on a
/// category also covers the categories below it unless they have their own. Needs <c>forms.manage</c>; audited.
/// </summary>
internal static class FormBindingEndpoints
{
    public static RouteGroupBuilder MapFormBindingEndpoints(this RouteGroupBuilder group)
    {
        var bindings = group.MapGroup("/form-bindings").WithTags("Forms").RequireTenant().RequirePermission(Permissions.FormsManage);
        bindings.MapGet("/", ListAsync).WithName("ListFormBindings")
            .WithSummary("Which form each record type and category uses, defaults first. Needs forms.manage.")
            .Produces<IReadOnlyList<FormBindingResponse>>();
        bindings.MapPut("/", SetAsync).WithName("SetFormBinding")
            .WithSummary("Use a form for a record type (no category: its default) or for a category and the ones below it. Replaces the form there before. Needs forms.manage; audited.")
            .Produces<FormBindingResponse>().ProducesValidationProblem();
        bindings.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteFormBinding")
            .WithSummary("Stop using a form there; the category above or the default applies. Needs forms.manage; audited.")
            .Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<Ok<List<FormBindingResponse>>> ListAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var bindings = await db.FormBindings.AsNoTracking().ToListAsync(cancellationToken);
        var forms = await db.Forms.AsNoTracking().ToDictionaryAsync(f => f.Id, cancellationToken);
        var categories = new TreeIndex<Category>(await db.Categories.AsNoTracking().ToListAsync(cancellationToken));
        return TypedResults.Ok(bindings
            .OrderBy(b => b.RecordType).ThenBy(b => b.CategoryId is null ? 0 : 1)
            .ThenBy(b => b.CategoryId is { } c && categories.Find(c) is { } node ? categories.PathOf(node).En : null, StringComparer.OrdinalIgnoreCase)
            .Select(b => Response(b, forms[b.FormId], categories)).ToList());
    }

    internal static async Task<Results<Ok<FormBindingResponse>, ValidationProblem, ProblemHttpResult>> SetAsync(
        SetFormBindingRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var form = await db.Forms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == request.FormId, cancellationToken);
        if (form is null || form.State == FormState.Retired)
        {
            return UserEndpoints.Validation("formId", "Choose a form that is not retired.");
        }

        if (request.CategoryId is { } categoryId && !await db.Categories.AnyAsync(c => c.Id == categoryId, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "The category does not exist.");
        }

        var recordType = request.RecordType ?? TicketType.Incident;
        var binding = await db.FormBindings.SingleOrDefaultAsync(b => b.RecordType == recordType && b.CategoryId == request.CategoryId, cancellationToken);
        if (binding is null)
        {
            binding = new FormBinding { RecordType = recordType, CategoryId = request.CategoryId, FormId = form.Id };
            db.FormBindings.Add(binding);
        }
        else
        {
            binding.FormId = form.Id;
            binding.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        var categories = new TreeIndex<Category>(await db.Categories.AsNoTracking().ToListAsync(cancellationToken));
        return TypedResults.Ok(Response(binding, form, categories));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var binding = await db.FormBindings.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (binding is null)
        {
            return FormsCommon.NotFound("form-binding");
        }

        db.FormBindings.Remove(binding);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static FormBindingResponse Response(FormBinding binding, Form form, TreeIndex<Category> categories) =>
        new(binding.Id, binding.RecordType, categories.RefOf(binding.CategoryId),
            new FormUseRef(form.Id, form.Key, LocalizedTextDto.From(form.Name), form.State));
}

/// <summary>Which form to use where.</summary>
public sealed class SetFormBindingRequest
{
    /// <summary>The record type (default Incident).</summary>
    public TicketType? RecordType { get; init; }

    /// <summary>The category; leave out for the record type's default.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>The form (draft or published; drafts apply once published).</summary>
    [Required]
    public Guid? FormId { get; init; }
}

/// <summary>A form used for a record type and category.</summary>
internal sealed record FormBindingResponse(Guid Id, TicketType RecordType, NodeRef? Category, FormUseRef Form);
