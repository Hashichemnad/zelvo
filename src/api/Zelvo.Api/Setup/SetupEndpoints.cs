using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Workspace;

namespace Zelvo.Api.Setup;

/// <summary>A step of the setup wizard and where it is.</summary>
public enum SetupStepStatus
{
    /// <summary>Not done yet.</summary>
    Pending,

    /// <summary>Marked done.</summary>
    Done,

    /// <summary>Skipped for now.</summary>
    Skipped,
}

/// <summary>A template applied by the wizard.</summary>
/// <param name="Template">Template key.</param>
/// <param name="DepartmentId">The department it made.</param>
/// <param name="AppliedAt">When.</param>
public sealed record AppliedTemplate(string Template, Guid DepartmentId, DateTimeOffset AppliedAt);

/// <summary>Progress of the setup wizard (tenant setting <c>setup.progress</c>).</summary>
/// <param name="Steps">Status per step key.</param>
/// <param name="CompletedAt">When the administrator finished the wizard; null while it is shown.</param>
/// <param name="Applied">Department templates applied.</param>
public sealed record SetupProgress(IReadOnlyDictionary<string, SetupStepStatus> Steps, DateTimeOffset? CompletedAt, IReadOnlyList<AppliedTemplate> Applied)
{
    /// <summary>Nothing done.</summary>
    public static SetupProgress Empty { get; } = new(new Dictionary<string, SetupStepStatus>(), null, []);
}

/// <summary>
/// The setup wizard (module 10, task F2): the steps that make a new tenant live in under a working
/// day, each with a check of what is already set up, and department templates that create a whole
/// department's service desk at once. The wizard guides; each step's settings are changed on their
/// own Settings pages through their own APIs. License activation joins with task F3. Needs
/// <c>setup.manage</c> (tenant administrators; not for integrations, for approval).
/// </summary>
internal static partial class SetupEndpoints
{
    /// <summary>The wizard's steps in order.</summary>
    public static IReadOnlyList<string> Steps { get; } =
        ["organisation", "language", "calendar", "branding", "signIn", "departments", "users", "channels", "ai"];

    public static SettingDefinition<SetupProgress> Progress { get; } =
        new("setup.progress", SetupProgress.Empty, Permissions.SetupManage, p => p.Steps is not null && p.Applied is not null);

