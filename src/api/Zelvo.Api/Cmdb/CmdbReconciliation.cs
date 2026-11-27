using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Connectors;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Tenancy;
using Zelvo.Search;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Cmdb;

/// <summary>Reconciliation settings of the tenant (task G2).</summary>
internal static class CmdbReconciliationSettings
{
    /// <summary>
    /// Precedence of values people set (on screen, by import or through the API), 1 (most trusted)
    /// to 100. Sources default to 50, so people's corrections stay unless an administrator ranks a
    /// source above them.
    /// </summary>
    public static SettingDefinition<int> ManualPriority { get; } = new("cmdb.manualPriority", 10, Permissions.CmdbManage, p => p is >= 1 and <= CiDiscoverySource.LowestPriority);
}

/// <summary>
/// Identification rules (task G2): which attribute values together identify an item of a class,
/// tried in order. A class without rules of its own takes those of the nearest class above, and at
/// the top Zelvo's defaults.
/// </summary>
internal static class CmdbIdentification
{
    /// <summary>The item's name as an identifying key.</summary>
    public const string NameKey = "name";

    /// <summary>Most rules per class.</summary>
    public const int MaxRules = 10;

    /// <summary>Most keys per rule.</summary>
    public const int MaxKeys = 3;

    /// <summary>Zelvo's rules: serial number, cloud resource id, MAC address, host name, IMEI.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Defaults { get; } = [["serialNumber"], ["resourceId"], ["macAddress"], ["hostName"], ["imei"]];

    public static List<List<string>>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<List<string>>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Write(IReadOnlyList<IReadOnlyList<string>>? rules) => rules is null or { Count: 0 } ? null : JsonSerializer.Serialize(rules);

    /// <summary>The rules that apply to items of <paramref name="classId"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> RulesFor(CiClassIndex classes, Guid classId)
    {
        foreach (var ciClass in Enumerable.Reverse(classes.Lineage(classId)))
        {
            if (Read(ciClass.IdentificationRules) is { Count: > 0 } own)
            {
                return own;
            }
        }

        return Defaults;
    }

    /// <summary>Problems of rules set on a class: each rule 1–3 keys of the class's attributes (or <c>name</c>), at most 10 rules.</summary>
    public static string? Problem(IReadOnlyList<IReadOnlyList<string>> rules, IReadOnlySet<string> keys)
    {
        if (rules.Count > MaxRules)
        {
            return "too-many-rules";
        }

        foreach (var rule in rules)
        {
            if (rule.Count is 0 or > MaxKeys || rule.Distinct(StringComparer.Ordinal).Count() != rule.Count)
            {
                return "rule-invalid";
            }

            if (rule.Any(k => k != NameKey && !keys.Contains(k)))
            {
                return "rule-key-unknown";
            }
        }

        return null;
    }

    /// <summary>A value as the database keeps it in <c>identifiers</c>: <c>key=value</c> in lower case; MAC addresses normalised first.</summary>
    public static string? Identifier(string key, JsonNode? value)
    {
        var text = value switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Trim(),
            JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
            _ => null,
        };
        if (string.IsNullOrEmpty(text) || text.Length > 300)
        {
            return null;
        }

