using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Platform;
using Zelvo.Api.Records;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Apps;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Licensing;
using Zelvo.Tickets;

namespace Zelvo.Api.Apps;

/// <summary>
/// The low-code app builder (module 20, task K3; product owner decision 20 of 2026-11-26):
/// Settings → Apps (apps grouping record types, data lists and roles with their list views, menu
/// entries and portal entries), Settings → Data lists, exporting an app as a JSON package, and
/// importing one: validated, previewed, and inactive until an administrator approves it, every
/// step audited (decided 2026-11-26). Everything needs <c>apps.manage</c> except reading apps,
/// which every signed-in person may do for the entries they may use.
/// </summary>
internal static class AppBuilderEndpoints
{
    /// <summary>Most apps per tenant.</summary>
    public const int MaxApps = 50;

    /// <summary>Most data lists per tenant.</summary>
    public const int MaxDataLists = 200;

    public static RouteGroupBuilder MapAppBuilderEndpoints(this RouteGroupBuilder group)
    {
        var apps = group.MapGroup("/apps").WithTags("Apps").RequireTenant();
        apps.MapGet("/", ListAsync).WithName("ListApps").RequireAuthorization()
            .WithSummary("Active apps with the menu entries (needs the record type's read or edit permission) and portal entries (needs being allowed to raise records of the type) the caller may use, and list views. Holders of apps.manage see every app with what it groups.")
            .Produces<IReadOnlyList<AppResponse>>();
        apps.MapGet("/{key}", GetAsync).WithName("GetApp").RequireAuthorization()
            .WithSummary("One app, by key. Returns an ETag to send as If-Match with changes.").Produces<AppResponse>();
        apps.MapPost("/", CreateAsync).WithName("CreateApp").RequirePermission(Permissions.AppsManage)
            .WithSummary("Make an app: key (never changes), bilingual name and description, icon. Needs apps.manage; audited.")
            .Produces<AppResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        apps.MapPatch("/{key}", UpdateAsync).WithName("UpdateApp").RequirePermission(Permissions.AppsManage)
            .WithSummary("Change an app: names, icon, the record types, data lists and custom roles it groups (each in at most one app), and its list views, menu entries and portal entries (on its own record types). Needs apps.manage; audited.")
            .Produces<AppResponse>().ProducesValidationProblem();
        apps.MapPost("/{key}/deactivate", DeactivateAsync).WithName("DeactivateApp").RequirePermission(Permissions.AppsManage)
            .WithSummary("Switch an app off: its entries are hidden, its record types take no new records and its workflow rules stop; switching it on restores exactly those. Audited.")
            .Produces<AppResponse>();
        apps.MapPost("/{key}/activate", ActivateAsync).WithName("ActivateApp").RequirePermission(Permissions.AppsManage)
            .WithSummary("Switch an app on again. Audited.").Produces<AppResponse>();
        apps.MapGet("/{key}/export", ExportAsync).WithName("ExportApp").RequirePermission(Permissions.AppsManage)
            .WithSummary("The app as a zelvo-app/1 JSON package: its data lists with items, record types, roles, the published forms of its record types (fields of field sets included), form bindings, workflow rules and entries; groups, categories, locations, departments, customer groups, services, people, connectors and classes they refer to are listed by name, path, code or email. No code. Audited.")
            .Produces<object>();

        var lists = group.MapGroup("/data-lists").WithTags("Data lists").RequireTenant();
        lists.MapGet("/", ListDataListsAsync).WithName("ListDataLists").RequireAuthorization()
            .WithSummary("Data lists with their items. Needs apps.manage or forms.manage (the form builder offers them as option sources).")
            .Produces<IReadOnlyList<DataListResponse>>();
        lists.MapGet("/{key}", GetDataListAsync).WithName("GetDataList").RequireAuthorization()
            .WithSummary("One data list with its items. Needs apps.manage or forms.manage.").Produces<DataListResponse>();
        lists.MapPost("/", CreateDataListAsync).WithName("CreateDataList").RequirePermission(Permissions.AppsManage)
            .WithSummary("Make a data list: key (never changes), bilingual name and description, up to 10 columns, up to 1,000 items (value, bilingual label, column values). Needs apps.manage; audited.")
            .Produces<DataListResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        lists.MapPut("/{key}", UpdateDataListAsync).WithName("UpdateDataList").RequirePermission(Permissions.AppsManage)
            .WithSummary("Replace a data list's names, columns and items: items given are added or updated in their order, items left out are no longer offered (kept for answers that chose them). Needs apps.manage; audited.")
            .Produces<DataListResponse>().ProducesValidationProblem();

        var imports = group.MapGroup("/app-imports").WithTags("Apps").RequireTenant().RequirePermission(Permissions.AppsManage);
        imports.MapPost("/", ImportAsync).WithName("ImportApp")
            .WithSummary("Import an app package (the JSON as the body, at most 5 MB; fileName in the query). It is kept as it came, checked and previewed, and nothing of it is active until an administrator approves it. Audited.")
            .Accepts<object>("application/json").Produces<AppImportResponse>(StatusCodes.Status201Created);
        imports.MapGet("/", ListImportsAsync).WithName("ListAppImports").WithSummary("Imported packages, newest first.").Produces<IReadOnlyList<AppImportSummary>>();
        imports.MapGet("/{id:guid}", GetImportAsync).WithName("GetAppImport")
            .WithSummary("An imported package with its check against this installation as it is now: problems, what already exists (choose keep or replace), what it adds, the permissions it makes and grants, and the references it found.")
            .Produces<AppImportResponse>();
        imports.MapPost("/{id:guid}/approve", ApproveAsync).WithName("ApproveAppImport")
            .WithSummary("Approve a pending package: checked again; refused while it has problems (422 package-invalid) or while something that exists has no choice (422 choices-required). Installs it in one transaction as chosen and activates the app. Audited.")
            .Produces<AppImportResponse>().ProducesValidationProblem();
        imports.MapPost("/{id:guid}/discard", DiscardAsync).WithName("DiscardAppImport")
            .WithSummary("Discard a pending package without changing anything. Audited.").Produces<AppImportResponse>();
        return group;
    }