    public static RouteGroupBuilder MapSetupEndpoints(this RouteGroupBuilder group)
    {
        var setup = group.MapGroup("/setup").WithTags("Setup").RequireTenant();
        setup.MapGet("/", GetAsync).WithName("GetSetup")
            .WithSummary("The setup wizard: each step with its status (Pending, Done, Skipped) and a check of what is already set up, when the wizard was finished, and the department templates applied. Needs setup.manage.")
            .RequirePermission(Permissions.SetupManage).Produces<SetupResponse>();
        setup.MapPut("/steps/{key}", StepAsync).WithName("UpdateSetupStep")
            .WithSummary("Mark a step Done, Skipped or Pending again. Needs setup.manage; audited.")
            .RequirePermission(Permissions.SetupManage).Produces<SetupResponse>().ProducesValidationProblem();
        setup.MapPost("/finish", FinishAsync).WithName("FinishSetup")
            .WithSummary("Finish the wizard (steps not done stay as they are). Needs setup.manage; audited.")
            .RequirePermission(Permissions.SetupManage).Produces<SetupResponse>();
        setup.MapPost("/reopen", ReopenAsync).WithName("ReopenSetup")
            .WithSummary("Show the wizard again. Needs setup.manage; audited.")
            .RequirePermission(Permissions.SetupManage).Produces<SetupResponse>();
        setup.MapGet("/templates", TemplatesAsync).WithName("ListDepartmentTemplates")
            .WithSummary("Zelvo's department templates (IT service desk, human resources, facilities, customer happiness centre) with everything each creates, in English and Arabic. Needs setup.manage.")
            .RequirePermission(Permissions.SetupManage).Produces<IReadOnlyList<DepartmentTemplateResponse>>();
        setup.MapPost("/templates/{key}/apply", ApplyAsync).WithName("ApplyDepartmentTemplate")
            .WithSummary("Create a department from a template in one transaction: the department, its groups (names can be changed), a category tree under a root category named after the department, assignment rules from each category to its group, SLA policies for those categories (placed first, so they apply before broader policies), and optionally a catalog with draft services and canned responses. Never changes what exists; apply again for another department. Needs setup.manage; audited.")
            .RequirePermission(Permissions.SetupManage).Produces<AppliedTemplateResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> GetAsync(ZelvoDbContext db, TenantSettingsStore store, ITenantContext tenant, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ResponseAsync(db, store, tenant, cancellationToken));

    internal static async Task<IResult> StepAsync(
        string key, SetupStepRequest request, ZelvoDbContext db, TenantSettingsStore store, ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!Steps.Contains(key))
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "step-not-found", "There is no such setup step.");
        }

        if (request.Status is not { } status || !Enum.IsDefined(status))
        {
            return UserEndpoints.Validation("status", "Choose Done, Skipped or Pending.");
        }

        var progress = await store.GetAsync(Progress, cancellationToken);
        var steps = new Dictionary<string, SetupStepStatus>(progress.Steps) { [key] = status };
        await store.SetAsync(Progress, progress with { Steps = steps }, cancellationToken);
        audit.Add(AuditCategory.Admin, "setup.step.updated", "setup", key, new Dictionary<string, object?> { ["status"] = status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, store, tenant, cancellationToken));
    }

    internal static async Task<IResult> FinishAsync(
        ZelvoDbContext db, TenantSettingsStore store, ITenantContext tenant, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var progress = await store.GetAsync(Progress, cancellationToken);
        await store.SetAsync(Progress, progress with { CompletedAt = time.GetUtcNow() }, cancellationToken);
        audit.Add(AuditCategory.Admin, "setup.finished", "setup");
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, store, tenant, cancellationToken));
    }

    internal static async Task<IResult> ReopenAsync(
        ZelvoDbContext db, TenantSettingsStore store, ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        var progress = await store.GetAsync(Progress, cancellationToken);
        await store.SetAsync(Progress, progress with { CompletedAt = null }, cancellationToken);
        audit.Add(AuditCategory.Admin, "setup.reopened", "setup");
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, store, tenant, cancellationToken));
    }

    internal static IResult TemplatesAsync() => TypedResults.Ok(DepartmentTemplates.All.Select(DepartmentTemplateResponse.From).ToList());

    internal static async Task<IResult> ApplyAsync(
        string key, ApplyTemplateRequest? request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore store, IAuditLog audit, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var template = DepartmentTemplates.Find(key);
        if (template is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "template-not-found", "There is no such department template.");
        }

        var departmentName = Text(request?.DepartmentName) ?? template.Name;
        if (!Fits(departmentName, Department.NameMaxLength))
        {
            return UserEndpoints.Validation("departmentName", $"Name the department in English or Arabic, up to {Department.NameMaxLength} characters.");
        }

        var groupNames = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
        foreach (var group in template.Groups)
        {
            var name = request?.GroupNames is { } names && names.TryGetValue(group.Key, out var given) ? Text(given) ?? group.Name : group.Name;
            if (!Fits(name, SupportGroup.NameMaxLength))
            {
                return UserEndpoints.Validation("groupNames", $"Name each group in English or Arabic, up to {SupportGroup.NameMaxLength} characters.");
            }

            groupNames[group.Key] = name;
        }

        var prefix = (request?.CodePrefix ?? template.CodePrefix).Trim().ToUpperInvariant();
        if (!PrefixPattern().IsMatch(prefix))
        {
            return UserEndpoints.Validation("codePrefix", "Use 2–10 capital letters or digits for the service code prefix, for example IT.");
        }

        var includeServices = request?.IncludeServices ?? true;
        var includeResponses = request?.IncludeResponses ?? true;
        var me = user.Account!;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var department = new Department { Name = departmentName };
        db.Departments.Add(department);

        var groups = new Dictionary<string, SupportGroup>(StringComparer.Ordinal);
        foreach (var group in template.Groups)
        {
            var created = new SupportGroup { Name = groupNames[group.Key] };
            db.SupportGroups.Add(created);
            groups[group.Key] = created;
        }

        // The department's own category tree: a new root named after it, so nothing existing changes.
        var rootPosition = await db.Categories.Where(c => c.ParentId == null).Select(c => (int?)c.Position).MaxAsync(cancellationToken) ?? -1;
        var root = new Category { Name = departmentName, Depth = 1, Position = rootPosition + 1 };
        db.Categories.Add(root);
        var routed = new List<(Category Category, SupportGroup Group)>();
        for (var i = 0; i < template.Categories.Count; i++)
        {
            var item = template.Categories[i];
            var category = new Category { Name = item.Name, ParentId = root.Id, Depth = 2, Position = i };
            db.Categories.Add(category);
            for (var j = 0; j < item.Children.Count; j++)
            {
                db.Categories.Add(new Category { Name = item.Children[j], ParentId = category.Id, Depth = 3, Position = j });
            }

            routed.Add((category, groups[item.Group]));
        }

        // Rules and policies for the new categories go first: they match nothing that exists, so the
        // order of the tenant's other rules and policies is kept, and they are not shadowed by broad ones.
        await db.AssignmentRules.ExecuteUpdateAsync(set => set.SetProperty(r => r.Position, r => r.Position + routed.Count), cancellationToken);
        for (var i = 0; i < routed.Count; i++)
        {
            var (category, group) = routed[i];
            db.AssignmentRules.Add(new AssignmentRule
            {
                Name = Join(departmentName, category.Name), Position = i, CategoryId = category.Id, GroupId = group.Id, Method = AssignmentMethod.GroupQueue,
            });
        }

        await db.SlaPolicies.ExecuteUpdateAsync(set => set.SetProperty(p => p.Position, p => p.Position + template.Policies.Count), cancellationToken);
        for (var i = 0; i < template.Policies.Count; i++)
        {
            var item = template.Policies[i];
            var policy = new SlaPolicy { Name = Join(departmentName, RecordTypeName(item.RecordType)), Position = i, RecordType = item.RecordType, CategoryId = root.Id };
            foreach (var (priority, response, resolution) in item.Targets)
            {
                policy.Targets.Add(new SlaPolicyTarget { PolicyId = policy.Id, Priority = priority, ResponseMinutes = response, ResolutionMinutes = resolution });
            }

            policy.Escalations.Add(new SlaEscalation { PolicyId = policy.Id, Percent = 75, NotifyAssignee = true, NotifyGroupLeads = true });
            policy.Escalations.Add(new SlaEscalation { PolicyId = policy.Id, Percent = 100, NotifyAssignee = true, NotifyGroupLeads = true });
            db.SlaPolicies.Add(policy);
        }

        var services = new List<Guid>();
        Guid? catalogId = null;
        if (includeServices && template.Services.Count > 0)
        {
            var catalogKey = await FreeCatalogKeyAsync(db, template.Key, cancellationToken);
            var catalog = new ServiceCatalog
            {
                Key = catalogKey, Name = departmentName, DepartmentId = department.Id,
                Position = (await db.ServiceCatalogs.Select(c => (int?)c.Position).MaxAsync(cancellationToken) ?? -1) + 1,
            };
            db.ServiceCatalogs.Add(catalog);
            catalogId = catalog.Id;
            foreach (var item in template.Services)
            {
                var card = ServiceTemplates.BuiltIn.FirstOrDefault(t => t.Key == item.Template).Card;
                if (card is null)
                {
                    continue;
                }

                card = card with { CatalogIds = [catalog.Id], CategoryIds = [root.Id] };
                if (item.Name is { } name)
                {
                    card = card with { Name = name };
                }

                var code = await FreeCodeAsync(db, $"{prefix}-{item.Code}", cancellationToken);
                services.Add((await ServiceEndpoints.NewDraftAsync(db, user, code, card, cancellationToken)).Id);
            }
        }

        var responses = 0;
        if (includeResponses)
        {
            var first = groups[template.Groups[0].Key];
            foreach (var item in template.Responses)
            {
                db.CannedResponses.Add(new CannedResponse { Scope = WorkspaceScope.Group, GroupId = first.Id, Title = item.Title, Body = item.Body, CreatedById = me.Id });
                responses++;
            }
        }

        var progress = await store.GetAsync(Progress, cancellationToken);
        await store.SetAsync(Progress, progress with
        {
            Applied = [.. progress.Applied, new AppliedTemplate(template.Key, department.Id, time.GetUtcNow())],
            Steps = new Dictionary<string, SetupStepStatus>(progress.Steps) { ["departments"] = SetupStepStatus.Done },
        }, cancellationToken);
        audit.Add(AuditCategory.Admin, "setup.template.applied", "department", department.Id.ToString(), new Dictionary<string, object?>
        {
            ["template"] = template.Key, ["groups"] = groups.Count, ["categories"] = routed.Count, ["policies"] = template.Policies.Count, ["services"] = services.Count,
            ["responses"] = responses,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Created($"/api/v1/departments/{department.Id}", new AppliedTemplateResponse(
            template.Key, department.Id, [.. groups.Values.Select(g => g.Id)], root.Id, routed.Count, template.Policies.Count, catalogId, services, responses));
    }

    private static async Task<SetupResponse> ResponseAsync(ZelvoDbContext db, TenantSettingsStore store, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var progress = await store.GetAsync(Progress, cancellationToken);
        var checks = await ChecksAsync(db, tenant, cancellationToken);
        var steps = Steps.Select(key => new SetupStepResponse(key, progress.Steps.TryGetValue(key, out var s) ? s : SetupStepStatus.Pending, checks.GetValueOrDefault(key))).ToList();
        return new SetupResponse(steps, progress.CompletedAt, steps.Count(s => s.Status != SetupStepStatus.Pending), steps.Count,
            [.. progress.Applied.Select(a => new AppliedTemplateSummary(a.Template, a.DepartmentId, a.AppliedAt))]);
    }

    // What is already set up, per step; null where Zelvo cannot tell.
    private static async Task<Dictionary<string, bool?>> ChecksAsync(ZelvoDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.Name).SingleOrDefaultAsync(cancellationToken);
        var requester = BuiltInRoles.Requester;
        return new Dictionary<string, bool?>(StringComparer.Ordinal)
        {
            ["organisation"] = tenantName is { IsComplete: true },
            ["language"] = await db.TenantSettings.AnyAsync(s => s.Key == "localization.defaultLanguage", cancellationToken),
            ["calendar"] = await db.BusinessCalendars.AnyAsync(c => c.IsDefault, cancellationToken),
            ["branding"] = await db.TenantBrandings.AnyAsync(b => b.PublishedAt != null, cancellationToken),
            ["signIn"] = null,
            ["departments"] = await db.Departments.AnyAsync(cancellationToken) && await db.SupportGroups.AnyAsync(cancellationToken),
            ["users"] = await db.Users.CountAsync(u => u.Status == UserStatus.Active && u.Roles.Any(r => r.Role!.Key != requester), cancellationToken) > 1,
            ["channels"] = await db.Mailboxes.AnyAsync(cancellationToken) || await db.ChatWidgets.AnyAsync(cancellationToken),
            ["ai"] = await db.AiProviders.AnyAsync(cancellationToken),
        };
    }

    private static async Task<string> FreeCatalogKeyAsync(ZelvoDbContext db, string key, CancellationToken cancellationToken)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? key : $"{key}-{n}";
            if (!await db.ServiceCatalogs.AnyAsync(c => c.Key == candidate, cancellationToken))
            {
                return candidate;
            }
        }
    }

    private static async Task<string> FreeCodeAsync(ZelvoDbContext db, string code, CancellationToken cancellationToken)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? code : $"{code}-{n}";
            if (candidate.Length <= 30 && !await db.CatalogServices.AnyAsync(s => s.Code == candidate, cancellationToken) && ServiceCards.IsCode(candidate))
            {
                return candidate;
            }

            if (candidate.Length > 30)
            {
                return $"SVC-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
            }
        }
    }

    private static LocalizedText Join(LocalizedText a, LocalizedText b) =>
        new(a.En is null || b.En is null ? a.En ?? b.En : $"{a.En}: {b.En}", a.Ar is null || b.Ar is null ? a.Ar ?? b.Ar : $"{a.Ar}: {b.Ar}");

    private static LocalizedText RecordTypeName(Data.Tickets.TicketType type) => type switch
    {
        Data.Tickets.TicketType.ServiceRequest => new("Service requests", "طلبات الخدمة"),
        Data.Tickets.TicketType.Complaint => new("Complaints", "الشكاوى"),
        Data.Tickets.TicketType.Inquiry => new("Inquiries", "الاستفسارات"),
        Data.Tickets.TicketType.Suggestion => new("Suggestions", "الاقتراحات"),
        _ => new("Incidents", "الحوادث"),
    };

    private static LocalizedText? Text(TextValue? value)
    {
        var en = string.IsNullOrWhiteSpace(value?.En) ? null : value.En.Trim();
        var ar = string.IsNullOrWhiteSpace(value?.Ar) ? null : value.Ar.Trim();
        return en is null && ar is null ? null : new LocalizedText(en, ar);
    }

    private static bool Fits(LocalizedText text, int max) => text.HasAnyValue && (text.En?.Length ?? 0) <= max && (text.Ar?.Length ?? 0) <= max;

    [GeneratedRegex("^[A-Z0-9]{2,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}

/// <summary>A bilingual value sent by the client.</summary>
internal sealed class TextValue
{
    /// <summary>English.</summary>
    [StringLength(200)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(200)]
    public string? Ar { get; init; }
}

/// <summary>A step's new status.</summary>
internal sealed record SetupStepRequest(SetupStepStatus? Status);

/// <summary>How to apply a template; everything is optional.</summary>
internal sealed class ApplyTemplateRequest
{
    /// <summary>The department's name (default: the template's).</summary>
    public TextValue? DepartmentName { get; init; }

    /// <summary>Group names by the template's group key.</summary>
    public IReadOnlyDictionary<string, TextValue>? GroupNames { get; init; }

    /// <summary>Prefix of the service codes (default: the template's, for example IT).</summary>
    [StringLength(10)]
    public string? CodePrefix { get; init; }

    /// <summary>Create the catalog with draft services (default true).</summary>
    public bool? IncludeServices { get; init; }

    /// <summary>Create the canned responses (default true).</summary>
    public bool? IncludeResponses { get; init; }
}

/// <summary>A step of the wizard.</summary>
/// <param name="Key">Step key.</param>
/// <param name="Status">Where it is.</param>
/// <param name="Configured">Whether its settings already look set up; null where Zelvo cannot tell.</param>
internal sealed record SetupStepResponse(string Key, SetupStepStatus Status, bool? Configured);

/// <summary>A template applied.</summary>
internal sealed record AppliedTemplateSummary(string Template, Guid DepartmentId, DateTimeOffset AppliedAt);

/// <summary>The wizard.</summary>
internal sealed record SetupResponse(IReadOnlyList<SetupStepResponse> Steps, DateTimeOffset? CompletedAt, int Finished, int Total, IReadOnlyList<AppliedTemplateSummary> Applied);

/// <summary>What applying a template created.</summary>
internal sealed record AppliedTemplateResponse(
    string Template, Guid DepartmentId, IReadOnlyList<Guid> GroupIds, Guid RootCategoryId, int Categories, int Policies, Guid? CatalogId, IReadOnlyList<Guid> ServiceIds, int Responses);

/// <summary>A bilingual name.</summary>
internal sealed record TemplateText(string? En, string? Ar)
{
    public static TemplateText From(LocalizedText text) => new(text.En, text.Ar);
}

/// <summary>A department template with everything it creates.</summary>
internal sealed record DepartmentTemplateResponse(
    string Key, TemplateText Name, TemplateText Description, string CodePrefix, IReadOnlyList<DepartmentTemplateGroup> Groups,
    IReadOnlyList<DepartmentTemplateCategory> Categories, IReadOnlyList<DepartmentTemplatePolicy> Policies, IReadOnlyList<DepartmentTemplateService> Services,
    IReadOnlyList<DepartmentTemplateResponseText> Responses)
{
    public static DepartmentTemplateResponse From(DepartmentTemplate t) => new(
        t.Key, TemplateText.From(t.Name), TemplateText.From(t.Description), t.CodePrefix,
        [.. t.Groups.Select(g => new DepartmentTemplateGroup(g.Key, TemplateText.From(g.Name)))],
        [.. t.Categories.Select(c => new DepartmentTemplateCategory(TemplateText.From(c.Name), c.Group, [.. c.Children.Select(TemplateText.From)]))],
        [.. t.Policies.Select(p => new DepartmentTemplatePolicy(p.RecordType, [.. p.Targets.Select(x => new DepartmentTemplateTarget(x.Priority, x.Response, x.Resolution))]))],
        [.. t.Services.Select(s => new DepartmentTemplateService(s.Template, $"{t.CodePrefix}-{s.Code}",
            TemplateText.From(s.Name ?? ServiceTemplates.BuiltIn.FirstOrDefault(b => b.Key == s.Template).Name ?? new LocalizedText(s.Template, null))))],
        [.. t.Responses.Select(r => new DepartmentTemplateResponseText(TemplateText.From(r.Title), TemplateText.From(r.Body)))]);
}

/// <summary>A group of a template.</summary>
internal sealed record DepartmentTemplateGroup(string Key, TemplateText Name);

/// <summary>A category of a template and its group.</summary>
internal sealed record DepartmentTemplateCategory(TemplateText Name, string Group, IReadOnlyList<TemplateText> Children);

/// <summary>An SLA policy of a template.</summary>
internal sealed record DepartmentTemplatePolicy(Data.Tickets.TicketType RecordType, IReadOnlyList<DepartmentTemplateTarget> Targets);

/// <summary>Targets of one priority, in business minutes.</summary>
internal sealed record DepartmentTemplateTarget(int Priority, int ResponseMinutes, int ResolutionMinutes);

/// <summary>A draft service of a template.</summary>
internal sealed record DepartmentTemplateService(string Template, string Code, TemplateText Name);

/// <summary>A canned response of a template.</summary>
internal sealed record DepartmentTemplateResponseText(TemplateText Title, TemplateText Body);