        return $"{key}={text}".ToLowerInvariant();
    }

    /// <summary>The value of each key of <paramref name="rule"/> in <paramref name="attributes"/>, or <see langword="null"/> when one is missing.</summary>
    public static (string[] Identifiers, string? Name)? Values(IReadOnlyList<string> rule, JsonObject attributes, string name)
    {
        var identifiers = new List<string>();
        string? byName = null;
        foreach (var key in rule)
        {
            if (key == NameKey)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                byName = name.Trim();
                continue;
            }

            if (Identifier(key, attributes[key]) is not { } identifier)
            {
                return null;
            }

            identifiers.Add(identifier);
        }

        return ([.. identifiers], byName);
    }

    /// <summary>
    /// Items (not retired, not merged) of <paramref name="classIds"/> that have every value, except
    /// <paramref name="except"/>; at most 10. One query through the GIN index on <c>identifiers</c>.
    /// </summary>
    public static async Task<List<Guid>> MatchAsync(
        ZelvoDbContext db, IReadOnlyCollection<Guid> classIds, string[] identifiers, string? name, Guid? except, CancellationToken cancellationToken)
    {
        var classes = classIds.ToArray();
        var none = except ?? Guid.Empty;
        if (identifiers.Length == 0 && name is null)
        {
            return [];
        }

        if (name is null)
        {
            return await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM configuration_items
                WHERE identifiers @> {identifiers} AND class_id = ANY({classes}) AND status <> 'Retired' AND merged_into_id IS NULL AND id <> {none}
                ORDER BY sequence LIMIT 10
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        return await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM configuration_items
            WHERE identifiers @> {identifiers} AND lower(name) = lower({name}) AND class_id = ANY({classes}) AND status <> 'Retired' AND merged_into_id IS NULL AND id <> {none}
            ORDER BY sequence LIMIT 10
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The classes an item of <paramref name="classId"/> is matched against: every class under the same top class.</summary>
    public static HashSet<Guid> Scope(CiClassIndex classes, Guid classId) =>
        classes.Lineage(classId) is { Count: > 0 } lineage ? classes.WithDescendants(lineage[0].Id) : [classId];
}

/// <summary>
/// Attribute precedence (task G2): who may overwrite a value. Each value remembers who set it
/// (<see cref="ConfigurationItem.AttributeSources"/>: a source id or <c>manual</c>); a source
/// overwrites it only when its priority for the key (per attribute, else its own) is the same or
/// better (lower) than the last setter's. People's values have the tenant's manual priority.
/// </summary>
internal sealed class CmdbPrecedence(int manualPriority, IReadOnlyDictionary<Guid, (int Priority, IReadOnlyDictionary<string, int> PerKey)> sources)
{
    /// <summary>Who set a value by hand.</summary>
    public const string Manual = "manual";

    public static async Task<CmdbPrecedence> LoadAsync(ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var manual = await settings.GetAsync(CmdbReconciliationSettings.ManualPriority, cancellationToken).ConfigureAwait(false);
        var sources = await db.CiDiscoverySources.AsNoTracking().Select(s => new { s.Id, s.Priority, s.AttributePriorities }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CmdbPrecedence(manual, sources.ToDictionary(s => s.Id, s => (s.Priority, ReadPriorities(s.AttributePriorities))));
    }

    public static IReadOnlyDictionary<string, int> ReadPriorities(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json ?? "{}") ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }

    /// <summary>The priority of <paramref name="setter"/> for <paramref name="key"/>; unknown setters (a removed source, nobody) rank last.</summary>
    public int Of(string? setter, string key) =>
        setter == Manual ? manualPriority
        : Guid.TryParse(setter, out var id) && sources.TryGetValue(id, out var source) ? source.PerKey.TryGetValue(key, out var own) ? own : source.Priority
        : int.MaxValue;

    /// <summary>Whether <paramref name="source"/> may set <paramref name="key"/>, last set by <paramref name="setter"/>.</summary>
    public bool MayOverwrite(Guid source, string? setter, string key) => setter == source.ToString() || Of(source.ToString(), key) <= Of(setter, key);

    public static JsonObject ReadSources(string json) => (JsonNode.Parse(json) as JsonObject) ?? [];

    /// <summary>Marks the values (and name) a person changed as theirs.</summary>
    public static string MarkManual(string sourcesJson, JsonObject before, JsonObject after, bool nameChanged)
    {
        var sources = ReadSources(sourcesJson);
        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key)).ToList())
        {
            if (!JsonNode.DeepEquals(before[key], after[key]))
            {
                if (after[key] is null)
                {
                    sources.Remove(key);
                }
                else
                {
                    sources[key] = Manual;
                }
            }
        }

        if (nameChanged)
        {
            sources[CmdbIdentification.NameKey] = Manual;
        }

        return sources.ToJsonString();
    }
}

/// <summary>Counts of one reconciliation run (a sync, a push or an import).</summary>
internal sealed class ReconcileStats
{
    public int Received { get; set; }

    public int Created { get; set; }

    public int Updated { get; set; }

    public int Unchanged { get; set; }

    public int Linked { get; set; }

    public int Review { get; set; }

    public int Duplicates { get; set; }

    public int Missing { get; set; }

    public int UnknownClass { get; set; }

    public int Skipped { get; set; }

    public int ValuesRefused { get; set; }

    public int Software { get; set; }

    public int Relationships { get; set; }

    public string Write() => JsonSerializer.Serialize(this, CmdbJson.Options);

    public Dictionary<string, object?> ToAudit() => JsonSerializer.Deserialize<Dictionary<string, object?>>(Write(), CmdbJson.Options)!;

