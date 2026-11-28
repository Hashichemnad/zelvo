using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets;

namespace Zelvo.Api.Apps;

/// <summary>
/// An app package (<c>zelvo-app/1</c>, task K3): everything an app groups, self-contained, so it
/// can move from one installation to another (for example from the test installation to the live
/// one). Items of the package refer to each other by key; things the package does not carry
/// (groups, categories, locations, departments, customer groups, services, people, connectors,
/// configuration item classes) are listed in <see cref="References"/> by name, path, code or email
/// and found again in the installation that imports it. Packages carry no code.
/// </summary>
internal sealed record AppPackage
{
    /// <summary>The format this API writes and reads.</summary>
    public const string CurrentFormat = "zelvo-app/1";

    /// <summary>Always <see cref="CurrentFormat"/>.</summary>
    public string? Format { get; init; }

    /// <summary>When it was exported.</summary>
    public DateTimeOffset? ExportedAt { get; init; }

    /// <summary>Where it was exported from (for the preview only).</summary>
    public PackageSource? Source { get; init; }

    /// <summary>The app itself.</summary>
    public PackageApp? App { get; init; }

    /// <summary>Data lists with their items.</summary>
    public List<PackageDataList>? DataLists { get; init; }

    /// <summary>Custom record types.</summary>
    public List<PackageRecordType>? RecordTypes { get; init; }

    /// <summary>Custom roles of the app.</summary>
    public List<PackageRole>? Roles { get; init; }

    /// <summary>Forms of the app's record types (fields of field sets included).</summary>
    public List<PackageForm>? Forms { get; init; }

    /// <summary>Which form each record type uses, per category.</summary>
    public List<PackageFormBinding>? FormBindings { get; init; }

    /// <summary>Workflow rules on the app's record types.</summary>
    public List<PackageRule>? Rules { get; init; }

    /// <summary>Things outside the package that its items refer to.</summary>
    public List<PackageReference>? References { get; init; }

    /// <summary>JSON of packages: camelCase, enums as names, nulls left out.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64,
        WriteIndented = true,
    };

    /// <summary>The same on one line, for what is stored.</summary>
    public static JsonSerializerOptions Compact { get; } = new(Json) { WriteIndented = false };
}

/// <summary>Text in English and Arabic.</summary>
internal sealed record PackageText(string? En, string? Ar)
{
    /// <summary>From a stored value.</summary>
    public static PackageText From(LocalizedText text) => new(text.En, text.Ar);

    /// <summary>As a stored value (blank languages missing).</summary>
    public LocalizedText ToValue() => new(string.IsNullOrWhiteSpace(En) ? null : En.Trim(), string.IsNullOrWhiteSpace(Ar) ? null : Ar.Trim());
}

/// <summary>Where a package came from.</summary>
internal sealed record PackageSource(string? Profile, string? ProductVersion);

/// <summary>The app: key, names, icon, and its list views, menu entries and portal entries.</summary>
internal sealed record PackageApp
{
    /// <summary>Key.</summary>
    public string? Key { get; init; }

    /// <summary>Name.</summary>
    public PackageText? Name { get; init; }

    /// <summary>Description.</summary>
    public PackageText? Description { get; init; }

    /// <summary>Icon of Zelvo's set.</summary>
    public string? Icon { get; init; }

    /// <summary>List views, menu entries and portal entries.</summary>
    public AppDefinition? Definition { get; init; }
}

/// <summary>An app's list views, menu entries and portal entries (stored in <c>apps.definition</c>).</summary>
internal sealed record AppDefinition
{
    /// <summary>Most entries of each kind.</summary>
    public const int MaxEntries = 50;

    /// <summary>Saved filters of a record type's list.</summary>
    public List<AppListView> ListViews { get; init; } = [];

    /// <summary>Links on the app's page for staff.</summary>
    public List<AppMenuEntry> MenuEntries { get; init; } = [];

    /// <summary>Links on the portal for people who may raise records.</summary>
    public List<AppPortalEntry> PortalEntries { get; init; } = [];

