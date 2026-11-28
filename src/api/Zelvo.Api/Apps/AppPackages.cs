using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Incidents;
using Zelvo.Api.Settings;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Apps;
using Zelvo.Data.Auditing;
using Zelvo.Data.Directory;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Records;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Apps;

/// <summary>A problem that stops a package from being approved.</summary>
/// <param name="Path">Where in the package, for example <c>recordTypes.0.numberPrefix</c>.</param>
/// <param name="Code">Stable code (the web app shows its own text by code).</param>
/// <param name="Message">English explanation.</param>
internal sealed record PackageProblem(string Path, string Code, string Message);

/// <summary>Something in the package that already exists here; the administrator chooses <c>keep</c> or <c>replace</c>.</summary>
/// <param name="Id">The choice's key: <c>kind:key</c>.</param>
/// <param name="Kind"><c>app</c>, <c>recordType</c>, <c>dataList</c> or <c>role</c>.</param>
/// <param name="Key">The key.</param>
/// <param name="Existing">The existing item's name.</param>
/// <param name="Replace">What replacing it changes (English; the web app words it by kind).</param>
internal sealed record PackageConflict(string Id, string Kind, string Key, LocalizedTextDto Existing, string Replace);

/// <summary>An item the package adds or replaces.</summary>
internal sealed record PackageItem(string Kind, string Key, LocalizedTextDto Name, bool Exists);

/// <summary>A reference to something outside the package and what it was found as here.</summary>
internal sealed record PackageReferencePreview(string Kind, string Label, Guid? FoundId, LocalizedTextDto? FoundName);

/// <summary>Permissions the package grants to a role.</summary>
internal sealed record PackageGrant(string Role, bool RoleExists, IReadOnlyList<string> Permissions);

/// <summary>What a package adds and changes, and why it cannot be approved yet.</summary>
internal sealed record PackagePlan(
    string? AppKey, LocalizedTextDto AppName, string? Icon, PackageSource? Source, DateTimeOffset? ExportedAt,
    IReadOnlyList<PackageProblem> Problems, IReadOnlyList<PackageConflict> Conflicts, IReadOnlyList<PackageItem> Items,
    IReadOnlyList<string> PermissionsCreated, IReadOnlyList<PackageGrant> Grants, IReadOnlyList<PackageReferencePreview> References,
    int Forms, int Rules, int ListViews, int MenuEntries, int PortalEntries)
{
    /// <summary>Nothing stops approval.</summary>
    public bool Valid => Problems.Count == 0;
}

/// <summary>
/// App packages (task K3): export an app as a self-contained <c>zelvo-app/1</c> JSON package,
/// check an imported one against this installation (its shape, every reference, the permissions it
/// creates and grants, what already exists) and, once an administrator approved it with a choice
/// for everything that already exists, install it in one transaction. Nothing is overwritten
/// without the administrator's choice; nothing is ever deleted.
/// </summary>
internal static partial class AppPackages
{
    /// <summary>Limits on what one package holds.</summary>
    public const int MaxRecordTypes = 20, MaxDataLists = 50, MaxRoles = 20, MaxForms = 50, MaxBindings = 200, MaxRules = 100, MaxReferences = 500;

    /// <summary>Choice that keeps what exists.</summary>
    public const string Keep = "keep";

    /// <summary>Choice that replaces what exists with the package's.</summary>
    public const string Replace = "replace";

    [GeneratedRegex("^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    public static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,48}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleKeyPattern();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,19}$", RegexOptions.CultureInvariant)]
    public static partial Regex ColumnKeyPattern();

    // ---------------------------------------------------------------- export

    /// <summary>The app as a package.</summary>
    public static async Task<AppPackage> ExportAsync(ZelvoDbContext db, App app, string? profile, string? productVersion, CancellationToken cancellationToken)
    {
        var types = await db.RecordTypes.AsNoTracking().Where(t => t.AppId == app.Id).OrderBy(t => t.Key).ToListAsync(cancellationToken);
        var typeIds = types.Select(t => t.Id).ToList();
        var typeKeys = types.ToDictionary(t => t.Id, t => t.Key);
        var lists = await db.DataLists.AsNoTracking().Where(l => l.AppId == app.Id).OrderBy(l => l.Key).ToListAsync(cancellationToken);
        var listIds = lists.Select(l => l.Id).ToList();
        var items = await db.DataListItems.AsNoTracking().Where(i => listIds.Contains(i.DataListId)).OrderBy(i => i.Position).ThenBy(i => i.Key).ToListAsync(cancellationToken);
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).Where(r => r.AppId == app.Id && !r.IsSystem).OrderBy(r => r.Key).ToListAsync(cancellationToken);
        var allRoles = await db.Roles.AsNoTracking().Include(r => r.Permissions).ToListAsync(cancellationToken);
        var bindings = await db.FormBindings.AsNoTracking().Where(b => b.CustomRecordTypeId != null && typeIds.Contains(b.CustomRecordTypeId.Value)).ToListAsync(cancellationToken);
        var rules = await db.WorkflowRules.AsNoTracking().Where(r => r.CustomRecordTypeId != null && typeIds.Contains(r.CustomRecordTypeId.Value) && !r.IsDraft)
            .OrderBy(r => r.Position).ToListAsync(cancellationToken);

        var references = new Dictionary<Guid, string>();
        Guid? Note(string kind, Guid id)
        {
            references.TryAdd(id, kind);
            return id;
        }

        // Forms: published ones, with the fields of their field sets, without the data lists' items.
        var forms = new List<PackageForm>();
        var formKeys = new Dictionary<Guid, string>();
        foreach (var formId in bindings.Select(b => b.FormId).Distinct())
        {
            if (await FormResolver.PublishedAsync(db, formId, cancellationToken) is not { } resolved)
            {
                continue;
            }

            var fields = resolved.PlainFields.Select(f => Portable(f, Note)).ToList();
            formKeys[formId] = resolved.Form.Key;
            forms.Add(new PackageForm
            {
                Key = resolved.Form.Key,
                Name = PackageText.From(resolved.Form.Name),
                Description = PackageText.From(resolved.Form.Description),
                Fields = JsonSerializer.SerializeToNode(fields, FormJson.Options)!.AsArray(),
            });
        }

        var packageRules = rules.Select(r => new PackageRule
        {
            Name = PackageText.From(r.Name),
            Description = PackageText.From(r.Description),
            Active = r.Active,
            Trigger = r.Trigger,
            RecordType = typeKeys[r.CustomRecordTypeId!.Value],
            Match = r.Match,
            Conditions = JsonSerializer.SerializeToNode(RuleReferences.Conditions(RuleDocuments.ReadConditions(r.Conditions), Note), RuleDocuments.Json)!.AsArray(),
            Actions = JsonSerializer.SerializeToNode(RuleReferences.Actions(RuleDocuments.ReadActions(r.Actions), Note, k => k), RuleDocuments.Json)!.AsArray(),
            StopProcessing = r.StopProcessing,
        }).ToList();

        foreach (var binding in bindings.Where(b => b.CategoryId is not null))
        {
            Note(ReferenceKinds.Category, binding.CategoryId!.Value);
        }