    public static ReconcileStats? Read(string? json)
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<ReconcileStats>(json, CmdbJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class CmdbJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Reconciliation (task G2): ties what a source reports to configuration items. A record is found
/// by its source's id, else by the identification rules among items under the same top class: one
/// match links it, none makes a new item, several put it in the review queue. Values follow the
/// attribute precedence; values that fail the class's checks are left out and counted. Linked or
/// updated items whose identifying values another item shares are raised as possible duplicates.
/// A complete run marks the records it no longer saw, and items no source reports any more; nothing
/// is ever deleted.
/// </summary>
internal sealed class CmdbReconciler(ZelvoDbContext db, ITenantContext tenant, TenantSettingsStore settings, ISearchIndexer indexer, TimeProvider time)
{
    /// <summary>Records saved per transaction.</summary>
    private const int BatchSize = 200;

    public async Task<ReconcileStats> ApplyAsync(CiDiscoverySource source, IReadOnlyList<DiscoveredRecord> records, bool complete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(records);
        var started = time.GetUtcNow();
        var stats = new ReconcileStats { Received = records.Count };
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var byKey = classes.All.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var precedence = await CmdbPrecedence.LoadAsync(db, settings, cancellationToken).ConfigureAwait(false);
        var prefix = await settings.GetAsync(CmdbSettings.NumberPrefix, cancellationToken).ConfigureAwait(false);
        var roles = await CmdbSearch.ReaderRolesAsync(db, cancellationToken).ConfigureAwait(false);
        // Where each external id ended up, for the relationships the source reports.
        var placed = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var links = new List<(Guid From, string Type, string Target)>();

        // The last record of an id wins when a source sends it twice.
        var unique = records.Where(r => !string.IsNullOrWhiteSpace(r.ExternalId)).GroupBy(r => r.ExternalId.Trim(), StringComparer.Ordinal).Select(g => g.Last()).ToList();
        stats.Skipped += records.Count - unique.Count;
        foreach (var batch in unique.Chunk(BatchSize))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var ids = batch.Select(r => r.ExternalId.Trim()).ToList();
            var known = await db.CiSourceRecords.Where(r => r.SourceId == source.Id && ids.Contains(r.ExternalId)).ToDictionaryAsync(r => r.ExternalId, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);
            foreach (var record in batch)
            {
                var externalId = record.ExternalId.Trim();
                var name = record.Name?.Trim() is { Length: > 0 } n ? (n.Length <= ConfigurationItem.NameMaxLength ? n : n[..ConfigurationItem.NameMaxLength]) : null;
                if (externalId.Length > CiSourceRecord.ExternalIdMaxLength || name is null)
                {
                    stats.Skipped++;
                    continue;
                }

                if (!byKey.TryGetValue(record.ClassKey, out var ciClass) || !classes.Selectable(ciClass.Id))
                {
                    stats.UnknownClass++;
                    continue;
                }

                var item = await PlaceAsync(source, record, externalId, name, ciClass, known, classes, precedence, prefix, roles, stats, started, cancellationToken).ConfigureAwait(false);
                if (item is null)
                {
                    continue;
                }

                // Saved at once: the next record of the batch may match this one by its values.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                placed[externalId] = item.Id;
                foreach (var link in record.Links ?? [])
                {
                    links.Add((item.Id, link.Type, link.Target));
                }

                await SoftwareAsync(source, item.Id, record.Software, stats, started, cancellationToken).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Release();
        }

        if (links.Count > 0)
        {
            await RelationshipsAsync(source, links, placed, stats, cancellationToken).ConfigureAwait(false);
        }

        if (complete)
        {
            stats.Missing = await MarkMissingAsync(source.Id, started, cancellationToken).ConfigureAwait(false);
        }

        return stats;
    }

    /// <summary>
    /// Settles a record that waited for review: ties it to <paramref name="item"/>, or to a new item
    /// made from it when <paramref name="item"/> is null, and applies its values with the usual
    /// precedence. Saved with the caller's next save.
    /// </summary>
    public async Task<ConfigurationItem> ResolveAsync(CiSourceRecord record, ConfigurationItem? item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var now = time.GetUtcNow();
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var precedence = await CmdbPrecedence.LoadAsync(db, settings, cancellationToken).ConfigureAwait(false);
        var created = item is null;
        if (item is null)
        {
            var prefix = await settings.GetAsync(CmdbSettings.NumberPrefix, cancellationToken).ConfigureAwait(false);
            var sequence = await CmdbNumbers.NextAsync(db, tenant.TenantId!.Value, cancellationToken).ConfigureAwait(false);
            item = new ConfigurationItem
            {
                TenantId = tenant.TenantId!.Value, Number = CmdbNumbers.Format(prefix, sequence), Sequence = sequence, ClassId = record.ClassId, Name = record.Name,
                Status = CiStatus.Active, CreatedAt = now, UpdatedAt = now,
            };
            db.ConfigurationItems.Add(item);
        }

        record.ConfigurationItemId = item.Id;
        record.State = SourceRecordState.Linked;
        var stats = new ReconcileStats();
        if (Apply(record.SourceId, item, record.Name, (JsonNode.Parse(record.Attributes) as JsonObject) ?? [], classes, precedence, stats, created) && !created)
        {
            item.UpdatedAt = now;
        }

        item.MissingSince = null;
        indexer.Upsert(CmdbSearch.Document(item, classes, await CmdbSearch.ReaderRolesAsync(db, cancellationToken).ConfigureAwait(false)));
        return item;
    }

    /// <summary>
    /// Marks the records of <paramref name="sourceId"/> not reported since <paramref name="since"/> as
    /// missing, and the items none of whose records are still reported; returns the records newly
    /// marked. Items are never deleted; a record reported again clears both marks.
    /// </summary>
    public async Task<int> MarkMissingAsync(Guid sourceId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var gone = await db.CiSourceRecords.Where(r => r.SourceId == sourceId && r.LastSeenAt < since && r.MissingSince == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in gone)
        {
            record.MissingSince = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var itemIds = gone.Where(r => r.ConfigurationItemId is not null).Select(r => r.ConfigurationItemId!.Value).Distinct().ToList();
        foreach (var chunk in itemIds.Chunk(1000))
        {
            var stillReported = await db.CiSourceRecords.Where(r => r.ConfigurationItemId != null && chunk.Contains(r.ConfigurationItemId.Value) && r.MissingSince == null)
                .Select(r => r.ConfigurationItemId!.Value).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
            var items = await db.ConfigurationItems.Where(i => chunk.Contains(i.Id) && !stillReported.Contains(i.Id) && i.MissingSince == null).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in items)
            {
                item.MissingSince = now;
                item.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        Release();
        return gone.Count;
    }

    private async Task<ConfigurationItem?> PlaceAsync(
        CiDiscoverySource source, DiscoveredRecord record, string externalId, string name, CiClass ciClass, Dictionary<string, CiSourceRecord> known, CiClassIndex classes,
        CmdbPrecedence precedence, string prefix, IReadOnlyList<string> roles, ReconcileStats stats, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var attributes = record.Attributes.DeepClone().AsObject();
        if (!known.TryGetValue(externalId, out var sourceRecord))
        {
            sourceRecord = new CiSourceRecord
            {
                TenantId = tenant.TenantId!.Value, SourceId = source.Id, ExternalId = externalId, Name = name, ClassId = ciClass.Id, FirstSeenAt = now, State = SourceRecordState.Review,
            };
            db.CiSourceRecords.Add(sourceRecord);
            known[externalId] = sourceRecord;
        }

        sourceRecord.Name = name;
        sourceRecord.ClassId = ciClass.Id;
        sourceRecord.Attributes = attributes.ToJsonString();
        sourceRecord.PrimaryUser = record.PrimaryUser is { Length: > 0 and <= CiSourceRecord.PrimaryUserMaxLength } user ? user.Trim() : null;
        sourceRecord.LastSeenAt = now;
        sourceRecord.MissingSince = null;

        ConfigurationItem? item = null;
        if (sourceRecord.ConfigurationItemId is { } linkedId)
        {
            item = await FollowMergesAsync(linkedId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                sourceRecord.ConfigurationItemId = null;
                sourceRecord.State = SourceRecordState.Review;
            }
            else
            {
                sourceRecord.ConfigurationItemId = item.Id;
            }
        }
        else if (db.Entry(sourceRecord).State != EntityState.Added && await db.CiReviewItems.AnyAsync(r => r.SourceRecordId == sourceRecord.Id && r.State == CiReviewState.Open, cancellationToken)
                     .ConfigureAwait(false))
        {
            // Still waiting for a person to say which item it is.
            stats.Review++;
            return null;
        }

        var scope = CmdbIdentification.Scope(classes, ciClass.Id);
        if (item is null)
        {
            List<Guid> matches = [];
            string? matchedOn = null;
            string? matchedValue = null;
            foreach (var rule in CmdbIdentification.RulesFor(classes, ciClass.Id))
            {
                if (CmdbIdentification.Values(rule, attributes, name) is not { } values)
                {
                    continue;
                }

                matches = await CmdbIdentification.MatchAsync(db, scope, values.Identifiers, values.Name, null, cancellationToken).ConfigureAwait(false);
                if (matches.Count > 0)
                {
                    matchedOn = string.Join('+', rule);
                    matchedValue = string.Join(" + ", values.Identifiers.Select(i => i[(i.IndexOf('=', StringComparison.Ordinal) + 1)..]).Concat(values.Name is null ? [] : [values.Name]));
                    break;
                }
            }

            if (matches.Count > 1)
            {
                sourceRecord.State = SourceRecordState.Review;
                sourceRecord.ConfigurationItemId = null;
                db.CiReviewItems.Add(new CiReviewItem
                {
                    TenantId = tenant.TenantId!.Value, Kind = CiReviewKind.Ambiguous, SourceRecordId = sourceRecord.Id, Candidates = JsonSerializer.Serialize(matches),
                    MatchedOn = Cut(matchedOn!, CiReviewItem.MatchedOnMaxLength), MatchedValue = Cut(matchedValue!, CiReviewItem.MatchedValueMaxLength), CreatedAt = now,
                });
                stats.Review++;
                return null;
            }

            if (matches.Count == 1)
            {
                item = await db.ConfigurationItems.SingleAsync(i => i.Id == matches[0], cancellationToken).ConfigureAwait(false);
                stats.Linked++;
            }
        }

        var created = false;
        if (item is null)
        {
            var sequence = await CmdbNumbers.NextAsync(db, tenant.TenantId!.Value, cancellationToken).ConfigureAwait(false);
            item = new ConfigurationItem
            {
                TenantId = tenant.TenantId!.Value, Number = CmdbNumbers.Format(prefix, sequence), Sequence = sequence, ClassId = ciClass.Id, Name = name, Status = CiStatus.Active,
                CreatedAt = now, UpdatedAt = now,
            };
            db.ConfigurationItems.Add(item);
            created = true;
        }

        sourceRecord.ConfigurationItemId = item.Id;
        sourceRecord.State = SourceRecordState.Linked;
        var changed = Apply(source.Id, item, name, attributes, classes, precedence, stats, created);
        if (item.MissingSince is not null)
        {
            item.MissingSince = null;
            changed = true;
        }

        if (created)
        {
            stats.Created++;
        }
        else if (changed)
        {
            item.UpdatedAt = now;
            stats.Updated++;
        }
        else
        {
            stats.Unchanged++;
        }

        if (created || changed)
        {
            indexer.Upsert(CmdbSearch.Document(item, classes, roles));
        }

        // A new item matched nothing, so nothing shares its values; others are checked again.
        if (!created && changed && item.Status != CiStatus.Retired)
        {
            stats.Duplicates += await RaiseDuplicatesAsync(item, classes, now, cancellationToken).ConfigureAwait(false);
        }

        return item;
    }

    /// <summary>Sets the values and name the source may set; returns whether anything changed.</summary>
    private static bool Apply(Guid sourceId, ConfigurationItem item, string name, JsonObject reported, CiClassIndex classes, CmdbPrecedence precedence, ReconcileStats stats, bool created)
    {
        var fields = classes.Attributes(item.ClassId).Select(a => a.Field)
            .Where(f => f.Type is not (FieldType.User or FieldType.Location)).ToDictionary(f => f.Key, StringComparer.Ordinal);
        var values = (JsonNode.Parse(item.Attributes) as JsonObject) ?? [];
        var setters = CmdbPrecedence.ReadSources(item.AttributeSources);
        var changed = false;
        foreach (var (key, value) in reported)
        {
            if (value is null || !fields.TryGetValue(key, out var field))
            {
                continue;
            }

            if (!precedence.MayOverwrite(sourceId, (string?)setters[key], key))
            {
                continue;
            }

            // Each value is checked on its own with the class's checks; one bad value does not stop the rest.
            var (clean, errors, _, _) = CmdbAttributes.CheckValues([field with { Required = false }], new JsonObject { [key] = value.DeepClone() });
            if (errors.Count > 0 || clean[key] is not { } accepted)
            {
                stats.ValuesRefused++;
                continue;
            }

            if (!JsonNode.DeepEquals(values[key], accepted))
            {
                values[key] = accepted.DeepClone();
                changed = true;
            }

            setters[key] = sourceId.ToString();
        }

        if ((created || name != item.Name) && precedence.MayOverwrite(sourceId, (string?)setters[CmdbIdentification.NameKey], CmdbIdentification.NameKey))
        {
            changed |= name != item.Name;
            item.Name = name;
            setters[CmdbIdentification.NameKey] = sourceId.ToString();
        }

        if (changed || created)
        {
            item.Attributes = values.ToJsonString();
        }

        var settersJson = setters.ToJsonString();
        if (settersJson != item.AttributeSources)
        {
            item.AttributeSources = settersJson;
        }

        return changed;
    }

    /// <summary>
    /// Raises possible duplicates of <paramref name="item"/>: other items under the same top class
    /// (not retired, not merged) that share the values of one of its identification rules. A pair
    /// is raised once, whatever was decided about it.
    /// </summary>
    public async Task<int> RaiseDuplicatesAsync(ConfigurationItem item, CiClassIndex classes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var attributes = (JsonNode.Parse(item.Attributes) as JsonObject) ?? [];
        var scope = CmdbIdentification.Scope(classes, item.ClassId);
        var raised = 0;
        foreach (var rule in CmdbIdentification.RulesFor(classes, item.ClassId))
        {
            if (CmdbIdentification.Values(rule, attributes, item.Name) is not { } values || rule.All(k => k == CmdbIdentification.NameKey))
            {
                // Names alone are too weak to call two items the same.
                continue;
            }

            foreach (var other in await CmdbIdentification.MatchAsync(db, scope, values.Identifiers, values.Name, item.Id, cancellationToken).ConfigureAwait(false))
            {
                var (first, second) = item.Id.CompareTo(other) < 0 ? (item.Id, other) : (other, item.Id);
                if (await db.CiReviewItems.AnyAsync(r => r.Kind == CiReviewKind.PossibleDuplicate && r.ConfigurationItemId == first && r.OtherItemId == second, cancellationToken)
                        .ConfigureAwait(false)
                    || db.ChangeTracker.Entries<CiReviewItem>().Any(e => e.Entity.ConfigurationItemId == first && e.Entity.OtherItemId == second))
                {
                    continue;
                }

                db.CiReviewItems.Add(new CiReviewItem
                {
                    TenantId = tenant.TenantId!.Value, Kind = CiReviewKind.PossibleDuplicate, ConfigurationItemId = first, OtherItemId = second,
                    MatchedOn = Cut(string.Join('+', rule), CiReviewItem.MatchedOnMaxLength),
                    MatchedValue = Cut(string.Join(" + ", values.Identifiers.Select(i => i[(i.IndexOf('=', StringComparison.Ordinal) + 1)..]).Concat(values.Name is null ? [] : [values.Name])),
                        CiReviewItem.MatchedValueMaxLength),
                    CreatedAt = now,
                });
                raised++;
            }
        }

        return raised;
    }

    private async Task<ConfigurationItem?> FollowMergesAsync(Guid id, CancellationToken cancellationToken)
    {
        for (var hops = 0; hops < 10; hops++)
        {
            var item = await db.ConfigurationItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
            if (item?.MergedIntoId is not { } next)
            {
                return item;
            }

            id = next;
        }

        return null;
    }

    private async Task SoftwareAsync(CiDiscoverySource source, Guid itemId, IReadOnlyList<DiscoveredSoftware>? software, ReconcileStats stats, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (software is null)
        {
            return;
        }

        var reported = software
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => (Key: CmdbSoftware.ProductKey(s.Name, s.Publisher), Name: Cut(s.Name.Trim(), CiInstalledSoftware.NameMaxLength),
                Publisher: s.Publisher?.Trim() is { Length: > 0 } p ? Cut(p, CiInstalledSoftware.NameMaxLength) : null,
                Version: Cut(s.Version?.Trim() ?? string.Empty, CiInstalledSoftware.VersionMaxLength)))
            .DistinctBy(s => (s.Key, s.Version)).Take(2000).ToList();
        var existing = await db.CiInstalledSoftwareItems.Where(s => s.ConfigurationItemId == itemId && s.SourceId == source.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var keep = reported.Select(r => (r.Key, r.Version)).ToHashSet();
        db.CiInstalledSoftwareItems.RemoveRange(existing.Where(e => !keep.Contains((e.ProductKey, e.Version))));
        var present = existing.ToDictionary(e => (e.ProductKey, e.Version));
        foreach (var r in reported)
        {
            if (present.TryGetValue((r.Key, r.Version), out var row))
            {
                row.LastSeenAt = now;
                row.Name = r.Name;
                row.Publisher = r.Publisher;
            }
            else
            {
                db.CiInstalledSoftwareItems.Add(new CiInstalledSoftware
                {
                    TenantId = tenant.TenantId!.Value, ConfigurationItemId = itemId, SourceId = source.Id, ProductKey = r.Key, Name = r.Name, Publisher = r.Publisher, Version = r.Version,
                    LastSeenAt = now,
                });
            }
        }

        stats.Software += reported.Count;
    }

    private async Task RelationshipsAsync(CiDiscoverySource source, List<(Guid From, string Type, string Target)> links, Dictionary<string, Guid> placed, ReconcileStats stats,
        CancellationToken cancellationToken)
    {
        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
        var types = await db.CiRelationshipTypes.AsNoTracking().Where(t => t.Active && t.TargetKind == RelationshipTargetKind.ConfigurationItem)
            .ToDictionaryAsync(t => t.Key, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        // Targets reported earlier, in other runs.
        var missingTargets = links.Select(l => l.Target).Where(t => !placed.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var chunk in missingTargets.Chunk(1000))
        {
            foreach (var r in await db.CiSourceRecords.AsNoTracking().Where(r => r.SourceId == source.Id && chunk.Contains(r.ExternalId) && r.ConfigurationItemId != null)
                         .Select(r => new { r.ExternalId, r.ConfigurationItemId }).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                placed[r.ExternalId] = r.ConfigurationItemId!.Value;
            }
        }

        foreach (var batch in links.Chunk(BatchSize))
        {
            var sources = batch.Select(b => b.From).Distinct().ToList();
            var existing = (await db.CiRelationships.AsNoTracking().Where(r => sources.Contains(r.SourceId) && r.TargetCiId != null)
                .Select(r => new { r.SourceId, r.TypeId, r.TargetCiId }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(r => (r.SourceId, r.TypeId, r.TargetCiId!.Value)).ToHashSet();
            foreach (var (from, typeKey, target) in batch)
            {
                if (!types.TryGetValue(typeKey, out var type) || !placed.TryGetValue(target, out var to) || to == from || !existing.Add((from, type.Id, to)))
                {
                    continue;
                }

                db.CiRelationships.Add(new CiRelationship { TenantId = tenant.TenantId!.Value, TypeId = type.Id, SourceId = from, TargetCiId = to, Note = Cut(source.Name, CiRelationship.NoteMaxLength) });
                stats.Relationships++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        Release();
    }

    /// <summary>
    /// Stops tracking the CMDB records of a saved batch, so a sync of tens of thousands of records
    /// keeps little in memory; other tracked records of the caller (settings, for example) stay.
    /// </summary>
    private void Release()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Unchanged
                     && e.Entity is ConfigurationItem or CiSourceRecord or CiInstalledSoftware or CiReviewItem or CiRelationship or CiRelationshipType or CiNumberSequence).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Installed software, counted per product (task G2, for G3's licence compliance).</summary>
internal static class CmdbSoftware
{
    /// <summary>
    /// The product a name and publisher name: lower case with single spaces, publisher first,
    /// for example <c>microsoft corporation|microsoft 365 apps for enterprise</c>.
    /// </summary>
    public static string ProductKey(string name, string? publisher)
    {
        static string Clean(string? value) => string.Join(' ', (value ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var key = $"{Clean(publisher)}|{Clean(name)}";
        return key.Length <= CiInstalledSoftware.NameMaxLength ? key : key[..CiInstalledSoftware.NameMaxLength];
    }
}

/// <summary>
/// Merging two items from the review queue (task G2): the item kept gets the other's tickets,
/// relationships, source records, installed software and the values it lacks; the other is retired
/// and points at the one kept. Numbers are never reused, so the retired item's number stays.
/// </summary>
internal sealed class CmdbMerger(ZelvoDbContext db, IAuditLog audit, ISearchIndexer indexer)
{
    public async Task MergeAsync(ConfigurationItem keep, ConfigurationItem drop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(drop);
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);

        // Tickets.
        var keepTickets = await db.TicketConfigurationItems.Where(l => l.ConfigurationItemId == keep.Id).Select(l => l.TicketId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var dropLinks = await db.TicketConfigurationItems.Where(l => l.ConfigurationItemId == drop.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var link in dropLinks)
        {
            db.TicketConfigurationItems.Remove(link);
            if (!keepTickets.Contains(link.TicketId))
            {
                db.TicketConfigurationItems.Add(new TicketConfigurationItem { TenantId = link.TenantId, TicketId = link.TicketId, ConfigurationItemId = keep.Id, AddedById = link.AddedById, AddedAt = link.AddedAt });
            }
        }

        // Relationships: re-pointed, unless the kept item already has the same one or it would point at itself.
        var all = await db.CiRelationships.Where(r => r.SourceId == drop.Id || r.TargetCiId == drop.Id || r.SourceId == keep.Id || r.TargetCiId == keep.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ends = all.Where(r => r.SourceId != drop.Id && r.TargetCiId != drop.Id)
            .Select(r => (r.SourceId, r.TypeId, r.TargetCiId, r.TargetUserId, r.TargetDepartmentId, r.TargetCustomerGroupId)).ToHashSet();
        var moved = 0;
        foreach (var relationship in all.Where(r => r.SourceId == drop.Id || r.TargetCiId == drop.Id))
        {
            db.CiRelationships.Remove(relationship);
            var from = relationship.SourceId == drop.Id ? keep.Id : relationship.SourceId;
            var to = relationship.TargetCiId == drop.Id ? keep.Id : relationship.TargetCiId;
            if (to == from || !ends.Add((from, relationship.TypeId, to, relationship.TargetUserId, relationship.TargetDepartmentId, relationship.TargetCustomerGroupId)))
            {
                continue;
            }

            db.CiRelationships.Add(new CiRelationship
            {
                TenantId = relationship.TenantId, TypeId = relationship.TypeId, SourceId = from, TargetCiId = to, TargetUserId = relationship.TargetUserId,
                TargetDepartmentId = relationship.TargetDepartmentId, TargetCustomerGroupId = relationship.TargetCustomerGroupId, Note = relationship.Note,
                CreatedById = relationship.CreatedById, CreatedAt = relationship.CreatedAt,
            });
            moved++;
        }

        // What the sources report, and the software on it.
        foreach (var record in await db.CiSourceRecords.Where(r => r.ConfigurationItemId == drop.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            record.ConfigurationItemId = keep.Id;
        }

        var keepSoftware = (await db.CiInstalledSoftwareItems.Where(s => s.ConfigurationItemId == keep.Id).Select(s => new { s.SourceId, s.ProductKey, s.Version })
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(s => (s.SourceId, s.ProductKey, s.Version)).ToHashSet();
        foreach (var row in await db.CiInstalledSoftwareItems.Where(s => s.ConfigurationItemId == drop.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (keepSoftware.Contains((row.SourceId, row.ProductKey, row.Version)))
            {
                db.CiInstalledSoftwareItems.Remove(row);
            }
            else
            {
                row.ConfigurationItemId = keep.Id;
            }
        }

        // Values the kept item lacks, with who set them.
        var keys = classes.Attributes(keep.ClassId).Select(a => a.Field.Key).ToHashSet(StringComparer.Ordinal);
        var keepValues = (JsonNode.Parse(keep.Attributes) as JsonObject) ?? [];
        var dropValues = (JsonNode.Parse(drop.Attributes) as JsonObject) ?? [];
        var keepSetters = CmdbPrecedence.ReadSources(keep.AttributeSources);
        var dropSetters = CmdbPrecedence.ReadSources(drop.AttributeSources);
        foreach (var (key, value) in dropValues)
        {
            if (value is not null && keys.Contains(key) && keepValues[key] is null)
            {
                keepValues[key] = value.DeepClone();
                if (dropSetters[key] is { } setter)
                {
                    keepSetters[key] = setter.DeepClone();
                }
            }
        }

        keep.Attributes = keepValues.ToJsonString();
        keep.AttributeSources = keepSetters.ToJsonString();
        keep.OwnerGroupId ??= drop.OwnerGroupId;
        keep.LocationId ??= drop.LocationId;
        keep.MissingSince = null;
        keep.UpdatedAt = DateTimeOffset.UtcNow;
        drop.Status = CiStatus.Retired;
        drop.MergedIntoId = keep.Id;
        drop.UpdatedAt = DateTimeOffset.UtcNow;

        // Other open questions about the retired item are no longer needed.
        foreach (var review in await db.CiReviewItems.Where(r => r.State == CiReviewState.Open && (r.ConfigurationItemId == drop.Id || r.OtherItemId == drop.Id))
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            review.State = CiReviewState.Closed;
            review.DecidedAt = DateTimeOffset.UtcNow;
        }

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kept"] = keep.Number, ["merged"] = drop.Number, ["tickets"] = dropLinks.Count, ["relationships"] = moved,
        };
        audit.Add(AuditCategory.Change, "cmdb.items.merged", CmdbAudit.ItemEntity, keep.Id.ToString(), details);
        audit.Add(AuditCategory.Change, "cmdb.items.merged", CmdbAudit.ItemEntity, drop.Id.ToString(), details);
        var roles = await CmdbSearch.ReaderRolesAsync(db, cancellationToken).ConfigureAwait(false);
        indexer.Upsert(CmdbSearch.Document(keep, classes, roles));
        indexer.Upsert(CmdbSearch.Document(drop, classes, roles));
    }
}