    // ---------------------------------------------------------------- apps

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var manage = user.Has(Permissions.AppsManage);
        var apps = await db.Apps.AsNoTracking().Where(a => manage || a.Active).OrderBy(a => a.Key).ToListAsync(cancellationToken);
        var types = await db.RecordTypes.AsNoTracking().Where(t => t.AppId != null).ToListAsync(cancellationToken);
        var result = new List<AppResponse>();
        foreach (var app in apps)
        {
            var response = await ResponseAsync(db, app, user, types, manage, cancellationToken);
            if (manage || response.MenuEntries.Count > 0 || response.PortalEntries.Count > 0)
            {
                result.Add(response);
            }
        }

        return TypedResults.Ok(result);
    }

    internal static async Task<IResult> GetAsync(string key, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var manage = user.Has(Permissions.AppsManage);
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(a => a.Key == key, cancellationToken);
        if (app is null || (!manage && !app.Active))
        {
            return FormsCommon.NotFound("app");
        }

        var types = await db.RecordTypes.AsNoTracking().Where(t => t.AppId == app.Id).ToListAsync(cancellationToken);
        var response = await ResponseAsync(db, app, user, types, manage, cancellationToken);
        if (!manage && response.MenuEntries.Count == 0 && response.PortalEntries.Count == 0)
        {
            return FormsCommon.NotFound("app");
        }

        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<IResult> CreateAsync(CreateAppRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var problems = new List<PackageProblem>();
        var key = request.Key?.Trim() ?? "";
        AppPackages.CheckApp(key, request.Name, request.Description, request.Icon, "", problems);
        if (problems.Count > 0)
        {
            return Problems(problems);
        }

        if (await db.Apps.CountAsync(cancellationToken) >= MaxApps)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "too-many", $"A tenant has at most {MaxApps} apps.");
        }

        if (await db.Apps.AnyAsync(a => a.Key == key, cancellationToken))
        {
            return FormsCommon.KeyTaken();
        }

        var app = new App
        {
            Key = key, Name = request.Name!.ToValue(), Description = request.Description?.ToValue() ?? new LocalizedText(null, null),
            Icon = request.Icon ?? "service", CreatedById = user.Account?.Id, Definition = new AppDefinition().Write(),
        };
        db.Apps.Add(app);
        audit.Add(AuditCategory.Admin, "apps.created", "app", app.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key });
        await db.SaveChangesAsync(cancellationToken);
        var saved = await db.Apps.AsNoTracking().SingleAsync(a => a.Id == app.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/apps/{key}", await ResponseAsync(db, saved, user, [], true, cancellationToken));
    }

    internal static async Task<IResult> UpdateAsync(
        string key, UpdateAppRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var app = await db.Apps.SingleOrDefaultAsync(a => a.Key == key, cancellationToken);
        if (app is null)
        {
            return FormsCommon.NotFound("app");
        }

        if (IfMatch(http, db, app) is { } mismatch)
        {
            return mismatch;
        }

        var problems = new List<PackageProblem>();
        AppPackages.CheckApp(app.Key, request.Name ?? PackageText.From(app.Name), request.Description, request.Icon, "", problems);

        // Members: each record type, data list and custom role in at most one app.
        var types = request.RecordTypes is { } typeKeys ? await db.RecordTypes.Where(t => typeKeys.Contains(t.Key)).ToListAsync(cancellationToken) : null;
        var lists = request.DataLists is { } listKeys ? await db.DataLists.Where(l => listKeys.Contains(l.Key)).ToListAsync(cancellationToken) : null;
        var roles = request.Roles is { } roleKeys ? await db.Roles.Where(r => roleKeys.Contains(r.Key)).ToListAsync(cancellationToken) : null;
        if (types is not null && (types.Count != request.RecordTypes!.Distinct().Count() || types.Any(t => t.AppId is { } other && other != app.Id)))
        {
            problems.Add(new("recordTypes", "member-invalid", "Every record type must exist and belong to no other app."));
        }

        if (lists is not null && (lists.Count != request.DataLists!.Distinct().Count() || lists.Any(l => l.AppId is { } other && other != app.Id)))
        {
            problems.Add(new("dataLists", "member-invalid", "Every data list must exist and belong to no other app."));
        }

        if (roles is not null && (roles.Count != request.Roles!.Distinct().Count() || roles.Any(r => r.IsSystem || (r.AppId is { } other && other != app.Id))))
        {
            problems.Add(new("roles", "member-invalid", "Every role must be a custom role that exists and belongs to no other app."));
        }

        var memberTypes = types ?? await db.RecordTypes.Where(t => t.AppId == app.Id).ToListAsync(cancellationToken);
        var definition = request.Definition ?? AppDefinition.Read(app.Definition);
        AppPackages.CheckDefinition(definition, memberTypes.ToDictionary(t => t.Key, t => (IReadOnlyList<string>)[.. RecordStates.Read(t.States).Select(s => s.Key)], StringComparer.Ordinal), "definition", problems);
        if (problems.Count > 0)
        {
            return Problems(problems);
        }

        if (types is not null)
        {
            foreach (var old in await db.RecordTypes.Where(t => t.AppId == app.Id).ToListAsync(cancellationToken))
            {
                old.AppId = null;
            }

            types.ForEach(t => t.AppId = app.Id);
        }

        if (lists is not null)
        {
            foreach (var old in await db.DataLists.Where(t => t.AppId == app.Id).ToListAsync(cancellationToken))
            {
                old.AppId = null;
            }

            lists.ForEach(t => t.AppId = app.Id);
        }

        if (roles is not null)
        {
            foreach (var old in await db.Roles.Where(t => t.AppId == app.Id).ToListAsync(cancellationToken))
            {
                old.AppId = null;
            }

            roles.ForEach(t => t.AppId = app.Id);
        }

        app.Name = request.Name?.ToValue() ?? app.Name;
        app.Description = request.Description?.ToValue() ?? app.Description;
        app.Icon = request.Icon ?? app.Icon;
        app.Definition = definition.Write();
        app.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "apps.updated", "app", app.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        return await GetAsync(key, http, db, user, cancellationToken);
    }

    internal static async Task<IResult> DeactivateAsync(string key, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var app = await db.Apps.SingleOrDefaultAsync(a => a.Key == key, cancellationToken);
        if (app is null)
        {
            return FormsCommon.NotFound("app");
        }

        if (app.Active)
        {
            var types = await db.RecordTypes.Where(t => t.AppId == app.Id && t.Active).ToListAsync(cancellationToken);
            var typeIds = await db.RecordTypes.Where(t => t.AppId == app.Id).Select(t => t.Id).ToListAsync(cancellationToken);
            var rules = await db.WorkflowRules.Where(r => r.CustomRecordTypeId != null && typeIds.Contains(r.CustomRecordTypeId.Value) && r.Active).ToListAsync(cancellationToken);
            types.ForEach(t => { t.Active = false; t.UpdatedAt = DateTimeOffset.UtcNow; });
            rules.ForEach(r => { r.Active = false; r.UpdatedAt = DateTimeOffset.UtcNow; });
            app.Suspended = JsonSerializer.Serialize(new Suspension([.. types.Select(t => t.Id)], [.. rules.Select(r => r.Id)]), AppPackage.Compact);
            app.Active = false;
            app.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add(AuditCategory.Admin, "apps.deactivated", "app", app.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = key, ["recordTypes"] = types.Count, ["rules"] = rules.Count,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(key, http, db, user, cancellationToken);
    }

    internal static async Task<IResult> ActivateAsync(string key, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var app = await db.Apps.SingleOrDefaultAsync(a => a.Key == key, cancellationToken);
        if (app is null)
        {
            return FormsCommon.NotFound("app");
        }

        if (!app.Active)
        {
            var suspended = ReadSuspension(app.Suspended);
            var types = await db.RecordTypes.Where(t => suspended.RecordTypes.Contains(t.Id) && t.AppId == app.Id).ToListAsync(cancellationToken);
            var rules = await db.WorkflowRules.Where(r => suspended.Rules.Contains(r.Id)).ToListAsync(cancellationToken);
            types.ForEach(t => { t.Active = true; t.UpdatedAt = DateTimeOffset.UtcNow; });
            rules.ForEach(r => { r.Active = true; r.UpdatedAt = DateTimeOffset.UtcNow; });
            app.Suspended = "{}";
            app.Active = true;
            app.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add(AuditCategory.Admin, "apps.activated", "app", app.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = key, ["recordTypes"] = types.Count, ["rules"] = rules.Count,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(key, http, db, user, cancellationToken);
    }

    internal static async Task<IResult> ExportAsync(string key, HttpContext http, ZelvoDbContext db, ILicenseService license, IAuditLog audit, CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(a => a.Key == key, cancellationToken);
        if (app is null)
        {
            return FormsCommon.NotFound("app");
        }

        var info = SystemEndpoints.GetInfo(license);
        var package = await AppPackages.ExportAsync(db, app, info.Environment, info.Version, cancellationToken);
        var json = JsonSerializer.Serialize(package, AppPackage.Json);
        audit.Add(AuditCategory.Admin, "apps.exported", "app", app.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key, ["sha256"] = Sha256(json), ["recordTypes"] = package.RecordTypes?.Count, ["rules"] = package.Rules?.Count,
        });
        await db.SaveChangesAsync(cancellationToken);
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{key}.zelvo-app.json\"";
        return TypedResults.Text(json, "application/json", Encoding.UTF8);
    }

    private static async Task<AppResponse> ResponseAsync(
        ZelvoDbContext db, App app, CurrentUser user, IReadOnlyList<Data.Records.RecordType> allTypes, bool manage, CancellationToken cancellationToken)
    {
        var types = allTypes.Where(t => t.AppId == app.Id).ToList();
        var byKey = types.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var definition = AppDefinition.Read(app.Definition);
        bool MayOpen(string type) => byKey.TryGetValue(type, out var t) && app.Active && t.Active && (RecordAccess.Of(user, t).Read || RecordAccess.Of(user, t).Edit);
        bool MayRaise(string type) => byKey.TryGetValue(type, out var t) && app.Active && t.Active && (t.PortalEntry || RecordAccess.Of(user, t).Edit);
        var menu = manage ? definition.MenuEntries : [.. definition.MenuEntries.Where(e => e.Target == "new" ? MayRaise(e.RecordType) : MayOpen(e.RecordType) || MayRaise(e.RecordType))];
        var portal = manage ? definition.PortalEntries : [.. definition.PortalEntries.Where(e => MayRaise(e.RecordType))];
        AppMembers? members = null;
        if (manage)
        {
            var typeIds = types.Select(t => t.Id).ToList();
            members = new AppMembers(
                [.. types.OrderBy(t => t.Key).Select(t => new AppMember(t.Key, LocalizedTextDto.From(t.Name), t.Active))],
                [.. (await db.DataLists.AsNoTracking().Where(l => l.AppId == app.Id).OrderBy(l => l.Key).ToListAsync(cancellationToken)).Select(l => new AppMember(l.Key, LocalizedTextDto.From(l.Name), true))],
                [.. (await db.Roles.AsNoTracking().Where(r => r.AppId == app.Id).OrderBy(r => r.Key).ToListAsync(cancellationToken)).Select(r => new AppMember(r.Key, LocalizedTextDto.From(r.Name), true))],
                [.. (await db.FormBindings.AsNoTracking().Where(b => b.CustomRecordTypeId != null && typeIds.Contains(b.CustomRecordTypeId.Value))
                    .Join(db.Forms, b => b.FormId, f => f.Id, (b, f) => f).Distinct().ToListAsync(cancellationToken)).Select(f => new AppMember(f.Key, LocalizedTextDto.From(f.Name), f.State == Data.Forms.FormState.Published))],
                [.. (await db.WorkflowRules.AsNoTracking().Where(r => r.CustomRecordTypeId != null && typeIds.Contains(r.CustomRecordTypeId.Value) && !r.IsDraft).OrderBy(r => r.Position).ToListAsync(cancellationToken))
                    .Select(r => new AppMember(r.Id.ToString(), LocalizedTextDto.From(r.Name), r.Active))]);
        }

        return new AppResponse(
            app.Id, app.Key, LocalizedTextDto.From(app.Name), LocalizedTextDto.From(app.Description), app.Icon, app.Active,
            manage ? definition.ListViews : [.. definition.ListViews.Where(v => MayOpen(v.RecordType))], menu, portal, members, app.ImportId,
            $"\"{app.Version}\"", app.CreatedAt, app.UpdatedAt);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? IfMatch(HttpContext http, ZelvoDbContext db, App app)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (ifMatch.Length == 0)
        {
            return null;
        }

        if (!uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != app.Version)
        {
            return FormsCommon.VersionMismatch();
        }

        db.Entry(app).Property(a => a.Version).OriginalValue = version;
        return null;
    }

    /// <summary>What switching an app off switched off.</summary>
    private sealed record Suspension(List<Guid> RecordTypes, List<Guid> Rules);

    private static Suspension ReadSuspension(string json)
    {
        try
        {
            var read = JsonSerializer.Deserialize<Suspension>(json, AppPackage.Json);
            return new Suspension(read?.RecordTypes ?? [], read?.Rules ?? []);
        }
        catch (JsonException)
        {
            return new Suspension([], []);
        }
    }

    // ---------------------------------------------------------------- data lists

    private static bool MayReadLists(CurrentUser user) => user.Has(Permissions.AppsManage) || user.Has(Permissions.FormsManage);

    internal static async Task<IResult> ListDataListsAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!MayReadLists(user))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Reading data lists needs apps.manage or forms.manage.");
        }

        var lists = await db.DataLists.AsNoTracking().OrderBy(l => l.Key).ToListAsync(cancellationToken);
        var items = await db.DataListItems.AsNoTracking().OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var apps = await db.Apps.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Key, cancellationToken);
        return TypedResults.Ok(lists.Select(l => DataListResponseOf(l, items.Where(i => i.DataListId == l.Id), apps)).ToList());
    }

    internal static async Task<IResult> GetDataListAsync(string key, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!MayReadLists(user))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Reading data lists needs apps.manage or forms.manage.");
        }

        var list = await db.DataLists.AsNoTracking().SingleOrDefaultAsync(l => l.Key == key, cancellationToken);
        if (list is null)
        {
            return FormsCommon.NotFound("data-list");
        }

        var items = await db.DataListItems.AsNoTracking().Where(i => i.DataListId == list.Id).OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var apps = await db.Apps.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Key, cancellationToken);
        http.Response.Headers.ETag = $"\"{list.Version}\"";
        return TypedResults.Ok(DataListResponseOf(list, items, apps));
    }

    internal static async Task<IResult> CreateDataListAsync(DataListRequest request, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var input = request.ToPackage();
        var problems = new List<PackageProblem>();
        AppPackages.CheckDataList(input, "", problems);
        if (problems.Count > 0)
        {
            return Problems(problems);
        }

        if (await db.DataLists.CountAsync(cancellationToken) >= MaxDataLists)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "too-many", $"A tenant has at most {MaxDataLists} data lists.");
        }

        if (await db.DataLists.AnyAsync(l => l.Key == input.Key, cancellationToken))
        {
            return FormsCommon.KeyTaken();
        }

        var list = new DataList
        {
            Key = input.Key!, Name = input.Name!.ToValue(), Description = input.Description?.ToValue() ?? new LocalizedText(null, null),
            Columns = JsonSerializer.Serialize(input.Columns ?? [], AppPackage.Compact),
        };
        db.DataLists.Add(list);
        await AppPackages.SetItemsAsync(db, list, input.Items ?? [], cancellationToken);
        audit.Add(AuditCategory.Admin, "apps.data-list.created", "data_list", list.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = list.Key, ["items"] = input.Items?.Count ?? 0,
        });
        await db.SaveChangesAsync(cancellationToken);
        var items = await db.DataListItems.AsNoTracking().Where(i => i.DataListId == list.Id).OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var saved = await db.DataLists.AsNoTracking().SingleAsync(l => l.Id == list.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/data-lists/{list.Key}", DataListResponseOf(saved, items, new Dictionary<Guid, string>()));
    }

    internal static async Task<IResult> UpdateDataListAsync(
        string key, DataListRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var list = await db.DataLists.SingleOrDefaultAsync(l => l.Key == key, cancellationToken);
        if (list is null)
        {
            return FormsCommon.NotFound("data-list");
        }

        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (ifMatch.Length > 0)
        {
            if (!uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != list.Version)
            {
                return FormsCommon.VersionMismatch();
            }

            db.Entry(list).Property(l => l.Version).OriginalValue = version;
        }

        var input = request.ToPackage() with { Key = key };
        var problems = new List<PackageProblem>();
        AppPackages.CheckDataList(input, "", problems);
        if (problems.Count > 0)
        {
            return Problems(problems);
        }

        list.Name = input.Name!.ToValue();
        list.Description = input.Description?.ToValue() ?? new LocalizedText(null, null);
        list.Columns = JsonSerializer.Serialize(input.Columns ?? [], AppPackage.Compact);
        list.UpdatedAt = DateTimeOffset.UtcNow;
        await AppPackages.SetItemsAsync(db, list, input.Items ?? [], cancellationToken);
        audit.Add(AuditCategory.Admin, "apps.data-list.updated", "data_list", list.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key, ["items"] = input.Items?.Count ?? 0,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        return await GetDataListAsync(key, http, db, user, cancellationToken);
    }

    private static DataListResponse DataListResponseOf(DataList list, IEnumerable<DataListItem> items, IReadOnlyDictionary<Guid, string> apps) => new(
        list.Id, list.Key, LocalizedTextDto.From(list.Name), LocalizedTextDto.From(list.Description), AppPackages.ReadColumns(list.Columns),
        [.. items.Select(i => new DataListItemResponse(i.Key, LocalizedTextDto.From(i.Label), AppPackages.ReadValues(i.Values), i.Active))],
        list.AppId is { } appId ? apps.GetValueOrDefault(appId) : null, $"\"{list.Version}\"", list.UpdatedAt);

    // ---------------------------------------------------------------- imports

    internal static async Task<IResult> ImportAsync(
        HttpRequest http, string? fileName, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (http.ContentLength is > AppImport.MaxPackageBytes)
        {
            return ApiProblem.Create(StatusCodes.Status413PayloadTooLarge, "package-too-large", "An app package is at most 5 MB.");
        }

        using var reader = new StreamReader(http.Body, Encoding.UTF8);
        var buffer = new char[AppImport.MaxPackageBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await reader.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
        {
            read += n;
        }

        if (read > AppImport.MaxPackageBytes)
        {
            return ApiProblem.Create(StatusCodes.Status413PayloadTooLarge, "package-too-large", "An app package is at most 5 MB.");
        }

        var json = new string(buffer, 0, read);
        var name = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName.Trim());
        var package = AppPackages.Read(json);
        if (package is null || package.Format != AppPackage.CurrentFormat || package.App?.Key is not { } appKey || !AppPackages.KeyPattern().IsMatch(appKey))
        {
            audit.Add(AuditCategory.Admin, "apps.import.refused", "app_import", null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fileName"] = name, ["sha256"] = Sha256(json),
            });
            await db.SaveChangesAsync(cancellationToken);
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "package-unreadable", $"This is not a {AppPackage.CurrentFormat} app package.");
        }

        var import = new AppImport
        {
            FileName = name is { Length: > AppImport.FileNameMaxLength } ? name[..AppImport.FileNameMaxLength] : name,
            Sha256 = Sha256(json),
            Package = JsonSerializer.Serialize(package, AppPackage.Compact),
            AppKey = appKey,
            AppName = package.App.Name?.ToValue() is { HasAnyValue: true } appName ? appName : new LocalizedText(appKey, null),
            ImportedById = user.Account?.Id,
        };
        db.AppImports.Add(import);
        var plan = await AppPackages.PlanAsync(db, settings, package, null, cancellationToken);
        audit.Add(AuditCategory.Admin, "apps.imported", "app_import", import.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["fileName"] = import.FileName, ["sha256"] = import.Sha256, ["app"] = appKey, ["problems"] = plan.Problems.Count, ["conflicts"] = plan.Conflicts.Count,
            ["permissionsCreated"] = string.Join(",", plan.PermissionsCreated),
        });
        await db.SaveChangesAsync(cancellationToken);
        var saved = await db.AppImports.AsNoTracking().SingleAsync(i => i.Id == import.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/app-imports/{import.Id}", ImportResponse(saved, plan));
    }

    internal static async Task<IResult> ListImportsAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.AppImports.AsNoTracking().OrderByDescending(i => i.ImportedAt).Take(100).ToListAsync(cancellationToken))
            .Select(i => new AppImportSummary(i.Id, i.FileName, i.AppKey, LocalizedTextDto.From(i.AppName), i.Status, i.ImportedAt, i.DecidedAt)).ToList());

    internal static async Task<IResult> GetImportAsync(Guid id, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var import = await db.AppImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (import is null)
        {
            return FormsCommon.NotFound("app-import");
        }

        var plan = import.Status == AppImportStatus.Pending ? await AppPackages.PlanAsync(db, settings, AppPackages.Read(import.Package)!, null, cancellationToken) : null;
        return TypedResults.Ok(ImportResponse(import, plan));
    }

    internal static async Task<IResult> ApproveAsync(
        Guid id, ApproveAppImportRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAuditLog audit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // One approval at a time: the row is locked for the transaction.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM app_imports WHERE id = {id} FOR UPDATE", cancellationToken);
        var import = await db.AppImports.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (import is null)
        {
            return FormsCommon.NotFound("app-import");
        }

        if (import.Status != AppImportStatus.Pending)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "import-decided", "This package was already approved or discarded.");
        }

        var choices = request.Choices ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (choices.Any(c => c.Value is not (AppPackages.Keep or AppPackages.Replace)))
        {
            return UserEndpoints.Validation("choices", "Each choice is keep or replace.");
        }

        var package = AppPackages.Read(import.Package)!;
        var plan = await AppPackages.PlanAsync(db, settings, package, choices, cancellationToken);
        if (!plan.Valid)
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "package-invalid", "The package has problems; it cannot be approved.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["problems"] = plan.Problems });
        }

        var missing = plan.Conflicts.Where(c => !choices.ContainsKey(c.Id)).Select(c => c.Id).ToList();
        if (missing.Count > 0)
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "choices-required", "Choose keep or replace for everything that already exists.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["missing"] = missing });
        }

        var result = await AppPackages.InstallAsync(db, settings, audit, package, choices, user.Account?.Id, import.Id, cancellationToken);
        import.Status = AppImportStatus.Approved;
        import.DecidedById = user.Account?.Id;
        import.DecidedAt = DateTimeOffset.UtcNow;
        import.Choices = JsonSerializer.Serialize(choices, AppPackage.Compact);
        import.Result = JsonSerializer.Serialize(result, AppPackage.Compact);
        import.AppId = result.AppId;
        audit.Add(AuditCategory.Admin, "apps.import.approved", "app_import", import.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["app"] = import.AppKey, ["sha256"] = import.Sha256, ["choices"] = import.Choices,
            ["added"] = result.Added.Count, ["replaced"] = result.Replaced.Count, ["kept"] = result.Kept.Count,
            ["permissionsCreated"] = string.Join(",", plan.PermissionsCreated),
        });
        audit.Add(AuditCategory.Admin, "apps.activated", "app", result.AppId.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = import.AppKey, ["import"] = import.Id.ToString(),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var saved = await db.AppImports.AsNoTracking().SingleAsync(i => i.Id == import.Id, cancellationToken);
        return TypedResults.Ok(ImportResponse(saved, null));
    }

    internal static async Task<IResult> DiscardAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var import = await db.AppImports.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (import is null)
        {
            return FormsCommon.NotFound("app-import");
        }

        if (import.Status != AppImportStatus.Pending)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "import-decided", "This package was already approved or discarded.");
        }

        import.Status = AppImportStatus.Discarded;
        import.DecidedById = user.Account?.Id;
        import.DecidedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "apps.import.discarded", "app_import", import.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["app"] = import.AppKey, ["sha256"] = import.Sha256,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        return TypedResults.Ok(ImportResponse(import, null));
    }

    private static AppImportResponse ImportResponse(AppImport import, PackagePlan? plan) => new(
        import.Id, import.FileName, import.Sha256, import.AppKey, LocalizedTextDto.From(import.AppName), import.Status, import.ImportedAt, import.DecidedAt,
        plan, JsonSerializer.Deserialize<Dictionary<string, string>>(import.Choices, AppPackage.Json) ?? [],
        import.Status == AppImportStatus.Approved ? JsonSerializer.Deserialize<AppPackages.InstallResult>(import.Result, AppPackage.Json) : null);

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem Problems(List<PackageProblem> problems) =>
        TypedResults.ValidationProblem(
            problems.GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray(), StringComparer.Ordinal),
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["problems"] = problems });
}

