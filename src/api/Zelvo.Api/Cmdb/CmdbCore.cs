using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Knowledge;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Localization;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Cmdb;

/// <summary>Tenant settings of the CMDB (module 18), changed in Settings → CMDB.</summary>
internal static class CmdbSettings
{
    /// <summary>Prefix of configuration item numbers, for example <c>CI</c> in <c>CI-000123</c>.</summary>
    public static SettingDefinition<string> NumberPrefix { get; } = new("cmdb.numberPrefix", "CI", Permissions.CmdbManage, KnowledgeSettings.IsPrefix);

    public static IReadOnlyList<SettingDefinition> All { get; } = [NumberPrefix];
}

/// <summary>Who may see what of the CMDB.</summary>
internal static class CmdbAccess
{
    /// <summary>Sees every item, relationship and map.</summary>
    public static bool IsReader(CurrentUser user) => user.Has(Permissions.CmdbRead) || user.Has(Permissions.CmdbManage);
}

/// <summary>
/// The tenant's classes in memory (a tenant has tens or hundreds): ancestors, descendants, paths
/// and the attributes an item of a class has (its own and every class above it).
/// </summary>
internal sealed class CiClassIndex
{
    private readonly Dictionary<Guid, CiClass> _byId;
    private readonly ILookup<Guid?, CiClass> _children;

    public CiClassIndex(IEnumerable<CiClass> classes)
    {
        var list = classes.ToList();
        _byId = list.ToDictionary(c => c.Id);
        _children = list.ToLookup(c => c.ParentId);
    }