        return new AppPackage
        {
            Format = AppPackage.CurrentFormat,
            ExportedAt = DateTimeOffset.UtcNow,
            Source = new PackageSource(profile, productVersion),
            App = new PackageApp
            {
                Key = app.Key,
                Name = PackageText.From(app.Name),
                Description = PackageText.From(app.Description),
                Icon = app.Icon,
                Definition = AppDefinition.Read(app.Definition),
            },
            DataLists = [.. lists.Select(l => new PackageDataList
            {
                Key = l.Key,
                Name = PackageText.From(l.Name),
                Description = PackageText.From(l.Description),
                Columns = ReadColumns(l.Columns),
                Items = [.. items.Where(i => i.DataListId == l.Id).Select(i => new PackageDataListItem
                {
                    Key = i.Key, Label = PackageText.From(i.Label), Values = ReadValues(i.Values), Active = i.Active,
                })],
            })],
            RecordTypes = [.. types.Select(t => new PackageRecordType
            {
                Key = t.Key,
                Name = PackageText.From(t.Name),
                Description = PackageText.From(t.Description),
                NumberPrefix = t.NumberPrefix,
                States = [.. RecordStates.Read(t.States).Select(s => new PackageState(s.Key, new PackageText(s.Name.En, s.Name.Ar), s.Category))],
                PortalEntry = t.PortalEntry,
                DefaultPriority = t.DefaultPriority,
                GrantRoles = [.. allRoles.Where(r => r.Key != BuiltInRoles.TenantAdmin && r.Permissions.Any(p => p.Permission == RecordPermissions.Read(t.Key))).Select(r => r.Key).Order(StringComparer.Ordinal)],
            })],
            Roles = [.. roles.Select(r => new PackageRole
            {
                Key = r.Key,
                Name = PackageText.From(r.Name),
                RequiresMfa = r.RequiresMfa,
                Permissions = [.. r.Permissions.Select(p => p.Permission).Where(p => RecordPermissions.Parse(p) is { } parsed && types.Any(t => t.Key == parsed.Key)).Order(StringComparer.Ordinal)],
            })],
            Forms = forms,
            FormBindings = [.. bindings.Where(b => formKeys.ContainsKey(b.FormId)).Select(b => new PackageFormBinding(typeKeys[b.CustomRecordTypeId!.Value], formKeys[b.FormId], b.CategoryId))],
            Rules = packageRules,
            References = await DescribeAsync(db, references, cancellationToken),
        };
    }

    /// <summary>A field without what only makes sense here: data list items, people and places as defaults; classes noted as references.</summary>
    private static FormField Portable(FormField field, Func<string, Guid, Guid?> note)
    {
        foreach (var id in field.ConfigurationItemClasses ?? [])
        {
            note(ReferenceKinds.CiClass, id);
        }

        return field with
        {
            Options = field.DataList is null ? field.Options : null,
            DefaultValue = field.Type is FieldType.User or FieldType.Location or FieldType.ConfigurationItem ? null : field.DefaultValue,
            Columns = field.Columns is null ? null : [.. field.Columns.Select(c => Portable(c, note))],
        };
    }

    /// <summary>How to find each referenced thing again: names, paths, codes, emails.</summary>
    private static async Task<List<PackageReference>> DescribeAsync(ZelvoDbContext db, Dictionary<Guid, string> references, CancellationToken cancellationToken)
    {
        var ids = references.Keys.ToList();
        var categories = references.ContainsValue(ReferenceKinds.Category) ? await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken) : [];
        var locations = references.ContainsValue(ReferenceKinds.Location) ? await db.Locations.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken) : [];
        var groups = await db.SupportGroups.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var departments = await db.Departments.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var customerGroups = await db.CustomerGroups.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var services = await db.CatalogServices.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Code, cancellationToken);
        var users = await db.Users.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Email, cancellationToken);
        var connectors = await db.Connectors.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var classes = await db.CiClasses.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Key, cancellationToken);

        static List<PackageText>? PathOf<T>(Dictionary<Guid, T> nodes, Guid id)
            where T : TreeNode
        {
            var path = new List<PackageText>();
            Guid? current = id;
            while (current is { } at && nodes.TryGetValue(at, out var node) && path.Count < TreeNode.MaxDepth)
            {
                path.Insert(0, PackageText.From(node.Name));
                current = node.ParentId;
            }

            return path.Count == 0 ? null : path;
        }

        return [.. references.Select(r => r.Value switch
        {
            ReferenceKinds.Category => new PackageReference { Id = r.Key, Kind = r.Value, Path = PathOf(categories, r.Key) },
            ReferenceKinds.Location => new PackageReference { Id = r.Key, Kind = r.Value, Path = PathOf(locations, r.Key) },
            ReferenceKinds.Group => new PackageReference { Id = r.Key, Kind = r.Value, Name = groups.TryGetValue(r.Key, out var g) ? PackageText.From(g) : null },
            ReferenceKinds.Department => new PackageReference { Id = r.Key, Kind = r.Value, Name = departments.TryGetValue(r.Key, out var d) ? PackageText.From(d) : null },
            ReferenceKinds.CustomerGroup => new PackageReference { Id = r.Key, Kind = r.Value, Name = customerGroups.TryGetValue(r.Key, out var c) ? PackageText.From(c) : null },
            ReferenceKinds.Service => new PackageReference { Id = r.Key, Kind = r.Value, Code = services.GetValueOrDefault(r.Key) },
            ReferenceKinds.User => new PackageReference { Id = r.Key, Kind = r.Value, Email = users.GetValueOrDefault(r.Key) },
            ReferenceKinds.Connector => new PackageReference { Id = r.Key, Kind = r.Value, Name = connectors.TryGetValue(r.Key, out var n) ? new PackageText(n, null) : null },
            _ => new PackageReference { Id = r.Key, Kind = r.Value, Code = classes.GetValueOrDefault(r.Key) },
        }).OrderBy(r => r.Kind, StringComparer.Ordinal)];
    }

    /// <summary>The columns stored on a list.</summary>
    public static List<DataListColumn> ReadColumns(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<DataListColumn>>(json, AppPackage.Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The values stored on an item.</summary>
    public static Dictionary<string, string> ReadValues(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json, AppPackage.Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- checks shared with the builder

    /// <summary>Problems of a data list's name, columns and items.</summary>
    public static void CheckDataList(PackageDataList list, string at, List<PackageProblem> problems)
    {
        if (list.Key is not { } key || !FormDefinitionCheck.DataListKeyPattern().IsMatch(key))
        {
            problems.Add(new($"{at}.key", "key-invalid", "Give a key of a lower-case letter, then letters, digits and hyphens, up to 40 characters."));
        }

        CheckName(list.Name, DataList.NameMaxLength, $"{at}.name", problems);
        CheckText(list.Description, DataList.DescriptionMaxLength, $"{at}.description", problems);
        var columns = list.Columns ?? [];
        if (columns.Count > DataList.MaxColumns || columns.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != columns.Count
            || columns.Any(c => c.Key is null || !ColumnKeyPattern().IsMatch(c.Key) || c.Label is null || !c.Label.ToValue().HasAnyValue || !DirectoryProblemsFit(c.Label, DataList.NameMaxLength)))
        {
            problems.Add(new($"{at}.columns", "columns-invalid", $"Give at most {DataList.MaxColumns} columns, each with a different key (a letter, then letters and digits) and a name."));
        }

        var items = list.Items ?? [];
        if (items.Count > DataList.MaxItems)
        {
            problems.Add(new($"{at}.items", "too-many-items", $"A data list has at most {DataList.MaxItems} items."));
        }

        if (items.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            problems.Add(new($"{at}.items", "item-duplicate", "Two items have the same value."));
        }

        var columnKeys = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > DataListItem.KeyMaxLength || item.Key.Trim() != item.Key)
            {
                problems.Add(new($"{at}.items.{i}.key", "item-key-invalid", $"Item values are 1 to {DataListItem.KeyMaxLength} characters without spaces around them."));
            }

            if (item.Label is null || !item.Label.ToValue().HasAnyValue || !DirectoryProblemsFit(item.Label, DataListItem.LabelMaxLength))
            {
                problems.Add(new($"{at}.items.{i}.label", "item-label-invalid", $"Every item needs a label in English or Arabic of at most {DataListItem.LabelMaxLength} characters."));
            }

            if ((item.Values ?? []).Any(v => !columnKeys.Contains(v.Key) || (v.Value?.Length ?? 0) > DataListItem.ValueMaxLength))
            {
                problems.Add(new($"{at}.items.{i}.values", "item-values-invalid", $"Values belong to the list's columns and have at most {DataListItem.ValueMaxLength} characters."));
            }
        }
    }

    /// <summary>
    /// Problems of an app's list views, menu entries and portal entries; <paramref name="states"/>
    /// gives the state keys of each record type the app may use (its own).
    /// </summary>
    public static void CheckDefinition(AppDefinition definition, IReadOnlyDictionary<string, IReadOnlyList<string>> states, string at, List<PackageProblem> problems)
    {
        if (definition.ListViews.Count > AppDefinition.MaxEntries || definition.MenuEntries.Count > AppDefinition.MaxEntries || definition.PortalEntries.Count > AppDefinition.MaxEntries)
        {
            problems.Add(new(at, "too-many-entries", $"An app has at most {AppDefinition.MaxEntries} list views, menu entries and portal entries of each kind."));
        }

        void Entry(string path, string key, PackageText label, string recordType, IEnumerable<string> keys)
        {
            if (!KeyPattern().IsMatch(key ?? ""))
            {
                problems.Add(new($"{path}.key", "key-invalid", "Give a key of lower-case letters, digits and hyphens."));
            }

            if (keys.Count(k => k == key) > 1)
            {
                problems.Add(new($"{path}.key", "key-duplicate", "Two entries of the same kind have this key."));
            }

            CheckName(label, App.NameMaxLength, $"{path}.name", problems);
            if (!states.ContainsKey(recordType ?? ""))
            {
                problems.Add(new($"{path}.recordType", "record-type-unknown", $"The record type '{recordType}' is not part of the app."));
            }
        }

        for (var i = 0; i < definition.ListViews.Count; i++)
        {
            var view = definition.ListViews[i];
            Entry($"{at}.listViews.{i}", view.Key, view.Name, view.RecordType, definition.ListViews.Select(v => v.Key));
            if (view.State is { } state && states.TryGetValue(view.RecordType ?? "", out var keys) && !keys.Contains(state))
            {
                problems.Add(new($"{at}.listViews.{i}.state", "state-unknown", $"The record type has no state '{state}'."));
            }

            if (view.Assignee is not (null or "me" or "none"))
            {
                problems.Add(new($"{at}.listViews.{i}.assignee", "assignee-invalid", "The assignee filter is me or none."));
            }
        }

        for (var i = 0; i < definition.MenuEntries.Count; i++)
        {
            var entry = definition.MenuEntries[i];
            Entry($"{at}.menuEntries.{i}", entry.Key, entry.Label, entry.RecordType, definition.MenuEntries.Select(v => v.Key));
            if (entry.Target is not ("records" or "new"))
            {
                problems.Add(new($"{at}.menuEntries.{i}.target", "target-invalid", "A menu entry opens the records (records) or the form to raise one (new)."));
            }

            if (entry.View is { } view && (entry.Target != "records" || !definition.ListViews.Any(v => v.Key == view && v.RecordType == entry.RecordType)))
            {
                problems.Add(new($"{at}.menuEntries.{i}.view", "view-unknown", $"The app has no list view '{view}' of this record type."));
            }
        }

        for (var i = 0; i < definition.PortalEntries.Count; i++)
        {
            var entry = definition.PortalEntries[i];
            Entry($"{at}.portalEntries.{i}", entry.Key, entry.Label, entry.RecordType, definition.PortalEntries.Select(v => v.Key));
            CheckText(entry.Description, App.DescriptionMaxLength, $"{at}.portalEntries.{i}.description", problems);
        }
    }

    /// <summary>Problems of an app's key, names and icon.</summary>
    public static void CheckApp(string? key, PackageText? name, PackageText? description, string? icon, string at, List<PackageProblem> problems)
    {
        if (!KeyPattern().IsMatch(key ?? ""))
        {
            problems.Add(new($"{at}.key", "key-invalid", "Give a key of lower-case letters, digits and hyphens, starting with a letter, up to 40 characters."));
        }

        CheckName(name, App.NameMaxLength, $"{at}.name", problems);
        CheckText(description, App.DescriptionMaxLength, $"{at}.description", problems);
        if (icon is not null && !Catalog.ServiceCards.Icons.Contains(icon))
        {
            problems.Add(new($"{at}.icon", "icon-unknown", "Choose one of Zelvo's icons."));
        }
    }

    private static void CheckName(PackageText? name, int max, string at, List<PackageProblem> problems)
    {
        if (name is null || !name.ToValue().HasAnyValue || !DirectoryProblemsFit(name, max))
        {
            problems.Add(new(at, "name-invalid", $"Give a name in English or Arabic of at most {max} characters."));
        }
    }

    private static void CheckText(PackageText? text, int max, string at, List<PackageProblem> problems)
    {
        if (text is not null && !DirectoryProblemsFit(text, max))
        {
            problems.Add(new(at, "text-too-long", $"Use at most {max} characters in each language."));
        }
    }

    private static bool DirectoryProblemsFit(PackageText text, int max) => (text.En?.Length ?? 0) <= max && (text.Ar?.Length ?? 0) <= max;

    // ---------------------------------------------------------------- import: plan

    /// <summary>What this installation holds that a package is checked against.</summary>
    private sealed record Target(
        Dictionary<string, App> Apps, Dictionary<string, RecordType> Types, Dictionary<string, DataList> Lists, Dictionary<string, Role> Roles,
        HashSet<string> BuiltInPrefixes, Dictionary<Guid, List<string>> UsedStates, Dictionary<Guid, Guid?> Resolved, List<PackageReferencePreview> References);

    /// <summary>Reads a package; <see langword="null"/> when it is not JSON of the package's shape.</summary>
    public static AppPackage? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AppPackage>(json, AppPackage.Json);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks a package against this installation: its shape and limits, each item with the checks
    /// the builders use, every reference found again, roles' permissions limited to the package's
    /// own record types, and what already exists (a conflict for the administrator to decide).
    /// </summary>
    public static async Task<PackagePlan> PlanAsync(ZelvoDbContext db, TenantSettingsStore settings, AppPackage package, IReadOnlyDictionary<string, string>? choices, CancellationToken cancellationToken)
    {
        var problems = new List<PackageProblem>();
        var target = await LoadAsync(db, settings, package, cancellationToken);
        var items = new List<PackageItem>();
        var conflicts = new List<PackageConflict>();

        string Choice(string kind, string key) => choices?.GetValueOrDefault($"{kind}:{key}") ?? Keep;

        if (package.Format != AppPackage.CurrentFormat)
        {
            problems.Add(new("format", "format-unknown", $"This is not a {AppPackage.CurrentFormat} app package."));
        }

        var app = package.App ?? new PackageApp();
        CheckApp(app.Key, app.Name, app.Description, app.Icon, "app", problems);
        var types = package.RecordTypes ?? [];
        var lists = package.DataLists ?? [];
        var roles = package.Roles ?? [];
        var forms = package.Forms ?? [];
        var bindings = package.FormBindings ?? [];
        var rules = package.Rules ?? [];
        if (types.Count > MaxRecordTypes || lists.Count > MaxDataLists || roles.Count > MaxRoles || forms.Count > MaxForms || bindings.Count > MaxBindings
            || rules.Count > MaxRules || (package.References?.Count ?? 0) > MaxReferences)
        {
            problems.Add(new("", "too-large", $"A package holds at most {MaxRecordTypes} record types, {MaxDataLists} data lists, {MaxRoles} roles, {MaxForms} forms, {MaxBindings} form bindings, {MaxRules} rules and {MaxReferences} references."));
        }

        if (app.Key is { } appKey && target.Apps.TryGetValue(appKey, out var existingApp))
        {
            conflicts.Add(new($"app:{appKey}", "app", appKey, LocalizedTextDto.From(existingApp.Name), "Its name, description, icon, list views, menu entries and portal entries become the package's."));
        }

        items.Add(new("app", app.Key ?? "", LocalizedTextDto.From(app.Name?.ToValue() ?? new LocalizedText(null, null)), app.Key is not null && target.Apps.ContainsKey(app.Key)));

        // Data lists.
        for (var i = 0; i < lists.Count; i++)
        {
            var list = lists[i];
            CheckDataList(list, $"dataLists.{i}", problems);
            if (lists.Take(i).Any(l => l.Key == list.Key))
            {
                problems.Add(new($"dataLists.{i}.key", "key-duplicate", "Two data lists of the package have this key."));
            }

            var exists = list.Key is not null && target.Lists.TryGetValue(list.Key, out var existing);
            if (exists)
            {
                conflicts.Add(new($"dataList:{list.Key}", "dataList", list.Key!, LocalizedTextDto.From(target.Lists[list.Key!].Name),
                    "Its name and columns become the package's; the package's items are added or updated, and items it lacks are no longer offered (never deleted)."));
            }

            items.Add(new("dataList", list.Key ?? "", LocalizedTextDto.From(list.Name?.ToValue() ?? new LocalizedText(null, null)), exists));
        }

        // Record types.
        var packageTypeKeys = types.Select(t => t.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var packageRoleKeys = roles.Select(r => r.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var permissionsCreated = new List<string>();
        var grants = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            var type = types[i];
            var at = $"recordTypes.{i}";
            if (!KeyPattern().IsMatch(type.Key ?? ""))
            {
                problems.Add(new($"{at}.key", "key-invalid", "Give a key of lower-case letters, digits and hyphens, starting with a letter, up to 40 characters."));
            }

            if (types.Take(i).Any(t => t.Key == type.Key))
            {
                problems.Add(new($"{at}.key", "key-duplicate", "Two record types of the package have this key."));
            }

            CheckName(type.Name, RecordType.NameMaxLength, $"{at}.name", problems);
            CheckText(type.Description, RecordType.DescriptionMaxLength, $"{at}.description", problems);
            if (type.DefaultPriority is < 1 or > 5)
            {
                problems.Add(new($"{at}.defaultPriority", "priority-invalid", "The default priority is 1 to 5."));
            }

            var states = (type.States ?? []).Select(s => new RecordState(s.Key ?? "", new SettingText(s.Name?.ToValue().En, s.Name?.ToValue().Ar), s.Category ?? (RecordStateCategory)(-1))).ToList();
            if (RecordStates.Problem(states) is { } stateProblem)
            {
                problems.Add(new($"{at}.states", "states-invalid", stateProblem));
            }

            var existing = type.Key is not null ? target.Types.GetValueOrDefault(type.Key) : null;
            var replacing = existing is not null && Choice("recordType", type.Key!) == Replace;
            if (existing is not null)
            {
                conflicts.Add(new($"recordType:{type.Key}", "recordType", type.Key!, LocalizedTextDto.From(existing.Name),
                    "Its names, number prefix (new numbers only), states, portal entry and default priority become the package's; its forms are replaced by the package's and its workflow rules are switched off and replaced by the package's."));
                if (replacing)
                {
                    // States in use stay, with their category, as in Settings → Record types.
                    var current = RecordStates.Read(existing.States);
                    foreach (var used in target.UsedStates.GetValueOrDefault(existing.Id) ?? [])
                    {
                        var now = states.FirstOrDefault(s => s.Key == used);
                        if (now is null || now.Category != current.FirstOrDefault(s => s.Key == used)?.Category)
                        {
                            problems.Add(new($"{at}.states", "state-in-use", $"Records are in the state '{used}' of the existing type; the package must keep it with the same category to replace the type."));
                        }
                    }
                }
            }

            var prefix = type.NumberPrefix ?? "";
            if (!PrefixPattern().IsMatch(prefix))
            {
                problems.Add(new($"{at}.numberPrefix", "prefix-invalid", "Give 1–10 capital letters or digits starting with a letter."));
            }
            else if (target.BuiltInPrefixes.Contains(prefix)
                || types.Take(i).Any(t => t.NumberPrefix == prefix)
                || ((existing is null || replacing) && target.Types.Values.Any(t => t.NumberPrefix == prefix && t.Key != type.Key)))
            {
                problems.Add(new($"{at}.numberPrefix", "prefix-taken", $"The number prefix {prefix} is already used here."));
            }

            if (existing is null || replacing)
            {
                foreach (var role in (type.GrantRoles ?? []).Append(BuiltInRoles.TenantAdmin).Distinct(StringComparer.Ordinal))
                {
                    if (role == BuiltInRoles.Requester)
                    {
                        problems.Add(new($"{at}.grantRoles", "grant-requester", "A record type's permissions are never given to every requester; requesters always see their own records."));
                    }
                    else if (!packageRoleKeys.Contains(role) && !target.Roles.ContainsKey(role))
                    {
                        problems.Add(new($"{at}.grantRoles", "role-unknown", $"There is no role '{role}' here or in the package."));
                    }
                    else if (!(packageRoleKeys.Contains(role) && target.Roles.ContainsKey(role) && Choice("role", role) == Keep))
                    {
                        var list = grants.TryGetValue(role, out var held) ? held : grants[role] = [];
                        list.AddRange(RecordPermissions.Actions.Select(a => $"records.{type.Key}.{a}"));
                    }
                }

                if (existing is null)
                {
                    permissionsCreated.AddRange(RecordPermissions.Actions.Select(a => $"records.{type.Key}.{a}"));
                }
            }

            items.Add(new("recordType", type.Key ?? "", LocalizedTextDto.From(type.Name?.ToValue() ?? new LocalizedText(null, null)), existing is not null));
        }

        // Roles.
        for (var i = 0; i < roles.Count; i++)
        {
            var role = roles[i];
            var at = $"roles.{i}";
            if (role.Key is not { } key || !RoleKeyPattern().IsMatch(key) || BuiltInRoles.All.Contains(key))
            {
                problems.Add(new($"{at}.key", "key-invalid", "Give a key of lower-case letters, digits and hyphens that is not a built-in role's."));
            }

            if (roles.Take(i).Any(r => r.Key == role.Key))
            {
                problems.Add(new($"{at}.key", "key-duplicate", "Two roles of the package have this key."));
            }

            CheckName(role.Name, Role.NameMaxLength, $"{at}.name", problems);
            foreach (var permission in role.Permissions ?? [])
            {
                if (RecordPermissions.Parse(permission) is not { } parsed || !packageTypeKeys.Contains(parsed.Key))
                {
                    problems.Add(new($"{at}.permissions", "permission-not-allowed", $"An app's role holds only the permissions of the package's record types; '{permission}' is not one."));
                }
            }

            var exists = role.Key is not null && target.Roles.TryGetValue(role.Key, out var existing);
            if (exists)
            {
                var existingRole = target.Roles[role.Key!];
                if (existingRole.IsSystem)
                {
                    problems.Add(new($"{at}.key", "role-built-in", "A package cannot change a built-in role."));
                }

                conflicts.Add(new($"role:{role.Key}", "role", role.Key!, LocalizedTextDto.From(existingRole.Name),
                    "Its name and second-factor setting become the package's and it gets the package's permissions (it keeps the others)."));
            }

            if (!exists || Choice("role", role.Key!) == Replace)
            {
                var list = grants.TryGetValue(role.Key ?? "", out var held) ? held : grants[role.Key ?? ""] = [];
                list.AddRange(role.Permissions ?? []);
            }

            items.Add(new("role", role.Key ?? "", LocalizedTextDto.From(role.Name?.ToValue() ?? new LocalizedText(null, null)), exists));
        }

        // References: each found again here, or a problem.
        foreach (var reference in package.References ?? [])
        {
            if (reference.Kind is null || !ReferenceKinds.All.Contains(reference.Kind))
            {
                problems.Add(new("references", "reference-kind-unknown", $"Unknown kind of reference '{reference.Kind}'."));
            }
        }

        foreach (var preview in target.References.Where(r => r.FoundId is null))
        {
            problems.Add(new("references", "reference-not-found", $"The package refers to the {preview.Kind} '{preview.Label}', which this installation does not have. Add it here first, then check the package again."));
        }

        Guid? Map(string kind, Guid id) => target.Resolved.GetValueOrDefault(id);
        var dataListKeys = lists.Select(l => l.Key).OfType<string>().Concat(target.Lists.Keys).ToHashSet(StringComparer.Ordinal);

        // Forms.
        var formKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < forms.Count; i++)
        {
            var form = forms[i];
            var at = $"forms.{i}";
            if (form.Key is not { } key || !FormsCommonKey().IsMatch(key) || !formKeys.Add(key))
            {
                problems.Add(new($"{at}.key", "key-invalid", "Each form has a different key of lower-case letters, digits and hyphens."));
            }

            CheckName(form.Name, Form.NameMaxLength, $"{at}.name", problems);
            if (ReadFields(form.Fields) is not { } fields)
            {
                problems.Add(new($"{at}.fields", "fields-invalid", "The fields are not in the form builder's shape."));
                continue;
            }

            if (fields.Count == 0)
            {
                problems.Add(new($"{at}.fields", "form-empty", "Add at least one field."));
            }

            problems.AddRange(FormDefinitionCheck.Check(fields).Select(p => new PackageProblem($"{at}.fields{(p.Field is null ? "" : "." + p.Field)}", p.Code, p.Message)));
            foreach (var list in fields.SelectMany(DataListOptions.Keys).Distinct(StringComparer.Ordinal).Where(k => !dataListKeys.Contains(k)))
            {
                problems.Add(new($"{at}.fields", "data-list-unknown", $"There is no data list '{list}' here or in the package."));
            }

            if (fields.SelectMany(f => f.ConfigurationItemClasses ?? []).Any(id => !target.Resolved.ContainsKey(id)))
            {
                problems.Add(new($"{at}.fields", "reference-missing", "A configuration item field names a class the package does not list in its references."));
            }
        }

        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            if (binding.RecordType is null || !packageTypeKeys.Contains(binding.RecordType) || binding.Form is null || !formKeys.Contains(binding.Form))
            {
                problems.Add(new($"formBindings.{i}", "binding-invalid", "A form binding names one of the package's record types and forms."));
            }

            if (binding.Category is { } category && !target.Resolved.ContainsKey(category))
            {
                problems.Add(new($"formBindings.{i}.category", "reference-missing", "The binding's category is not in the package's references."));
            }

            if (bindings.Take(i).Any(b => b.RecordType == binding.RecordType && b.Category == binding.Category))
            {
                problems.Add(new($"formBindings.{i}", "binding-duplicate", "Two bindings of the same record type have the same category."));
            }
        }

        // Rules: checked like the builder checks them, with this installation's ids.
        var roleForCheck = new Func<string, string>(k => packageRoleKeys.Contains(k) ? BuiltInRoles.TenantAdmin : k);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var at = $"rules.{i}";
            if (rule.RecordType is null || !packageTypeKeys.Contains(rule.RecordType))
            {
                problems.Add(new($"{at}.recordType", "record-type-unknown", "A rule of a package is on one of the package's record types."));
                continue;
            }

            if (target.References.Any(r => r.FoundId is null))
            {
                continue;
            }

            var input = RuleInput(rule, Map, roleForCheck, out var shapeProblem);
            if (shapeProblem is not null || input is null)
            {
                problems.Add(new(at, "rule-invalid", shapeProblem ?? "The rule is not in the builder's shape."));
                continue;
            }

            var (_, found) = await WorkflowRuleEndpoints.CheckAsync(input, db, cancellationToken);
            problems.AddRange(found.Select(p => new PackageProblem($"{at}{(p.Field is null ? "" : "." + p.Field)}", p.Code, p.Message)));
        }

        // The app's own entries, against the package's record types.
        var typeStates = types.Where(t => t.Key is not null).GroupBy(t => t.Key!).ToDictionary(
            g => g.Key, g => (IReadOnlyList<string>)[.. (g.First().States ?? []).Select(s => s.Key ?? "")], StringComparer.Ordinal);
        var definition = app.Definition ?? new AppDefinition();
        CheckDefinition(definition, typeStates, "app.definition", problems);

        return new PackagePlan(
            app.Key, LocalizedTextDto.From(app.Name?.ToValue() ?? new LocalizedText(null, null)), app.Icon, package.Source, package.ExportedAt,
            problems, conflicts, items, permissionsCreated,
            [.. grants.Where(g => g.Value.Count > 0).Select(g => new PackageGrant(g.Key, target.Roles.ContainsKey(g.Key), [.. g.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))],
            target.References, forms.Count, rules.Count, definition.ListViews.Count, definition.MenuEntries.Count, definition.PortalEntries.Count);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex FormsCommonKey();

    /// <summary>The fields of a package form, or <see langword="null"/> when they are not in C1's shape.</summary>
    private static List<FormField>? ReadFields(JsonArray? fields)
    {
        if (fields is null)
        {
            return null;
        }

        try
        {
            return fields.Deserialize<List<FormField>>(FormJson.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A package rule as the builder's input, with ids mapped and role keys put through <paramref name="role"/>.</summary>
    private static WorkflowRuleInput? RuleInput(PackageRule rule, Func<string, Guid, Guid?> map, Func<string, string> role, out string? problem)
    {
        problem = null;
        List<WorkflowCondition> conditions;
        List<WorkflowAction> actions;
        try
        {
            conditions = rule.Conditions?.Deserialize<List<WorkflowCondition>>(RuleDocuments.Json) ?? [];
            actions = rule.Actions?.Deserialize<List<WorkflowAction>>(RuleDocuments.Json) ?? [];
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            problem = "The rule's conditions or actions are not in the builder's shape.";
            return null;
        }

        if (rule.Trigger is null)
        {
            problem = "The rule has no trigger.";
            return null;
        }

        return new WorkflowRuleInput
        {
            Name = new LocalizedTextInput { En = rule.Name?.En, Ar = rule.Name?.Ar },
            Description = rule.Description is null ? null : new LocalizedTextInput { En = rule.Description.En, Ar = rule.Description.Ar },
            Active = rule.Active,
            Trigger = rule.Trigger,
            RecordType = TicketType.Custom,
            Match = rule.Match,
            Conditions = JsonSerializer.SerializeToNode(RuleReferences.Conditions(conditions, map), RuleDocuments.Json)!.AsArray(),
            Actions = JsonSerializer.SerializeToNode(RuleReferences.Actions(actions, map, role), RuleDocuments.Json)!.AsArray(),
            StopProcessing = rule.StopProcessing,
        };
    }

    /// <summary>What the package is checked against, with each reference looked up.</summary>
    private static async Task<Target> LoadAsync(ZelvoDbContext db, TenantSettingsStore settings, AppPackage package, CancellationToken cancellationToken)
    {
        var builtIn = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken);
        var types = await db.RecordTypes.AsNoTracking().ToListAsync(cancellationToken);
        var used = (await db.CustomRecords.AsNoTracking().Select(r => new { r.RecordTypeId, r.StateKey }).Distinct().ToListAsync(cancellationToken))
            .GroupBy(r => r.RecordTypeId).ToDictionary(g => g.Key, g => g.Select(r => r.StateKey).ToList());
        var resolved = new Dictionary<Guid, Guid?>();
        var previews = new List<PackageReferencePreview>();
        var references = (package.References ?? []).Where(r => r.Kind is not null && ReferenceKinds.All.Contains(r.Kind)).Take(MaxReferences).ToList();
        if (references.Count > 0)
        {
            var categories = await db.Categories.AsNoTracking().ToListAsync(cancellationToken);
            var locations = await db.Locations.AsNoTracking().ToListAsync(cancellationToken);
            var groups = await db.SupportGroups.AsNoTracking().Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken);
            var departments = await db.Departments.AsNoTracking().Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken);
            var customerGroups = await db.CustomerGroups.AsNoTracking().Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken);
            var services = await db.CatalogServices.AsNoTracking().Select(g => new { g.Id, g.Code, g.Name }).ToListAsync(cancellationToken);
            var connectors = await db.Connectors.AsNoTracking().Where(c => c.Kind == Data.Connectors.ConnectorKind.Rest).Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken);
            var classes = await db.CiClasses.AsNoTracking().Select(g => new { g.Id, g.Key, g.Name }).ToListAsync(cancellationToken);
            var emails = references.Where(r => r.Kind == ReferenceKinds.User && r.Email is not null).Select(r => r.Email!.ToLowerInvariant()).ToList();
#pragma warning disable CA1304, CA1311 // Translated to PostgreSQL's lower().
            var users = await db.Users.AsNoTracking().Where(u => u.Email != null && emails.Contains(u.Email.ToLower()) && u.ErasedAt == null)
                .Select(u => new { u.Id, u.Email, u.DisplayName }).ToListAsync(cancellationToken);
#pragma warning restore CA1304, CA1311

            static bool Same(LocalizedText name, PackageText? wanted) =>
                wanted is not null && ((wanted.En is { } en && string.Equals(name.En, en.Trim(), StringComparison.OrdinalIgnoreCase))
                    || (wanted.Ar is { } ar && string.Equals(name.Ar, ar.Trim(), StringComparison.Ordinal)));

            static T? InTree<T>(List<T> nodes, List<PackageText>? path)
                where T : TreeNode
            {
                T? found = null;
                foreach (var step in path ?? [])
                {
                    found = nodes.FirstOrDefault(n => n.ParentId == found?.Id && Same(n.Name, step));
                    if (found is null)
                    {
                        return null;
                    }
                }

                return found;
            }

            static string Label(PackageText? text) => text?.En ?? text?.Ar ?? "?";

            foreach (var reference in references)
            {
                (Guid? Id, LocalizedText? Name, string Label) hit = reference.Kind switch
                {
                    ReferenceKinds.Category => InTree(categories, reference.Path) is { } c ? (c.Id, c.Name, "") : (null, null, ""),
                    ReferenceKinds.Location => InTree(locations, reference.Path) is { } l ? (l.Id, l.Name, "") : (null, null, ""),
                    ReferenceKinds.Group => groups.FirstOrDefault(g => Same(g.Name, reference.Name)) is { } g ? (g.Id, g.Name, "") : (null, null, ""),
                    ReferenceKinds.Department => departments.FirstOrDefault(g => Same(g.Name, reference.Name)) is { } d ? (d.Id, d.Name, "") : (null, null, ""),
                    ReferenceKinds.CustomerGroup => customerGroups.FirstOrDefault(g => Same(g.Name, reference.Name)) is { } cg ? (cg.Id, cg.Name, "") : (null, null, ""),
                    ReferenceKinds.Service => services.FirstOrDefault(s => s.Code == reference.Code) is { } s ? (s.Id, s.Name, "") : (null, null, ""),
                    ReferenceKinds.Connector => connectors.FirstOrDefault(c => string.Equals(c.Name, reference.Name?.En, StringComparison.OrdinalIgnoreCase)) is { } cn ? (cn.Id, new LocalizedText(cn.Name, null), "") : (null, null, ""),
                    ReferenceKinds.User => users.FirstOrDefault(u => string.Equals(u.Email, reference.Email, StringComparison.OrdinalIgnoreCase)) is { } u ? (u.Id, new LocalizedText(u.DisplayName, null), "") : (null, null, ""),
                    _ => classes.FirstOrDefault(c => c.Key == reference.Code) is { } k ? (k.Id, k.Name, "") : (null, null, ""),
                };
                var label = reference.Kind switch
                {
                    ReferenceKinds.Category or ReferenceKinds.Location => string.Join(" › ", (reference.Path ?? []).Select(Label)),
                    ReferenceKinds.Service or ReferenceKinds.CiClass => reference.Code ?? "?",
                    ReferenceKinds.User => reference.Email ?? "?",
                    _ => Label(reference.Name),
                };
                resolved[reference.Id] = hit.Id;
                previews.Add(new PackageReferencePreview(reference.Kind!, label, hit.Id, hit.Name is null ? null : LocalizedTextDto.From(hit.Name)));
            }
        }

        return new Target(
            await db.Apps.AsNoTracking().ToDictionaryAsync(a => a.Key, StringComparer.Ordinal, cancellationToken),
            types.ToDictionary(t => t.Key, StringComparer.Ordinal),
            await db.DataLists.AsNoTracking().ToDictionaryAsync(l => l.Key, StringComparer.Ordinal, cancellationToken),
            await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken),
            [.. TicketNumbers.BuiltInTypes.Select(t => TicketNumbers.PrefixOf(builtIn, t))],
            used, resolved, previews);
    }

    // ---------------------------------------------------------------- import: install

    /// <summary>What an approval did.</summary>
    public sealed record InstallResult(Guid AppId, List<string> Added, List<string> Replaced, List<string> Kept);

    /// <summary>
    /// Installs a checked package in the caller's transaction (saved by the caller), following the
    /// administrator's choices; audits each item. Run <see cref="PlanAsync"/> first: it must have no problems.
    /// </summary>
    public static async Task<InstallResult> InstallAsync(
        ZelvoDbContext db, TenantSettingsStore settings, IAuditLog audit, AppPackage package, IReadOnlyDictionary<string, string> choices, Guid? actorId, Guid? importId, CancellationToken cancellationToken)
    {
        var target = await LoadAsync(db, settings, package, cancellationToken);
        var added = new List<string>();
        var replaced = new List<string>();
        var kept = new List<string>();
        string Choice(string kind, string key) => choices.GetValueOrDefault($"{kind}:{key}") ?? Keep;
        Guid? Map(string kind, Guid id) => target.Resolved.GetValueOrDefault(id);
        var now = DateTimeOffset.UtcNow;
        var pkgApp = package.App!;

        // The app.
        var app = await db.Apps.SingleOrDefaultAsync(a => a.Key == pkgApp.Key, cancellationToken);
        if (app is null)
        {
            app = new App { Key = pkgApp.Key!, Name = pkgApp.Name!.ToValue(), CreatedById = actorId, ImportId = importId };
            db.Apps.Add(app);
            added.Add($"app:{app.Key}");
        }
        else if (Choice("app", app.Key) == Replace)
        {
            replaced.Add($"app:{app.Key}");
        }
        else
        {
            kept.Add($"app:{app.Key}");
        }

        if (!kept.Contains($"app:{app.Key}"))
        {
            app.Name = pkgApp.Name!.ToValue();
            app.Description = pkgApp.Description?.ToValue() ?? new LocalizedText(null, null);
            app.Icon = pkgApp.Icon ?? "service";
            app.Definition = (pkgApp.Definition ?? new AppDefinition()).Write();
            app.Active = true;
            app.Suspended = "{}";
            app.ImportId = importId;
            app.UpdatedAt = now;
        }

        // Data lists.
        foreach (var list in package.DataLists ?? [])
        {
            var existing = await db.DataLists.SingleOrDefaultAsync(l => l.Key == list.Key, cancellationToken);
            if (existing is not null && Choice("dataList", existing.Key) == Keep)
            {
                kept.Add($"dataList:{existing.Key}");
                existing.AppId ??= app.Id;
                continue;
            }

            if (existing is null)
            {
                existing = new DataList { Key = list.Key!, Name = list.Name!.ToValue() };
                db.DataLists.Add(existing);
                added.Add($"dataList:{existing.Key}");
            }
            else
            {
                replaced.Add($"dataList:{existing.Key}");
            }

            existing.Name = list.Name!.ToValue();
            existing.Description = list.Description?.ToValue() ?? new LocalizedText(null, null);
            existing.Columns = JsonSerializer.Serialize(list.Columns ?? [], AppPackage.Compact);
            existing.AppId = app.Id;
            existing.UpdatedAt = now;
            await SetItemsAsync(db, existing, list.Items ?? [], cancellationToken);
        }

        // Roles (the package's permissions are added when the record types are).
        var roleGrants = new Dictionary<string, Role>(StringComparer.Ordinal);
        foreach (var pkgRole in package.Roles ?? [])
        {
            var role = await db.Roles.Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Key == pkgRole.Key, cancellationToken);
            if (role is not null && Choice("role", role.Key) == Keep)
            {
                kept.Add($"role:{role.Key}");
                continue;
            }

            if (role is null)
            {
                role = new Role { Key = pkgRole.Key!, Name = pkgRole.Name!.ToValue(), IsSystem = false };
                db.Roles.Add(role);
                added.Add($"role:{role.Key}");
            }
            else
            {
                replaced.Add($"role:{role.Key}");
            }

            role.Name = pkgRole.Name!.ToValue();
            role.RequiresMfa = pkgRole.RequiresMfa ?? false;
            role.AppId = app.Id;
            role.UpdatedAt = now;
            roleGrants[role.Key] = role;
        }

        // Record types and the permissions they make.
        var typeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var installedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pkgType in package.RecordTypes ?? [])
        {
            var type = await db.RecordTypes.SingleOrDefaultAsync(t => t.Key == pkgType.Key, cancellationToken);
            var states = RecordStates.Write([.. (pkgType.States ?? []).Select(s => new RecordState(s.Key!, new SettingText(s.Name?.ToValue().En, s.Name?.ToValue().Ar), s.Category!.Value))]);
            if (type is not null && Choice("recordType", type.Key) == Keep)
            {
                kept.Add($"recordType:{type.Key}");
                type.AppId ??= app.Id;
                typeIds[type.Key] = type.Id;
                continue;
            }

            if (type is null)
            {
                type = new RecordType { Key = pkgType.Key!, Name = pkgType.Name!.ToValue(), NumberPrefix = pkgType.NumberPrefix! };
                db.RecordTypes.Add(type);
                added.Add($"recordType:{type.Key}");
            }
            else
            {
                replaced.Add($"recordType:{type.Key}");
            }

            type.Name = pkgType.Name!.ToValue();
            type.Description = pkgType.Description?.ToValue() ?? new LocalizedText(null, null);
            type.NumberPrefix = pkgType.NumberPrefix!;
            type.States = states;
            type.PortalEntry = pkgType.PortalEntry ?? false;
            type.DefaultPriority = pkgType.DefaultPriority ?? 3;
            type.Active = true;
            type.AppId = app.Id;
            type.UpdatedAt = now;
            typeIds[type.Key] = type.Id;
            installedTypes.Add(type.Key);

            foreach (var roleKey in (pkgType.GrantRoles ?? []).Append(BuiltInRoles.TenantAdmin).Distinct(StringComparer.Ordinal))
            {
                if ((package.Roles ?? []).Any(r => r.Key == roleKey) && !roleGrants.ContainsKey(roleKey))
                {
                    continue; // A package role the administrator kept as it is.
                }

                var role = roleGrants.GetValueOrDefault(roleKey) ?? await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.Key == roleKey, cancellationToken);
                Grant(role, RecordPermissions.Actions.Select(a => $"records.{type.Key}.{a}"));
            }
        }

        foreach (var pkgRole in (package.Roles ?? []).Where(r => roleGrants.ContainsKey(r.Key!)))
        {
            Grant(roleGrants[pkgRole.Key!], (pkgRole.Permissions ?? []).Where(p => RecordPermissions.Parse(p) is { } parsed && installedTypes.Contains(parsed.Key)));
        }

        await db.SaveChangesAsync(cancellationToken);

        // Forms and their bindings for the types installed (added or replaced).
        var formIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var bindings = (package.FormBindings ?? []).Where(b => installedTypes.Contains(b.RecordType!)).ToList();
        var takenFormKeys = (await db.Forms.AsNoTracking().Select(f => f.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var versions = new List<(Form Form, FormVersion Version)>();
        foreach (var pkgForm in (package.Forms ?? []).Where(f => bindings.Any(b => b.Form == f.Key)))
        {
            var fields = ReadFields(pkgForm.Fields)!.Select(f => MapField(f, Map)).ToList();
            var key = pkgForm.Key!;
            for (var n = 2; takenFormKeys.Contains(key); n++)
            {
                key = $"{pkgForm.Key![..Math.Min(pkgForm.Key!.Length, 55)]}-{n}";
            }

            takenFormKeys.Add(key);
            var schema = FormJson.Write(new FormSchema { Elements = [.. fields.Select(f => new FormElement { Type = FormElementType.Field, Field = f })] });
            var form = new Form
            {
                Key = key, Name = pkgForm.Name!.ToValue(), Description = pkgForm.Description?.ToValue() ?? new LocalizedText(null, null),
                State = FormState.Draft, DraftSchema = schema, HasUnpublishedChanges = false, CreatedById = actorId,
            };
            db.Forms.Add(form);
            versions.Add((form, new FormVersion { FormId = form.Id, Number = 1, Schema = schema, PublishedById = actorId }));
            formIds[pkgForm.Key!] = form.Id;
            added.Add($"form:{key}");
        }

        // A form and its published version point at each other: saved one after the other.
        await db.SaveChangesAsync(cancellationToken);
        db.FormVersions.AddRange(versions.Select(v => v.Version));
        await db.SaveChangesAsync(cancellationToken);
        versions.ForEach(v => { v.Form.PublishedVersionId = v.Version.Id; v.Form.State = FormState.Published; });

        foreach (var typeKey in installedTypes)
        {
            var typeId = typeIds[typeKey];
            db.FormBindings.RemoveRange(await db.FormBindings.Where(b => b.CustomRecordTypeId == typeId).ToListAsync(cancellationToken));
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var binding in bindings)
        {
            db.FormBindings.Add(new FormBinding
            {
                RecordType = TicketType.Custom, CustomRecordTypeId = typeIds[binding.RecordType!],
                CategoryId = binding.Category is { } category ? Map(ReferenceKinds.Category, category) : null, FormId = formIds[binding.Form!],
            });
        }

        // Rules: those of replaced types switched off, the package's added at the end.
        var replacedTypeIds = replaced.Where(r => r.StartsWith("recordType:", StringComparison.Ordinal)).Select(r => typeIds[r[11..]]).ToList();
        foreach (var old in await db.WorkflowRules.Where(r => r.CustomRecordTypeId != null && replacedTypeIds.Contains(r.CustomRecordTypeId.Value) && r.Active).ToListAsync(cancellationToken))
        {
            old.Active = false;
            old.UpdatedAt = now;
        }

        var position = (await db.WorkflowRules.MaxAsync(r => (int?)r.Position, cancellationToken) ?? -1) + 1;
        foreach (var pkgRule in (package.Rules ?? []).Where(r => installedTypes.Contains(r.RecordType!)))
        {
            var input = RuleInput(pkgRule, Map, k => k, out _)!;
            var (parsed, problems) = await WorkflowRuleEndpoints.CheckAsync(input, db, cancellationToken);
            if (parsed is null)
            {
                throw new InvalidOperationException($"A checked rule failed its check: {string.Join("; ", problems.Select(p => p.Message))}");
            }

            var rule = new WorkflowRule { Name = parsed.Name, Position = position++, CreatedById = actorId };
            WorkflowRuleEndpoints.Apply(rule, parsed with { CustomRecordTypeId = typeIds[pkgRule.RecordType!] });
            db.WorkflowRules.Add(rule);
            added.Add("rule");
        }

        foreach (var item in added.Where(a => a != "rule").Concat(replaced))
        {
            audit.Add(AuditCategory.Admin, added.Contains(item) ? "apps.import.added" : "apps.import.replaced", importId is null ? "app" : "app_import", (importId ?? app.Id).ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["item"] = item, ["app"] = app.Key });
        }

        return new InstallResult(app.Id, added, replaced, kept);
    }

    private static void Grant(Role role, IEnumerable<string> permissions)
    {
        foreach (var permission in permissions)
        {
            if (role.Permissions.All(p => p.Permission != permission))
            {
                role.Permissions.Add(new RoleGrant { Permission = permission });
            }
        }

        role.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>A package field with this installation's ids.</summary>
    private static FormField MapField(FormField field, Func<string, Guid, Guid?> map) => field with
    {
        ConfigurationItemClasses = field.ConfigurationItemClasses is null ? null : [.. field.ConfigurationItemClasses.Select(id => map(ReferenceKinds.CiClass, id) ?? id)],
        Columns = field.Columns is null ? null : [.. field.Columns.Select(c => MapField(c, map))],
    };

    /// <summary>
    /// Sets a list's items: the given ones added or updated in their order; items not given are no
    /// longer offered (kept, so answers that chose them keep their label).
    /// </summary>
    public static async Task SetItemsAsync(ZelvoDbContext db, DataList list, IReadOnlyList<PackageDataListItem> items, CancellationToken cancellationToken)
    {
        var existing = await db.DataListItems.Where(i => i.DataListId == list.Id).ToDictionaryAsync(i => i.Key, StringComparer.Ordinal, cancellationToken);
        var compact = AppPackage.Compact;
        for (var i = 0; i < items.Count; i++)
        {
            var given = items[i];
            if (!existing.TryGetValue(given.Key!, out var item))
            {
                item = new DataListItem { DataListId = list.Id, Key = given.Key!, Label = given.Label!.ToValue() };
                db.DataListItems.Add(item);
            }

            item.Label = given.Label!.ToValue();
            item.Values = JsonSerializer.Serialize(given.Values ?? [], compact);
            item.Active = given.Active ?? true;
            item.Position = i;
        }

        var keys = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in existing.Values.Where(i => !keys.Contains(i.Key)))
        {
            stale.Active = false;
            stale.Position = items.Count + stale.Position;
        }
    }
}
