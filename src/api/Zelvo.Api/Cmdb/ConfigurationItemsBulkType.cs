using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Search;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Cmdb;

/// <summary>
/// Configuration items in bulk (task G2, module 18: "import and sync from Excel"), through the E1
/// bulk framework (CSV and Excel). A row with a number changes that item; a row without one is
/// matched by the class's identification rules (one match: changed; none: a new item; several: a
/// problem). Attribute values come as a JSON object in <c>attributes</c> and as columns named
/// <c>attr.&lt;key&gt;</c>; they are merged into the item's values and checked with the class's
/// checks. Nothing changes while any row has a problem. Imported values count as set by people.
/// </summary>
internal sealed class ConfigurationItemsBulkType(
    ZelvoDbContext db, ITenantContext tenant, TenantSettingsStore settings, ISearchIndexer indexer, IAuditLog audit) : IBulkType
{
    private const string Number = "number";
    private const string Class = "class";
    private const string Name = "name";
    private const string DescriptionEn = "descriptionEn";
    private const string DescriptionAr = "descriptionAr";
    private const string Status = "status";
    private const string OwnerGroup = "ownerGroup";
    private const string Location = "location";
    private const string AttributesColumn = "attributes";
    private const string AttributePrefix = "attr.";

    public string Key => "configuration-items";

    public string ExportPermission => Permissions.CmdbRead;

    public string ImportPermission => Permissions.CmdbManage;

    public IReadOnlyList<BulkColumn> Columns { get; } =
    [
        new(Number, false, "The item's number (CI-000123) to change it; leave empty for new items, which are matched by the class's identification rules."),
        new(Class, false, "Class key, for example server or laptop; needed for new items."),
        new(Name, false, "The item's name; needed for new items."),
        new(DescriptionEn, false, "Description in English."),
        new(DescriptionAr, false, "Description in Arabic."),
        new(Status, false, "Planned, Active, Maintenance or Retired (new items: Active)."),
        new(OwnerGroup, false, "Owner group's name in English or Arabic."),
        new(Location, false, "Location path, levels by ' > ', in English or Arabic."),
        new(AttributesColumn, false, "Attribute values as a JSON object, for example {\"serialNumber\":\"5CG1234\",\"memoryGb\":16}; null clears a value. Columns named attr.<key> work too."),
    ];

    public async Task<BulkTable> ExportAsync(CancellationToken cancellationToken)
    {
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var groups = await db.SupportGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var locations = new TreeIndex<Location>(await db.Locations.AsNoTracking().ToListAsync(cancellationToken));
        var items = await db.ConfigurationItems.AsNoTracking().Where(i => i.MergedIntoId == null).OrderBy(i => i.Sequence).ToListAsync(cancellationToken);
        var rows = items.Select(item => (IReadOnlyList<string?>)[.. Columns.Select(c => c.Key switch
        {
            Number => item.Number,
            Class => classes.Find(item.ClassId)?.Key,
            Name => item.Name,
            DescriptionEn => item.Description.En,
            DescriptionAr => item.Description.Ar,
            Status => item.Status.ToString(),
            OwnerGroup => item.OwnerGroupId is { } g && groups.TryGetValue(g, out var name) ? name.En ?? name.Ar : null,
            Location => item.LocationId is { } l && locations.Find(l) is { } node ? locations.PathOf(node).En ?? locations.PathOf(node).Ar : null,
            AttributesColumn => item.Attributes == "{}" ? null : item.Attributes,
            _ => null,
        })]).ToList();
        await audit.RecordAsync(AuditCategory.Admin, "bulk.configuration-items.exported", CmdbAudit.ItemEntity, null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = items.Count }, cancellationToken);
        return new BulkTable([.. Columns.Select(c => c.Key)], rows);
    }

    public async Task<BulkImportResult> ImportAsync(BulkTable table, bool dryRun, CancellationToken cancellationToken)
    {
        var errors = new List<BulkImportError>();
        var (index, attributeColumns) = MapHeader(table.Header, errors);
        if (errors.Count > 0)
        {
            return new BulkImportResult(dryRun, false, table.Rows.Count, 0, 0, 0, errors);
        }

        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var byKey = classes.All.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var groups = await db.SupportGroups.AsNoTracking().Where(g => g.Active).ToListAsync(cancellationToken);
        var locations = new TreeIndex<Location>(await db.Locations.AsNoTracking().ToListAsync(cancellationToken));
        var plans = new List<Plan>();
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newIdentifiers = new Dictionary<string, int>(StringComparer.Ordinal);
        var unchanged = 0;
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var rowNumber = i + 2;
            var row = table.Rows[i];
            var before = errors.Count;
            void Error(string? column, string code, string message) => errors.Add(new(rowNumber, column, code, message));

            ConfigurationItem? existing = null;
            if (Cell(row, index, Number) is { } number)
            {
                if (!numbers.Add(number))
                {
                    Error(Number, "number-duplicate", "The number is in the file twice.");
                    continue;
                }

                var upper = number.ToUpperInvariant();
                existing = await db.ConfigurationItems.AsNoTracking().SingleOrDefaultAsync(c => c.Number == upper, cancellationToken);
                if (existing is null || existing.MergedIntoId is not null)
                {
                    Error(Number, "number-unknown", "No item has this number.");
                    continue;
                }
            }

            var classId = existing?.ClassId;
            if (Cell(row, index, Class) is { } classKey)
            {
                if (!byKey.TryGetValue(classKey.ToLowerInvariant(), out var ciClass))
                {
                    Error(Class, "class-unknown", $"No class has the key '{classKey}'.");
                    continue;
                }

                if (ciClass.Id != classId && !classes.Selectable(ciClass.Id))
                {
                    Error(Class, "class-inactive", "The class takes no new items.");
                    continue;
                }

                classId = ciClass.Id;
            }

            if (classId is null)
            {
                Error(Class, "class-required", "New items need a class.");
                continue;
            }

            var name = Cell(row, index, Name) ?? existing?.Name;
            if (name is null || name.Length > ConfigurationItem.NameMaxLength)
            {
                Error(Name, "name-required", "Give the item a name of at most 200 characters.");
                continue;
            }

            var status = existing?.Status ?? CiStatus.Active;
            if (Cell(row, index, Status) is { } statusText && !Enum.TryParse(statusText, true, out status))
            {
                Error(Status, "status-invalid", "Use Planned, Active, Maintenance or Retired.");
            }

            var groupId = existing?.OwnerGroupId;
            if (index.ContainsKey(OwnerGroup))
            {
                groupId = null;
                if (Cell(row, index, OwnerGroup) is { } groupName)
                {
                    groupId = groups.FirstOrDefault(g => string.Equals(g.Name.En, groupName, StringComparison.OrdinalIgnoreCase) || g.Name.Ar == groupName)?.Id;
                    if (groupId is null)
                    {
                        Error(OwnerGroup, "group-unknown", $"No active group is called '{groupName}'.");
                    }
                }
            }

            var locationId = existing?.LocationId;
            if (index.ContainsKey(Location))
            {
                locationId = null;
                if (Cell(row, index, Location) is { } path)
                {
                    var wanted = NormalisePath(path);
                    locationId = locations.Nodes.FirstOrDefault(n => locations.Selectable(n)
                        && (NormalisePath(locations.PathOf(n).En) == wanted || NormalisePath(locations.PathOf(n).Ar) == wanted))?.Id;
                    if (locationId is null)
                    {
                        Error(Location, "location-unknown", $"No active location has the path '{path}'.");
                    }
                }
            }

            var description = existing?.Description ?? new LocalizedText(null, null);
            if (index.ContainsKey(DescriptionEn) || index.ContainsKey(DescriptionAr))
            {
                description = new LocalizedText(
                    index.ContainsKey(DescriptionEn) ? Cell(row, index, DescriptionEn) : description.En,
                    index.ContainsKey(DescriptionAr) ? Cell(row, index, DescriptionAr) : description.Ar);
                if (description.En?.Length > ConfigurationItem.DescriptionMaxLength || description.Ar?.Length > ConfigurationItem.DescriptionMaxLength)
                {
                    Error(DescriptionEn, "description-too-long", "Descriptions have at most 4,000 characters.");
                }
            }

            // Values: the item's, with the file's on top (null clears one).
            var fields = classes.Attributes(classId.Value).Select(a => a.Field).ToList();
            var byField = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
            var current = existing is null ? [] : (JsonNode.Parse(existing.Attributes) as JsonObject ?? []);
            var submitted = new JsonObject();
            foreach (var (key, value) in current.Where(p => byField.ContainsKey(p.Key)))
            {
                submitted[key] = value?.DeepClone();
            }

            if (Cell(row, index, AttributesColumn) is { } json)
            {
                JsonObject? given = null;
                try
                {
                    given = JsonNode.Parse(json) as JsonObject;
                }
                catch (JsonException)
                {
                    given = null;
                }

                if (given is null)
                {
                    Error(AttributesColumn, "attributes-invalid", "Write the values as a JSON object.");
                }
                else
                {
                    foreach (var (key, value) in given)
                    {
                        if (!byField.ContainsKey(key))
                        {
                            Error(AttributesColumn, "attribute-unknown", $"The class has no attribute '{key}'.");
                        }
                        else
                        {
                            submitted[key] = value?.DeepClone();
                        }
                    }
                }
            }

            foreach (var (column, key) in attributeColumns)
            {
                if (!byField.TryGetValue(key, out var field))
                {
                    if (Cell(row, index, column) is not null)
                    {
                        Error(column, "attribute-unknown", $"The class has no attribute '{key}'.");
                    }

                    continue;
                }

                submitted[key] = Cell(row, index, column) is { } text ? Typed(field, text) : null;
            }

            foreach (var key in submitted.Where(p => p.Value is null).Select(p => p.Key).ToList())
            {
                submitted.Remove(key);
            }

            var (values, valueErrors, users, locationValues) = CmdbAttributes.CheckValues(fields, submitted);
            foreach (var error in valueErrors)
            {
                Error($"attributes.{error.Field}", error.Code, error.Message);
            }

            var userIds = users.Select(u => u.Id).Distinct().ToList();
            if (userIds.Count > 0)
            {
                var active = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => u.Id).ToListAsync(cancellationToken);
                foreach (var (field, _) in users.Where(u => !active.Contains(u.Id)))
                {
                    Error($"attributes.{field}", "user-unknown", "Choose an active user of this organisation.");
                }
            }

            foreach (var (field, id) in locationValues)
            {
                if (locations.Find(id) is not { } node || !locations.Selectable(node))
                {
                    Error($"attributes.{field}", "location-unknown", "Choose an active location.");
                }
            }

            if (errors.Count > before)
            {
                continue;
            }

            // A row without a number is matched by the identification rules, like a source's record.
            if (existing is null)
            {
                var scope = CmdbIdentification.Scope(classes, classId.Value);
                foreach (var rule in CmdbIdentification.RulesFor(classes, classId.Value))
                {
                    if (CmdbIdentification.Values(rule, values, name) is not { } match)
                    {
                        continue;
                    }

                    var signature = $"{classes.Lineage(classId.Value)[0].Id}|{string.Join('|', match.Identifiers)}|{match.Name?.ToLowerInvariant()}";
                    if (newIdentifiers.TryGetValue(signature, out var earlier))
                    {
                        Error(null, "row-same-item", $"Row {earlier} describes the same item ({string.Join('+', rule)}).");
                        break;
                    }

                    var matches = await CmdbIdentification.MatchAsync(db, scope, match.Identifiers, match.Name, null, cancellationToken);
                    if (matches.Count > 1)
                    {
                        Error(null, "item-ambiguous", $"Several items match ({string.Join('+', rule)}); give the number of the one to change.");
                        break;
                    }

                    if (matches.Count == 1)
                    {
                        existing = await db.ConfigurationItems.AsNoTracking().SingleAsync(c => c.Id == matches[0], cancellationToken);
                        if (numbers.Contains(existing.Number))
                        {
                            Error(null, "row-same-item", "Another row changes the same item.");
                        }

                        numbers.Add(existing.Number);
                        break;
                    }

                    newIdentifiers[signature] = rowNumber;
                }

                if (errors.Count > before)
                {
                    continue;
                }

                if (existing is not null)
                {
                    // Matched: the file's values on top of the item's own.
                    var merged = JsonNode.Parse(existing.Attributes) as JsonObject ?? [];
                    foreach (var (key, value) in values)
                    {
                        merged[key] = value?.DeepClone();
                    }

                    var recheck = CmdbAttributes.CheckValues(classes.Attributes(existing.ClassId).Select(a => a.Field).ToList(), merged);
                    values = recheck.Values;
                    classId = index.ContainsKey(Class) ? classId : existing.ClassId;
                    groupId = index.ContainsKey(OwnerGroup) ? groupId : existing.OwnerGroupId;
                    locationId = index.ContainsKey(Location) ? locationId : existing.LocationId;
                    status = index.ContainsKey(Status) ? status : existing.Status;
                    description = index.ContainsKey(DescriptionEn) || index.ContainsKey(DescriptionAr) ? description : existing.Description;
                }
            }

            var plan = new Plan(rowNumber, existing, classId.Value, name, description, status, groupId, locationId, values);
            if (existing is not null && !plan.Changes(existing))
            {
                unchanged++;
                continue;
            }

            plans.Add(plan);
        }

        var created = plans.Count(p => p.Existing is null);
        var updated = plans.Count - created;
        if (errors.Count > 0 || dryRun)
        {
            return new BulkImportResult(dryRun, false, table.Rows.Count, created, updated, unchanged, errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var prefix = await settings.GetAsync(CmdbSettings.NumberPrefix, cancellationToken);
        var roles = await CmdbSearch.ReaderRolesAsync(db, cancellationToken);
        foreach (var plan in plans)
        {
            ConfigurationItem item;
            JsonObject before;
            var nameChanged = true;
            if (plan.Existing is null)
            {
                var sequence = await CmdbNumbers.NextAsync(db, tenant.TenantId!.Value, cancellationToken);
                item = new ConfigurationItem { Number = CmdbNumbers.Format(prefix, sequence), Sequence = sequence, ClassId = plan.ClassId, Name = plan.Name };
                db.ConfigurationItems.Add(item);
                before = [];
            }
            else
            {
                item = await db.ConfigurationItems.SingleAsync(c => c.Id == plan.Existing.Id, cancellationToken);
                before = JsonNode.Parse(item.Attributes) as JsonObject ?? [];
                nameChanged = item.Name != plan.Name;
                item.UpdatedAt = DateTimeOffset.UtcNow;
            }

            item.ClassId = plan.ClassId;
            item.Name = plan.Name;
            item.Description = plan.Description;
            item.Status = plan.Status;
            item.OwnerGroupId = plan.GroupId;
            item.LocationId = plan.LocationId;
            item.Attributes = plan.Values.ToJsonString();
            item.AttributeSources = CmdbPrecedence.MarkManual(item.AttributeSources, before, plan.Values, nameChanged);
            indexer.Upsert(CmdbSearch.Document(item, classes, roles));
        }

        audit.Add(AuditCategory.Admin, "bulk.configuration-items.imported", CmdbAudit.ItemEntity, null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["created"] = created, ["updated"] = updated, ["unchanged"] = unchanged });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BulkImportResult(false, true, table.Rows.Count, created, updated, unchanged, []);
    }

    private (Dictionary<string, int> Index, List<(string Column, string Key)> Attributes) MapHeader(IReadOnlyList<string> header, List<BulkImportError> errors)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var attributes = new List<(string, string)>();
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            string key;
            if (name.StartsWith(AttributePrefix, StringComparison.OrdinalIgnoreCase) && name.Length > AttributePrefix.Length)
            {
                key = AttributePrefix + name[AttributePrefix.Length..];
                attributes.Add((key, name[AttributePrefix.Length..]));
            }
            else if (Columns.FirstOrDefault(c => string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase)) is { } column)
            {
                key = column.Key;
            }
            else
            {
                errors.Add(new(1, name, "column-unknown", $"Unknown column '{name}'."));
                continue;
            }

            if (!index.TryAdd(key, i))
            {
                errors.Add(new(1, key, "column-duplicate", $"The column '{key}' is there twice."));
            }
        }

        if (!index.ContainsKey(Number) && !index.ContainsKey(Class))
        {
            errors.Add(new(1, Class, "column-required", "The file needs a number or class column."));
        }

        return (index, attributes);
    }

    /// <summary>A cell's text as the attribute's type: numbers for number fields, true or false for checkboxes.</summary>
    private static JsonNode Typed(FormField field, string text) => field.Type switch
    {
        FieldType.Number when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
        FieldType.Checkbox when bool.TryParse(text, out var flag) => JsonValue.Create(flag),
        FieldType.Dropdown when field.Multiple => new JsonArray([.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => (JsonNode)JsonValue.Create(v))]),
        _ => JsonValue.Create(text),
    };

    private static string? Cell(IReadOnlyList<string?> row, Dictionary<string, int> index, string column)
    {
        if (!index.TryGetValue(column, out var i) || i >= row.Count || row[i] is not { } raw)
        {
            return null;
        }

        var value = raw.Trim();
        value = value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;
        return value.Length == 0 ? null : value;
    }

    private static string? NormalisePath(string? path) =>
        path is null ? null : string.Join('>', path.Split(['>', '›'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private sealed record Plan(
        int Row, ConfigurationItem? Existing, Guid ClassId, string Name, LocalizedText Description, CiStatus Status, Guid? GroupId, Guid? LocationId, JsonObject Values)
    {
        public bool Changes(ConfigurationItem item) =>
            item.ClassId != ClassId || item.Name != Name || item.Description.En != Description.En || item.Description.Ar != Description.Ar || item.Status != Status
            || item.OwnerGroupId != GroupId || item.LocationId != LocationId || !JsonNode.DeepEquals(JsonNode.Parse(item.Attributes), Values);
    }
}