    public static async Task<CiClassIndex> LoadAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        new(await db.CiClasses.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));

    public IEnumerable<CiClass> All => _byId.Values;

    public CiClass? Find(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>The class and those above it, top first.</summary>
    public List<CiClass> Lineage(Guid id)
    {
        var lineage = new List<CiClass>();
        for (var current = Find(id); current is not null && lineage.Count <= CiClass.MaxDepth; current = current.ParentId is { } p ? Find(p) : null)
        {
            lineage.Insert(0, current);
        }

        return lineage;
    }

    /// <summary>The class and every class below it.</summary>
    public HashSet<Guid> WithDescendants(Guid id)
    {
        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>([id]);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (result.Add(next))
            {
                foreach (var child in _children[next])
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return result;
    }

    /// <summary>Classes in tree order (parents before children, siblings by position).</summary>
    public IEnumerable<CiClass> Ordered(Guid? parentId = null)
    {
        foreach (var node in _children[parentId].OrderBy(c => c.Family).ThenBy(c => c.Position).ThenBy(c => c.Name.En ?? c.Name.Ar, StringComparer.CurrentCulture))
        {
            yield return node;
            foreach (var below in Ordered(node.Id))
            {
                yield return below;
            }
        }
    }

    /// <summary><c>Hardware › Server</c> in each language.</summary>
    public LocalizedText Path(Guid id)
    {
        var lineage = Lineage(id);
        return new LocalizedText(
            string.Join(" › ", lineage.Select(c => c.Name.En ?? c.Name.Ar)),
            string.Join(" › ", lineage.Select(c => c.Name.Ar ?? c.Name.En)));
    }

    /// <summary>Whether items can be added to the class: it and every class above it are active.</summary>
    public bool Selectable(Guid id) => Lineage(id) is { Count: > 0 } lineage && lineage.All(c => c.Active) && lineage[^1].Id == id;

    /// <summary>The attributes of an item of the class, those of the top class first, each with the class it comes from.</summary>
    public List<(FormField Field, Guid ClassId)> Attributes(Guid id) =>
        [.. Lineage(id).SelectMany(c => CmdbAttributes.Read(c.Attributes).Select(f => (f, c.Id)))];

    /// <summary>Requesters may pick items of the class.</summary>
    public bool RequesterVisible(Guid id) => Find(id)?.RequesterVisible == true;
}

/// <summary>
/// Attributes of classes (task G1): C1 form fields, limited to what describes an item: text,
/// number, date, dropdown, checkbox, radio, user and location, with C1's checks, without rules,
/// files, signatures, tables, profile defaults or personal data.
/// </summary>
internal static class CmdbAttributes
{
    /// <summary>Most attributes of one class.</summary>
    public const int MaxPerClass = 50;

    public static readonly FieldType[] AllowedTypes =
        [FieldType.Text, FieldType.Number, FieldType.Date, FieldType.Dropdown, FieldType.Checkbox, FieldType.Radio, FieldType.User, FieldType.Location];

    public static List<FormField> Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FormField>>(json, FormJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Write(IReadOnlyList<FormField> fields) => FormJson.Write(fields);

    /// <summary>Problems of a class's own attributes; <paramref name="inheritedKeys"/> and <paramref name="belowKeys"/> may not be reused.</summary>
    public static List<FormProblem> Check(IReadOnlyList<FormField> fields, IReadOnlySet<string> inheritedKeys, IReadOnlySet<string> belowKeys)
    {
        var problems = fields.Count > MaxPerClass
            ? [new FormProblem(string.Empty, "too-many-attributes", $"A class has at most {MaxPerClass} attributes of its own.")]
            : FormDefinitionCheck.CheckFieldSet(fields);
        foreach (var field in fields)
        {
            var key = field.Key ?? string.Empty;
            if (!AllowedTypes.Contains(field.Type))
            {
                problems.Add(new(key, "attribute-type", "Attributes are text, number, date, dropdown, checkbox, radio, user or location fields."));
            }

            if ((field.Rules?.Count ?? 0) > 0 || field.ProfileDefault is not null || field.ReadOnly)
            {
                problems.Add(new(key, "attribute-not-allowed", "Attributes have no rules, profile defaults or read-only setting."));
            }

            if (field.PersonalData || field.Validation?.Format is FieldFormat.EmiratesId or FieldFormat.Iban)
            {
                problems.Add(new(key, "attribute-personal-data", "Configuration items do not hold personal data; Emirates ID and IBAN formats are not available."));
            }

            if (inheritedKeys.Contains(key))
            {
                problems.Add(new(key, "attribute-inherited", "A class above already has an attribute with this key."));
            }
            else if (belowKeys.Contains(key))
            {
                problems.Add(new(key, "attribute-below", "A class below already has an attribute with this key."));
            }
        }

        return problems;
    }

    /// <summary>
    /// Checks attribute values of an item of a class with C1's answer checks; returns the clean
    /// values (by key, those not answered left out) or the errors. Values of keys the class does
    /// not have are refused.
    /// </summary>
    public static (JsonObject Values, List<FormAnswerError> Errors, List<(string Field, Guid Id)> Users, List<(string Field, Guid Id)> Locations) CheckValues(
        IReadOnlyList<FormField> fields, JsonObject submitted)
    {
        var defaults = new JsonObject();
        foreach (var field in fields.Where(f => f.DefaultValue is not null))
        {
            defaults[field.Key] = field.DefaultValue!.DeepClone();
        }

        var result = FormAnswers.Check(fields, submitted, defaults);
        var values = new JsonObject();
        foreach (var field in fields)
        {
            var value = result.Answers[field.Key] ?? result.PersonalAnswers[field.Key];
            if (value is not null)
            {
                values[field.Key] = value.DeepClone();
            }
        }

        return (values, result.Errors, result.Users, result.Locations);
    }
}

/// <summary>The four built-in relationship types (module 18: runs on, depends on, connects to, used by).</summary>
internal static class CmdbRelationshipDefaults
{
    public const string RunsOn = "runs-on";
    public const string DependsOn = "depends-on";
    public const string ConnectsTo = "connects-to";
    public const string UsedBy = "used-by";

    public static IReadOnlyList<CiRelationshipType> Create() =>
    [
        new() { Key = RunsOn, OutgoingName = new("Runs on", "يعمل على"), IncomingName = new("Hosts", "يستضيف"), Impact = ImpactDirection.ToSource, BuiltIn = true },
        new() { Key = DependsOn, OutgoingName = new("Depends on", "يعتمد على"), IncomingName = new("Needed by", "مطلوب لـ"), Impact = ImpactDirection.ToSource, BuiltIn = true },
        new() { Key = ConnectsTo, OutgoingName = new("Connects to", "يتصل بـ"), IncomingName = new("Connected from", "متصل من"), Impact = ImpactDirection.Both, BuiltIn = true },
        new()
        {
            Key = UsedBy, OutgoingName = new("Used by", "يستخدمه"), IncomingName = new("Uses", "يستخدم"),
            TargetKind = RelationshipTargetKind.Consumer, Impact = ImpactDirection.ToTarget, BuiltIn = true,
        },
    ];

    /// <summary>
    /// Adds the built-in types the tenant does not have yet (with the change being saved; the
    /// unique key makes a concurrent first use fail once, and the caller's next try finds them).
    /// </summary>
    public static async Task EnsureAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var keys = await db.CiRelationshipTypes.Where(t => t.BuiltIn).Select(t => t.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = Create().Where(t => !keys.Contains(t.Key)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        db.CiRelationshipTypes.AddRange(missing);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another request added them at the same moment.
            foreach (var type in missing)
            {
                db.Entry(type).State = EntityState.Detached;
            }
        }
    }
}

/// <summary>Configuration item numbers: <c>CI-000123</c>, never reused.</summary>
internal static class CmdbNumbers
{
    /// <summary>Takes the next number for the current tenant inside the caller's transaction (row lock; a rollback gives it back).</summary>
    public static async Task<long> NextAsync(ZelvoDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var values = await db.Database.SqlQuery<long>($"""
            INSERT INTO ci_number_sequences (tenant_id, last_value)
            VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_value = ci_number_sequences.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return values.Single();
    }

    public static string Format(string prefix, long sequence) => TicketNumbers.Format(prefix, sequence);
}

/// <summary>Configuration items in the search index (A4): found by holders of <c>cmdb.read</c>.</summary>
internal static partial class CmdbSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "ci";

    public static async Task<IReadOnlyList<string>> ReaderRolesAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Roles.Where(r => r.Permissions.Any(p => p.Permission == Permissions.CmdbRead || p.Permission == Permissions.CmdbManage))
            .Select(r => r.Key).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);

    public static SearchDocument Document(ConfigurationItem item, CiClassIndex classes, IReadOnlyList<string> roles)
    {
        var path = classes.Path(item.ClassId);
        var values = new List<string>();
        foreach (var (_, value) in JsonNode.Parse(item.Attributes)?.AsObject() ?? [])
        {
            switch (value)
            {
                case JsonValue v when v.GetValueKind() == JsonValueKind.String && !Guid.TryParse(v.GetValue<string>(), out _):
                    values.Add(v.GetValue<string>());
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                    values.Add(v.ToJsonString());
                    break;
            }
        }

        var title = $"{item.Number} {item.Name}";
        return new SearchDocument
        {
            Type = Type,
            Id = item.Id.ToString(),
            Title = new LocalizedText(title, title),
            Body = item.Description,
            Text = values,
            Keywords = [item.Number, item.Name, .. values.Where(v => v.Length <= 100 && !WhitespacePattern().IsMatch(v))],
            Readers = [.. roles.Select(SearchReaders.Role)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = item.Number,
                ["status"] = item.Status.ToString(),
                ["classId"] = item.ClassId.ToString(),
                ["class"] = path.En ?? string.Empty,
                ["classAr"] = path.Ar ?? string.Empty,
            },
            Url = $"/cmdb/{item.Id}",
            UpdatedAt = item.UpdatedAt,
        };
    }

    [GeneratedRegex(@"\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex WhitespacePattern();
}

/// <summary>Every configuration item of the tenant, for rebuilding the search index.</summary>
internal sealed class CmdbSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => CmdbSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var roles = await CmdbSearch.ReaderRolesAsync(db, cancellationToken).ConfigureAwait(false);
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var items = db.ConfigurationItems.AsNoTracking().OrderBy(i => i.Id).AsAsyncEnumerable();
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return CmdbSearch.Document(item, classes, roles);
        }
    }
}

/// <summary>Classes and relationship types that miss a language (LANG-5), for Settings → Missing translations.</summary>
internal sealed class CmdbMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        foreach (var ciClass in (await db.CiClasses.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!ciClass.Name.IsComplete)
            {
                missing.Add(new MissingTranslation("ci_classes", ciClass.Key, "name", ciClass.Name.MissingLanguages));
            }

            foreach (var field in CmdbAttributes.Read(ciClass.Attributes).Where(f => f.Label is { HasAnyValue: true, IsComplete: false }))
            {
                missing.Add(new MissingTranslation("ci_classes", ciClass.Key, $"attributes.{field.Key}", field.Label.MissingLanguages));
            }
        }

        foreach (var type in (await db.CiRelationshipTypes.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!type.OutgoingName.IsComplete)
            {
                missing.Add(new MissingTranslation("ci_relationship_types", type.Key, "outgoingName", type.OutgoingName.MissingLanguages));
            }

            if (!type.IncomingName.IsComplete)
            {
                missing.Add(new MissingTranslation("ci_relationship_types", type.Key, "incomingName", type.IncomingName.MissingLanguages));
            }
        }

        return missing;
    }
}
