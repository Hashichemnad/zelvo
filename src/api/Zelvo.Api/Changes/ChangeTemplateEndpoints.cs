using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Changes;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Changes;

/// <summary>A task of a standard change template.</summary>
internal sealed record TemplateTaskResponse(string Key, LocalizedTextDto Name, LocalizedTextDto Description, NamedRef? Group, bool ParallelWithPrevious, int? TargetMinutes);

/// <summary>A standard change template.</summary>
internal sealed record ChangeTemplateResponse(
    Guid Id, LocalizedTextDto Name, LocalizedTextDto Description, NodeRef? Category, NamedRef? Group, string? ImplementationPlan, string? TestPlan,
    string? BackoutPlan, IReadOnlyList<TemplateTaskResponse> Tasks, bool Active, int ChangeCount, string Etag, DateTimeOffset UpdatedAt);

/// <summary>The catalog of standard changes (task H3): pre-approved changes of a known kind, with their plans and tasks.</summary>
internal static class ChangeTemplateEndpoints
{
    public static RouteGroupBuilder MapChangeTemplateEndpoints(this RouteGroupBuilder group)
    {
        var templates = group.MapGroup("/change-templates").WithTags("Changes").RequireTenant();
        templates.MapGet("/", ListAsync).WithName("ListChangeTemplates")
            .WithSummary("The catalog of standard changes; with all=true also inactive ones. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeTemplateResponse>>();
        templates.MapGet("/{id:guid}", GetAsync).WithName("GetChangeTemplate")
            .WithSummary("One standard change template. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<ChangeTemplateResponse>();
        templates.MapPost("/", CreateAsync).WithName("CreateChangeTemplate")
            .WithSummary("Add a standard change template: bilingual name and description, category, group, plans and up to 30 tasks. Needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<ChangeTemplateResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        templates.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateChangeTemplate")
            .WithSummary("Replace a standard change template; changes made from it keep what they copied. Needs changes.manage; If-Match; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<ChangeTemplateResponse>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> ListAsync(bool? all, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.ChangeTemplates.AsNoTracking();
        if (all != true)
        {
            query = query.Where(t => t.Active);
        }

        return TypedResults.Ok(await ResponsesAsync(db, await query.Take(500).ToListAsync(cancellationToken), cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.ChangeTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken) is { } template
            ? TypedResults.Ok((await ResponsesAsync(db, [template], cancellationToken)).Single())
            : FormsCommon.NotFound("change-template");

    internal static async Task<IResult> CreateAsync(ChangeTemplateRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var template = new ChangeTemplate { Name = new LocalizedText(null, null) };
        if (await ApplyAsync(db, template, request, cancellationToken) is { } problem)
        {
            return problem;
        }

        db.ChangeTemplates.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/change-templates/{template.Id}", (await ResponsesAsync(db, [template], cancellationToken)).Single());
    }

    internal static async Task<IResult> UpdateAsync(Guid id, ChangeTemplateRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var template = await db.ChangeTemplates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return FormsCommon.NotFound("change-template");
        }

        if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } match && match != $"\"{template.Version}\"")
        {
            return FormsCommon.VersionMismatch();
        }

        if (await ApplyAsync(db, template, request, cancellationToken) is { } problem)
        {
            return problem;
        }

        template.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        return TypedResults.Ok((await ResponsesAsync(db, [template], cancellationToken)).Single());
    }

    private static async Task<IResult?> ApplyAsync(ZelvoDbContext db, ChangeTemplate template, ChangeTemplateRequest request, CancellationToken cancellationToken)
    {
        var name = new LocalizedText(request.Name?.En, request.Name?.Ar);
        if (!name.HasAnyValue)
        {
            return UserEndpoints.Validation("name", "Name the standard change in English or Arabic.");
        }

        if (request.CategoryId is { } category && category != template.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        var groups = (request.Tasks ?? []).Select(t => t.GroupId).Append(request.GroupId).OfType<Guid>().Distinct().ToList();
        if (await db.SupportGroups.CountAsync(g => groups.Contains(g.Id) && g.Active, cancellationToken) != groups.Count)
        {
            return UserEndpoints.Validation("groupId", "Choose active groups.");
        }

        var tasks = new List<FulfilmentTask>();
        foreach (var (task, i) in (request.Tasks ?? []).Select((t, i) => (t, i)))
        {
            var taskName = new LocalizedText(task.Name?.En, task.Name?.Ar);
            if (!taskName.HasAnyValue)
            {
                return UserEndpoints.Validation($"tasks.{i}.name", "Name the task in English or Arabic.");
            }

            tasks.Add(new FulfilmentTask
            {
                Key = $"task{i + 1}",
                Name = taskName,
                Description = new LocalizedText(task.Description?.En, task.Description?.Ar),
                GroupId = task.GroupId,
                TargetMinutes = task.TargetMinutes,
                ParallelWithPrevious = i > 0 && task.ParallelWithPrevious == true,
            });
        }

        template.Name = name;
        template.Description = new LocalizedText(request.Description?.En, request.Description?.Ar);
        template.CategoryId = request.CategoryId;
        template.GroupId = request.GroupId;
        template.ImplementationPlan = ChangeCore.Clean(request.ImplementationPlan);
        template.TestPlan = ChangeCore.Clean(request.TestPlan);
        template.BackoutPlan = ChangeCore.Clean(request.BackoutPlan);
        template.Tasks = ChangeCore.WriteTasks(tasks);
        template.Active = request.Active ?? true;
        return null;
    }

    private static async Task<List<ChangeTemplateResponse>> ResponsesAsync(ZelvoDbContext db, IReadOnlyList<ChangeTemplate> templates, CancellationToken cancellationToken)
    {
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        var ids = templates.Select(t => t.Id).ToList();
        var counts = await db.Changes.AsNoTracking().Where(c => c.TemplateId != null && ids.Contains(c.TemplateId.Value)).GroupBy(c => c.TemplateId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        return [.. templates
            .OrderBy(t => t.Name.En ?? t.Name.Ar, StringComparer.CurrentCulture)
            .Select(t => new ChangeTemplateResponse(
                t.Id, LocalizedTextDto.From(t.Name), LocalizedTextDto.From(t.Description), refs.Categories.RefOf(t.CategoryId), refs.Group(t.GroupId),
                t.ImplementationPlan, t.TestPlan, t.BackoutPlan,
                [.. ChangeCore.ReadTasks(t.Tasks).Select(k => new TemplateTaskResponse(k.Key, LocalizedTextDto.From(k.Name), LocalizedTextDto.From(k.Description),
                    refs.Group(k.GroupId), k.ParallelWithPrevious, k.TargetMinutes))],
                t.Active, counts.GetValueOrDefault(t.Id), $"\"{t.Version}\"", t.UpdatedAt))];
    }
}

/// <summary>A standard change template.</summary>
public sealed class ChangeTemplateRequest
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public TemplateText? Name { get; init; }

    /// <summary>What it does and when to use it.</summary>
    public TemplateLongText? Description { get; init; }

    /// <summary>Category of its changes.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>The group that carries it out.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Implementation plan.</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? ImplementationPlan { get; init; }

    /// <summary>Test plan.</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? TestPlan { get; init; }

    /// <summary>Back-out plan.</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? BackoutPlan { get; init; }

    /// <summary>The tasks, in order.</summary>
    [MaxLength(ChangeTemplate.MaxTasks)]
    public IReadOnlyList<ChangeTaskRequest>? Tasks { get; init; }

    /// <summary>Offered in the catalog (default true).</summary>
    public bool? Active { get; init; }
}

/// <summary>A template's bilingual name.</summary>
public sealed class TemplateText
{
    /// <summary>English.</summary>
    [StringLength(ChangeTemplate.NameMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(ChangeTemplate.NameMaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A template's bilingual description.</summary>
public sealed class TemplateLongText
{
    /// <summary>English.</summary>
    [StringLength(ChangeTemplate.DescriptionMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(ChangeTemplate.DescriptionMaxLength)]
    public string? Ar { get; init; }
}