    /// <summary>Reads a stored definition.</summary>
    public static AppDefinition Read(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<AppDefinition>(json, AppPackage.Json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>As stored.</summary>
    public string Write() => JsonSerializer.Serialize(this, AppPackage.Compact);
}

/// <summary>A list view: a record type's list with a filter (state, open only, assignee).</summary>
internal sealed record AppListView
{
    /// <summary>Key, unique in the app.</summary>
    public string Key { get; init; } = "";

    /// <summary>Name.</summary>
    public PackageText Name { get; init; } = new(null, null);

    /// <summary>The record type's key.</summary>
    public string RecordType { get; init; } = "";

    /// <summary>One state of the type, or none for any.</summary>
    public string? State { get; init; }

    /// <summary>Only records that are not resolved or closed.</summary>
    public bool? Open { get; init; }

    /// <summary><c>me</c> (assigned to the viewer) or <c>none</c> (not assigned), or none for anyone.</summary>
    public string? Assignee { get; init; }
}

/// <summary>A menu entry: the list of a record type (optionally a view) or the form to raise one.</summary>
internal sealed record AppMenuEntry
{
    /// <summary>Key, unique in the app.</summary>
    public string Key { get; init; } = "";

    /// <summary>Label.</summary>
    public PackageText Label { get; init; } = new(null, null);

    /// <summary><c>records</c> or <c>new</c>.</summary>
    public string Target { get; init; } = "records";

    /// <summary>The record type's key.</summary>
    public string RecordType { get; init; } = "";

    /// <summary>With <c>records</c>: a list view's key.</summary>
    public string? View { get; init; }
}

/// <summary>A portal entry: raise a record of a type, with a label and description of its own.</summary>
internal sealed record AppPortalEntry
{
    /// <summary>Key, unique in the app.</summary>
    public string Key { get; init; } = "";

    /// <summary>Label.</summary>
    public PackageText Label { get; init; } = new(null, null);

    /// <summary>What it is for.</summary>
    public PackageText Description { get; init; } = new(null, null);

    /// <summary>The record type's key.</summary>
    public string RecordType { get; init; } = "";
}

/// <summary>A column of a data list.</summary>
internal sealed record DataListColumn(string Key, PackageText Label);

/// <summary>A data list.</summary>
internal sealed record PackageDataList
{
    /// <summary>Key.</summary>
    public string? Key { get; init; }

    /// <summary>Name.</summary>
    public PackageText? Name { get; init; }

    /// <summary>Description.</summary>
    public PackageText? Description { get; init; }

    /// <summary>Extra columns.</summary>
    public List<DataListColumn>? Columns { get; init; }

    /// <summary>Items in order.</summary>
    public List<PackageDataListItem>? Items { get; init; }
}

/// <summary>An item of a data list.</summary>
internal sealed record PackageDataListItem
{
    /// <summary>Value stored in answers.</summary>
    public string? Key { get; init; }

    /// <summary>Label.</summary>
    public PackageText? Label { get; init; }

    /// <summary>Values of the columns.</summary>
    public Dictionary<string, string>? Values { get; init; }

    /// <summary>Offered in forms (default true).</summary>
    public bool? Active { get; init; }
}

/// <summary>A state of a record type.</summary>
internal sealed record PackageState(string? Key, PackageText? Name, RecordStateCategory? Category);

/// <summary>A custom record type.</summary>
internal sealed record PackageRecordType
{
    /// <summary>Key.</summary>
    public string? Key { get; init; }

    /// <summary>Name.</summary>
    public PackageText? Name { get; init; }

    /// <summary>Description.</summary>
    public PackageText? Description { get; init; }

    /// <summary>Number prefix.</summary>
    public string? NumberPrefix { get; init; }

    /// <summary>States in order.</summary>
    public List<PackageState>? States { get; init; }

    /// <summary>Requesters raise them in the portal.</summary>
    public bool? PortalEntry { get; init; }

    /// <summary>Priority of new records.</summary>
    public int? DefaultPriority { get; init; }

    /// <summary>Roles (built-in, the package's own or existing custom roles) given the type's read, edit and assign permissions; tenant administrators always get them.</summary>
    public List<string>? GrantRoles { get; init; }
}

/// <summary>A custom role of the app; it may hold only the permissions of the package's record types.</summary>
internal sealed record PackageRole
{
    /// <summary>Key.</summary>
    public string? Key { get; init; }

    /// <summary>Name.</summary>
    public PackageText? Name { get; init; }

    /// <summary>Needs a second factor.</summary>
    public bool? RequiresMfa { get; init; }

    /// <summary>Permissions (<c>records.&lt;key&gt;.read</c>, <c>.edit</c>, <c>.assign</c> of the package's types).</summary>
    public List<string>? Permissions { get; init; }
}

/// <summary>A form with its fields.</summary>
internal sealed record PackageForm
{
    /// <summary>Key.</summary>
    public string? Key { get; init; }

    /// <summary>Name.</summary>
    public PackageText? Name { get; init; }

    /// <summary>Description.</summary>
    public PackageText? Description { get; init; }

    /// <summary>Fields in C1's shape.</summary>
    public JsonArray? Fields { get; init; }
}

/// <summary>A record type's form for a category (none: the type's default).</summary>
internal sealed record PackageFormBinding(string? RecordType, string? Form, Guid? Category);

/// <summary>A workflow rule on one of the package's record types.</summary>
internal sealed record PackageRule
{
    /// <summary>Name.</summary>
    public PackageText? Name { get; init; }

    /// <summary>Description.</summary>
    public PackageText? Description { get; init; }

    /// <summary>Runs (default true).</summary>
    public bool? Active { get; init; }

    /// <summary>Trigger.</summary>
    public WorkflowTrigger? Trigger { get; init; }

    /// <summary>The record type's key.</summary>
    public string? RecordType { get; init; }

    /// <summary>All or any.</summary>
    public ConditionMatch? Match { get; init; }

    /// <summary>Conditions in C3's shape, ids from <see cref="AppPackage.References"/>.</summary>
    public JsonArray? Conditions { get; init; }

    /// <summary>Actions in C3's shape, ids from <see cref="AppPackage.References"/>.</summary>
    public JsonArray? Actions { get; init; }

    /// <summary>Stops later rules.</summary>
    public bool? StopProcessing { get; init; }
}

/// <summary>Kinds of thing a package refers to outside itself.</summary>
internal static class ReferenceKinds
{
    /// <summary>A category, found by its path of names.</summary>
    public const string Category = "category";

    /// <summary>A location, found by its path of names.</summary>
    public const string Location = "location";

    /// <summary>A support group, by name.</summary>
    public const string Group = "group";

    /// <summary>A department, by name.</summary>
    public const string Department = "department";

    /// <summary>A customer group, by name.</summary>
    public const string CustomerGroup = "customerGroup";

    /// <summary>A catalog service, by code.</summary>
    public const string Service = "service";

    /// <summary>A person, by email.</summary>
    public const string User = "user";

    /// <summary>A REST connector, by name.</summary>
    public const string Connector = "connector";

    /// <summary>A configuration item class, by key.</summary>
    public const string CiClass = "ciClass";

    /// <summary>Every kind.</summary>
    public static IReadOnlyList<string> All { get; } = [Category, Location, Group, Department, CustomerGroup, Service, User, Connector, CiClass];
}

/// <summary>
/// Something a package refers to: the id it had where it was exported (used inside the package)
/// and how to find it again: a name, a path of names (trees), a code, a key or an email.
/// </summary>
internal sealed record PackageReference
{
    /// <summary>Id in the exporting installation.</summary>
    public Guid Id { get; init; }

    /// <summary>Kind (<see cref="ReferenceKinds"/>).</summary>
    public string? Kind { get; init; }

    /// <summary>Name (groups, departments, customer groups, connectors in <see cref="PackageText.En"/>).</summary>
    public PackageText? Name { get; init; }

    /// <summary>Names from the top of the tree down (categories, locations).</summary>
    public List<PackageText>? Path { get; init; }

    /// <summary>Code or key (services, configuration item classes).</summary>
    public string? Code { get; init; }

    /// <summary>Email (people).</summary>
    public string? Email { get; init; }
}

/// <summary>
/// The ids a rule's conditions and actions refer to, by kind, rewritten through a map: used to
/// list references on export and to put the importing installation's ids in on import.
/// </summary>
internal static class RuleReferences
{
    /// <summary>The kind of id a condition field takes.</summary>
    public static string? KindOf(string field) => field switch
    {
        Zelvo.Workflows.Rules.RuleFields.Service => ReferenceKinds.Service,
        Zelvo.Workflows.Rules.RuleFields.Category => ReferenceKinds.Category,
        Zelvo.Workflows.Rules.RuleFields.Location => ReferenceKinds.Location,
        Zelvo.Workflows.Rules.RuleFields.Group => ReferenceKinds.Group,
        Zelvo.Workflows.Rules.RuleFields.RequesterDepartment => ReferenceKinds.Department,
        Zelvo.Workflows.Rules.RuleFields.RequesterCustomerGroup => ReferenceKinds.CustomerGroup,
        _ => null,
    };

    /// <summary>
    /// The conditions with every id put through <paramref name="map"/> (which may return the same id
    /// to only look); <see langword="null"/> from the map leaves the id as it was.
    /// </summary>
    public static List<Zelvo.Workflows.Rules.WorkflowCondition> Conditions(IEnumerable<Zelvo.Workflows.Rules.WorkflowCondition> conditions, Func<string, Guid, Guid?> map) =>
        [.. conditions.Select(c => KindOf(c.Field) is { } kind && c.Value is JsonArray values
            ? c with { Value = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v is JsonValue s && s.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? (map(kind, id) ?? id).ToString() : v?.ToString()))]) }
            : c)];

    /// <summary>The actions with every id put through <paramref name="map"/> and role keys through <paramref name="role"/>.</summary>
    public static List<Zelvo.Workflows.Rules.WorkflowAction> Actions(IEnumerable<Zelvo.Workflows.Rules.WorkflowAction> actions, Func<string, Guid, Guid?> map, Func<string, string> role)
    {
        Guid? Id(string kind, Guid? id) => id is { } value ? map(kind, value) ?? value : null;
        return [.. actions.Select(a => a switch
        {
            Zelvo.Workflows.Rules.AssignAction assign => assign with { GroupId = Id(ReferenceKinds.Group, assign.GroupId), AssigneeId = Id(ReferenceKinds.User, assign.AssigneeId) },
            Zelvo.Workflows.Rules.NotifyAction notify => notify with { Recipients = [.. notify.Recipients.Select(r => Recipient(r, map, role))] },
            Zelvo.Workflows.Rules.ConnectorAction connector => connector with { ConnectorId = Id(ReferenceKinds.Connector, connector.ConnectorId) },
            Zelvo.Workflows.Rules.ApprovalAction approval => approval with { Levels = [.. approval.Levels.Select(l => l with
            {
                Conditions = Conditions(l.Conditions, map),
                Approvers = [.. l.Approvers.Select(p => p.Kind switch
                {
                    Zelvo.Workflows.Rules.ApproverKind.User => p with { Id = Id(ReferenceKinds.User, p.Id) },
                    Zelvo.Workflows.Rules.ApproverKind.Group or Zelvo.Workflows.Rules.ApproverKind.GroupLeads => p with { Id = Id(ReferenceKinds.Group, p.Id) },
                    Zelvo.Workflows.Rules.ApproverKind.Role when p.Key is { } key => p with { Key = role(key) },
                    _ => p,
                })],
            })] },
            _ => a,
        })];
    }

    private static string Recipient(string recipient, Func<string, Guid, Guid?> map, Func<string, string> role)
    {
        foreach (var (prefix, kind) in new[] { ("user:", ReferenceKinds.User), ("group:", ReferenceKinds.Group), ("groupLeads:", ReferenceKinds.Group) })
        {
            if (recipient.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(recipient[prefix.Length..], out var id))
            {
                return prefix + (map(kind, id) ?? id);
            }
        }

        return recipient.StartsWith("role:", StringComparison.Ordinal) ? "role:" + role(recipient[5..]) : recipient;
    }
}