/// <summary>A record type, data list, role, form or rule an app groups.</summary>
internal sealed record AppMember(string Key, LocalizedTextDto Name, bool Active);

/// <summary>What an app groups (for administrators).</summary>
internal sealed record AppMembers(
    IReadOnlyList<AppMember> RecordTypes, IReadOnlyList<AppMember> DataLists, IReadOnlyList<AppMember> Roles, IReadOnlyList<AppMember> Forms, IReadOnlyList<AppMember> Rules);

/// <summary>An app.</summary>
internal sealed record AppResponse(
    Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, string Icon, bool Active,
    IReadOnlyList<AppListView> ListViews, IReadOnlyList<AppMenuEntry> MenuEntries, IReadOnlyList<AppPortalEntry> PortalEntries,
    AppMembers? Members, Guid? ImportId, string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>An item of a data list.</summary>
internal sealed record DataListItemResponse(string Key, LocalizedTextDto Label, IReadOnlyDictionary<string, string> Values, bool Active);

/// <summary>A data list with its items.</summary>
internal sealed record DataListResponse(
    Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, IReadOnlyList<DataListColumn> Columns, IReadOnlyList<DataListItemResponse> Items,
    string? App, string Etag, DateTimeOffset UpdatedAt);

/// <summary>An imported package in a list.</summary>
internal sealed record AppImportSummary(Guid Id, string? FileName, string AppKey, LocalizedTextDto AppName, AppImportStatus Status, DateTimeOffset ImportedAt, DateTimeOffset? DecidedAt);

/// <summary>An imported package: its check while pending, the choices and what the approval did.</summary>
internal sealed record AppImportResponse(
    Guid Id, string? FileName, string Sha256, string AppKey, LocalizedTextDto AppName, AppImportStatus Status, DateTimeOffset ImportedAt, DateTimeOffset? DecidedAt,
    PackagePlan? Plan, IReadOnlyDictionary<string, string> Choices, AppPackages.InstallResult? Result);

/// <summary>A new app.</summary>
internal sealed class CreateAppRequest
{
    /// <summary>Key, for example <c>hr</c>.</summary>
    [Required]
    [StringLength(App.KeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public PackageText? Name { get; init; }

    /// <summary>What it is for.</summary>
    public PackageText? Description { get; init; }

    /// <summary>One of Zelvo's icons.</summary>
    public string? Icon { get; init; }
}

/// <summary>Changes to an app; members and the definition replace what it had when given.</summary>
internal sealed class UpdateAppRequest
{
    /// <summary>Name in English and Arabic.</summary>
    public PackageText? Name { get; init; }

    /// <summary>What it is for.</summary>
    public PackageText? Description { get; init; }

    /// <summary>One of Zelvo's icons.</summary>
    public string? Icon { get; init; }

    /// <summary>Keys of the record types it groups.</summary>
    [MaxLength(AppPackages.MaxRecordTypes)]
    public IReadOnlyList<string>? RecordTypes { get; init; }

    /// <summary>Keys of the data lists it groups.</summary>
    [MaxLength(AppPackages.MaxDataLists)]
    public IReadOnlyList<string>? DataLists { get; init; }

    /// <summary>Keys of the custom roles it groups.</summary>
    [MaxLength(AppPackages.MaxRoles)]
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>List views, menu entries and portal entries.</summary>
    public AppDefinition? Definition { get; init; }
}

/// <summary>A data list as made or replaced.</summary>
internal sealed class DataListRequest
{
    /// <summary>Key (on creation), for example <c>vehicles</c>.</summary>
    [StringLength(DataList.KeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public PackageText? Name { get; init; }

    /// <summary>What it holds.</summary>
    public PackageText? Description { get; init; }

    /// <summary>Extra columns.</summary>
    [MaxLength(DataList.MaxColumns)]
    public List<DataListColumn>? Columns { get; init; }

    /// <summary>Items in order.</summary>
    [MaxLength(DataList.MaxItems)]
    public List<PackageDataListItem>? Items { get; init; }

    /// <summary>As a package data list (the same checks).</summary>
    public PackageDataList ToPackage() => new() { Key = Key?.Trim(), Name = Name, Description = Description, Columns = Columns, Items = Items };
}

/// <summary>An approval with a choice for everything that already exists.</summary>
internal sealed class ApproveAppImportRequest
{
    /// <summary>By conflict id (<c>recordType:facilities</c>): <c>keep</c> or <c>replace</c>.</summary>
    public Dictionary<string, string>? Choices { get; init; }
}
