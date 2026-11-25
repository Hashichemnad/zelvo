using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets.Reports;

/// <summary>
/// Builds the dashboards of module 10 (task F2) with aggregate SQL on the tenant's own connection,
/// so row-level security applies to every query. Every value is a parameter. The API shows them and
/// the worker puts them in scheduled reports, both through this class with the person's
/// <see cref="ReportScope"/>. Dates are counted in the tenant's time zone.
/// </summary>
/// <remarks>
/// Definitions: <b>created</b>, <b>resolved</b> and <b>closed</b> count tickets by when that
/// happened; SLA figures are the clocks of tickets created in the period, a clock being <b>met</b>
/// when it stopped without a breach and <b>breached</b> when it passed its target (running clocks
/// are not counted in the percentage); agent figures use the ticket's current assignee; a ticket was
/// <b>open</b> at a moment when it had been created and was not yet resolved or closed then.
/// </remarks>
public sealed class ReportBuilder(ZelvoDbContext db)
{
    private static readonly string[] AgeBands = ["lt1d", "1to3d", "3to7d", "7to30d", "gt30d"];

    /// <summary>The tenant's time zone: the default business calendar's (B3), else Dubai.</summary>
    public async Task<TimeZoneInfo> TimeZoneAsync(CancellationToken cancellationToken)
    {
        var id = await db.BusinessCalendars.AsNoTracking().Where(c => c.IsDefault).Select(c => c.TimeZone).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? Data.Sla.BusinessCalendar.DefaultTimeZone;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>The tenant's reporting targets.</summary>
    public Task<ReportTargets> TargetsAsync(CancellationToken cancellationToken) =>
        TicketSettingKeys.ReadAsync(db, ReportTargets.Key, ReportTargets.Default, t => t.IsValid(), cancellationToken);

    /// <summary>Key figures of every dashboard.</summary>
    public async Task<OverviewReport> OverviewAsync(ReportFilter filter, ReportScope scope, CancellationToken cancellationToken)
    {
        var q = await QueryAsync(filter, scope, cancellationToken).ConfigureAwait(false);
        var counts = await q.RowsAsync($"""
            SELECT
              count(*) FILTER (WHERE t.created_at >= @from AND t.created_at < @to),
              count(*) FILTER (WHERE t.resolved_at >= @from AND t.resolved_at < @to),
              count(*) FILTER (WHERE t.state NOT IN ('Resolved', 'Closed')),
              percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM t.responded_at - t.created_at) / 60)
                FILTER (WHERE t.created_at >= @from AND t.created_at < @to AND t.responded_at IS NOT NULL)
            FROM tickets t WHERE {q.Where("t")}
            """, r => (I(r, 0), I(r, 1), I(r, 2), r.IsDBNull(3) ? (double?)null : Math.Round(D(r, 3), 1)), cancellationToken).ConfigureAwait(false);
        var (created, resolved, open, median) = counts.Single();
        var sla = await SlaTotalsAsync(q, cancellationToken).ConfigureAwait(false);
        var (ratings, happiness) = await HappinessTotalsAsync(q, filter, scope, cancellationToken).ConfigureAwait(false);
        return new OverviewReport(filter.From, filter.To, created, resolved, open, sla.Response.Percent, sla.Resolution.Percent, ratings, happiness, median,
            await TargetsAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Tickets created, resolved and closed in the period.</summary>
    public async Task<VolumesReport> VolumesAsync(ReportFilter filter, ReportScope scope, CancellationToken cancellationToken)
    {
        var q = await QueryAsync(filter, scope, cancellationToken).ConfigureAwait(false);
        var created = await q.BucketsAsync("t.created_at", cancellationToken).ConfigureAwait(false);
        var resolved = await q.BucketsAsync("t.resolved_at", cancellationToken).ConfigureAwait(false);
        var closed = await q.BucketsAsync("t.closed_at", cancellationToken).ConfigureAwait(false);
        var series = filter.Buckets().Select(b => new VolumePoint(b, created.GetValueOrDefault(b), resolved.GetValueOrDefault(b), closed.GetValueOrDefault(b))).ToList();
        var inPeriod = "t.created_at >= @from AND t.created_at < @to";
        return new VolumesReport(filter.From, filter.To, filter.EffectiveInterval, created.Values.Sum(), resolved.Values.Sum(), closed.Values.Sum(), series,
            await q.KeysAsync("t.type", inPeriod, cancellationToken).ConfigureAwait(false),
            await q.KeysAsync("t.channel", inPeriod, cancellationToken).ConfigureAwait(false),
            await q.KeysAsync(PriorityExpression, inPeriod, cancellationToken).ConfigureAwait(false),
            await NamedAsync(q, "t.group_id", inPeriod, Groups, cancellationToken).ConfigureAwait(false),
            await NamedAsync(q, "t.category_id", inPeriod, Categories, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Open tickets now, and the open count at the end of each bucket of the period.</summary>
    public async Task<BacklogReport> BacklogAsync(ReportFilter filter, ReportScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var q = await QueryAsync(filter, scope, cancellationToken).ConfigureAwait(false);
        const string open = "t.state NOT IN ('Resolved', 'Closed')";
        var byState = await q.KeysAsync("t.state", open, cancellationToken).ConfigureAwait(false);
        var nowParameter = q.Add(now);
        var ages = await q.KeysAsync($"""
            CASE WHEN {nowParameter} - t.created_at < interval '1 day' THEN 'lt1d'
                 WHEN {nowParameter} - t.created_at < interval '3 days' THEN '1to3d'
                 WHEN {nowParameter} - t.created_at < interval '7 days' THEN '3to7d'
                 WHEN {nowParameter} - t.created_at < interval '30 days' THEN '7to30d'
                 ELSE 'gt30d' END
            """, open, cancellationToken).ConfigureAwait(false);
        var unassigned = (await q.RowsAsync($"SELECT count(*) FROM tickets t WHERE {q.Where("t")} AND {open} AND t.assignee_id IS NULL",
            r => I(r, 0), cancellationToken).ConfigureAwait(false)).Single();

        // The open count at the end of each bucket (or now, for the bucket that has not ended).
        var buckets = filter.Buckets();
        var ends = buckets.Select(b => Min(q.Instant(ReportFilter.Next(b, filter.EffectiveInterval)), now)).ToArray();
        var endsParameter = q.Add(ends, NpgsqlDbType.Array | NpgsqlDbType.TimestampTz);
        var trend = await q.RowsAsync($"""
            SELECT b.i, count(t.id)
            FROM unnest({endsParameter}) WITH ORDINALITY AS b(at, i)
            LEFT JOIN tickets t ON {q.Where("t")} AND t.created_at < b.at AND COALESCE(t.resolved_at, t.closed_at, 'infinity'::timestamptz) >= b.at
            GROUP BY b.i ORDER BY b.i
            """, r => (Index: (int)r.GetInt64(0), Count: I(r, 1)), cancellationToken).ConfigureAwait(false);
        var points = trend.Select(t => new BacklogPoint(buckets[t.Index - 1], t.Count)).ToList();

        return new BacklogReport(filter.From, filter.To, filter.EffectiveInterval, byState.Sum(s => s.Count), unassigned, byState,
            [.. AgeBands.Select(band => new KeyCount(band, ages.FirstOrDefault(a => a.Key == band)?.Count ?? 0))],
            await q.KeysAsync("t.type", open, cancellationToken).ConfigureAwait(false),
            await q.KeysAsync(PriorityExpression, open, cancellationToken).ConfigureAwait(false),
            await NamedAsync(q, "t.group_id", open, Groups, cancellationToken).ConfigureAwait(false),
            points);
    }

    /// <summary>SLA compliance of tickets created in the period.</summary>
    public async Task<SlaReport> SlaAsync(ReportFilter filter, ReportScope scope, CancellationToken cancellationToken)
    {
        var q = await QueryAsync(filter, scope, cancellationToken).ConfigureAwait(false);
        var totals = await SlaTotalsAsync(q, cancellationToken).ConfigureAwait(false);
        var byPriority = await SlaByAsync(q, PriorityExpression, cancellationToken).ConfigureAwait(false);
        var byType = await SlaByAsync(q, "t.type", cancellationToken).ConfigureAwait(false);
        var byGroup = await SlaByAsync(q, "t.group_id::text", cancellationToken).ConfigureAwait(false);
        var byPolicy = await SlaByAsync(q, "s.policy_id::text", cancellationToken).ConfigureAwait(false);
        var groupNames = await Groups(Ids(byGroup), cancellationToken).ConfigureAwait(false);
        var policyIds = Ids(byPolicy);
        var policyNames = await db.SlaPolicies.AsNoTracking().Where(p => policyIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => ReportName.From(p.Name), cancellationToken).ConfigureAwait(false);

        var trend = await SlaByAsync(q, Query.BucketExpression("t.created_at"), cancellationToken).ConfigureAwait(false);
        var points = filter.Buckets().Select(b =>
        {
            var row = trend.FirstOrDefault(t => t.Key == b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return new SlaPoint(b, row?.Response.Percent, row?.Resolution.Percent);
        }).ToList();

        return new SlaReport(filter.From, filter.To, filter.EffectiveInterval, totals.Response, totals.Resolution,
            [.. byPriority.OrderBy(r => r.Key, StringComparer.Ordinal)], byType,
            [.. byGroup.Select(r => r with { Id = Guid.TryParse(r.Key, out var id) ? id : null, Name = Guid.TryParse(r.Key, out var g) ? groupNames.GetValueOrDefault(g) : null })],
            [.. byPolicy.Select(r => r with { Id = Guid.TryParse(r.Key, out var id) ? id : null, Name = Guid.TryParse(r.Key, out var p) ? policyNames.GetValueOrDefault(p) : null })],
            points, (await TargetsAsync(cancellationToken).ConfigureAwait(false)).SlaTargetPercent);
    }

    /// <summary>
    /// Figures per agent: tickets assigned to them that were created in the period, resolved in the
    /// period, open now; median minutes to the first response and hours to resolve; SLA targets met;
    /// share reopened; happiness of their tickets' ratings. Agents the scope allows only; for the
    /// "my performance" scope, just that person.
    /// </summary>
    public async Task<AgentsReport> AgentsAsync(ReportFilter filter, ReportScope scope, CancellationToken cancellationToken)
    {
        var q = await QueryAsync(filter, scope, cancellationToken).ConfigureAwait(false);
        var members = await AgentsInScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        var rows = await q.RowsAsync($"""
            SELECT t.assignee_id,
              count(*) FILTER (WHERE t.created_at >= @from AND t.created_at < @to),
              count(*) FILTER (WHERE t.resolved_at >= @from AND t.resolved_at < @to),
              count(*) FILTER (WHERE t.state NOT IN ('Resolved', 'Closed')),
              percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM t.responded_at - t.created_at) / 60)
                FILTER (WHERE t.created_at >= @from AND t.created_at < @to AND t.responded_at IS NOT NULL),
              percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM t.resolved_at - t.created_at) / 3600)
                FILTER (WHERE t.resolved_at >= @from AND t.resolved_at < @to),
              count(*) FILTER (WHERE t.resolved_at >= @from AND t.resolved_at < @to AND t.reopen_count > 0)
            FROM tickets t WHERE {q.Where("t")} AND t.assignee_id IS NOT NULL
            GROUP BY t.assignee_id
            """, r => (Id: r.GetGuid(0), Assigned: I(r, 1), Resolved: I(r, 2), Open: I(r, 3),
                Response: r.IsDBNull(4) ? (double?)null : Math.Round(D(r, 4), 1), Resolution: r.IsDBNull(5) ? (double?)null : Math.Round(D(r, 5), 1),
                Reopened: I(r, 6)), cancellationToken).ConfigureAwait(false);
        var sla = (await q.RowsAsync($"""
            SELECT t.assignee_id,
              count(*) FILTER (WHERE s.breached_at IS NULL AND s.state = 'Stopped'),
              count(*) FILTER (WHERE s.breached_at IS NOT NULL)
            FROM ticket_slas s JOIN tickets t ON t.id = s.ticket_id
            WHERE {q.Where("t")} AND t.assignee_id IS NOT NULL AND s.target_minutes IS NOT NULL AND t.created_at >= @from AND t.created_at < @to
            GROUP BY t.assignee_id
            """, r => (Id: r.GetGuid(0), Met: I(r, 1), Breached: I(r, 2)), cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.Id);
        var happiness = (await q.RowsAsync($"""
            SELECT t.assignee_id, count(*), avg((h.score - 1) * 100.0 / (h.points - 1))
            FROM happiness_ratings h JOIN tickets t ON t.id = h.ticket_id
            WHERE {q.Where("t")} AND t.assignee_id IS NOT NULL AND h.created_at >= @from AND h.created_at < @to
            GROUP BY t.assignee_id
            """, r => (Id: r.GetGuid(0), Count: I(r, 1), Percent: r.IsDBNull(2) ? (double?)null : Math.Round(D(r, 2), 1)), cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Id);

        var ids = rows.Select(r => r.Id).Concat(members ?? []).Distinct().Where(id => members is null || members.Contains(id)).ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? u.Id.ToString(), cancellationToken).ConfigureAwait(false);
        var agents = ids.Select(id =>
        {
            var row = rows.FirstOrDefault(r => r.Id == id);
            var clocks = sla.GetValueOrDefault(id);
            var rated = happiness.GetValueOrDefault(id);
            return new AgentRow(id, names.GetValueOrDefault(id) ?? string.Empty, row.Assigned, row.Resolved, row.Open, row.Response, row.Resolution,
                new SlaFigures(clocks.Met, clocks.Breached, 0).Percent,
                row.Resolved == 0 ? null : Math.Round(row.Reopened * 100.0 / row.Resolved, 1),
                rated.Count, rated.Percent);
        }).OrderByDescending(a => a.Resolved).ThenByDescending(a => a.Assigned).ThenBy(a => a.Name, StringComparer.CurrentCulture).ToList();
        return new AgentsReport(filter.From, filter.To, agents);
    }

    /// <summary>Happiness meter ratings given in the period, of tickets the scope covers (and portal ratings when allowed).</summary>
    public async Task<HappinessReport> HappinessAsync(ReportFilter filter, ReportScope scope, CancellationToken cancellationToken)
    {
        var q = await QueryAsync(filter, scope, cancellationToken).ConfigureAwait(false);
        var from = RatingsFrom(q, filter, scope);
        var scores = await q.RowsAsync($"SELECT h.points, h.score, count(*) FROM {from} GROUP BY h.points, h.score ORDER BY h.points, h.score",
            r => new ScoreCount(I(r, 0), I(r, 1), I(r, 2)), cancellationToken).ConfigureAwait(false);
        var (ratings, percent) = await HappinessTotalsAsync(q, filter, scope, cancellationToken).ConfigureAwait(false);
        var byType = await HappinessByAsync(q, from, "COALESCE(h.record_type, 'Portal')", cancellationToken).ConfigureAwait(false);
        var byChannel = await HappinessByAsync(q, from, "h.channel", cancellationToken).ConfigureAwait(false);
        var byGroup = await HappinessByAsync(q, from, "t.group_id::text", cancellationToken).ConfigureAwait(false);
        var byService = await HappinessByAsync(q, from, "h.service_id::text", cancellationToken).ConfigureAwait(false);
        var groups = await Groups(Ids(byGroup), cancellationToken).ConfigureAwait(false);
        var serviceIds = Ids(byService);
        var services = await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => ReportName.From(s.Name), cancellationToken).ConfigureAwait(false);
        var trend = await HappinessByAsync(q, from, Query.BucketExpression("h.created_at"), cancellationToken).ConfigureAwait(false);
        var points = filter.Buckets().Select(b =>
        {
            var row = trend.FirstOrDefault(t => t.Key == b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return new HappinessPoint(b, row?.Ratings ?? 0, row?.HappinessPercent);
        }).ToList();
        return new HappinessReport(filter.From, filter.To, filter.EffectiveInterval, ratings, percent, scores, byType, byChannel,
            [.. byGroup.Select(g => g with { Id = Guid.Parse(g.Key), Name = groups.GetValueOrDefault(Guid.Parse(g.Key)) })],
            [.. byService.Select(s => s with { Id = Guid.Parse(s.Key), Name = services.GetValueOrDefault(Guid.Parse(s.Key)) }).Take(50)],
            points, (await TargetsAsync(cancellationToken).ConfigureAwait(false)).HappinessTargetPercent);
    }

    // Incidents carry their priority; customer voice records theirs; service requests have none.
    private const string PriorityExpression =
        "COALESCE((SELECT i.priority FROM incidents i WHERE i.ticket_id = t.id), (SELECT v.priority FROM customer_voice_records v WHERE v.ticket_id = t.id))::text";

    private static async Task<(SlaFigures Response, SlaFigures Resolution)> SlaTotalsAsync(Query q, CancellationToken cancellationToken)
    {
        var rows = await SlaByAsync(q, "'all'", cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? (new(0, 0, 0), new(0, 0, 0)) : (rows[0].Response, rows[0].Resolution);
    }

    private static async Task<List<SlaRow>> SlaByAsync(Query q, string key, CancellationToken cancellationToken)
    {
        var rows = await q.RowsAsync($"""
            SELECT {key} AS k,
              count(*) FILTER (WHERE s.metric = 'Response' AND s.breached_at IS NULL AND s.state = 'Stopped'),
              count(*) FILTER (WHERE s.metric = 'Response' AND s.breached_at IS NOT NULL),
              count(*) FILTER (WHERE s.metric = 'Response' AND s.breached_at IS NULL AND s.state <> 'Stopped'),
              count(*) FILTER (WHERE s.metric = 'Resolution' AND s.breached_at IS NULL AND s.state = 'Stopped'),
              count(*) FILTER (WHERE s.metric = 'Resolution' AND s.breached_at IS NOT NULL),
              count(*) FILTER (WHERE s.metric = 'Resolution' AND s.breached_at IS NULL AND s.state <> 'Stopped')
            FROM ticket_slas s JOIN tickets t ON t.id = s.ticket_id
            WHERE {q.Where("t")} AND s.target_minutes IS NOT NULL AND t.created_at >= @from AND t.created_at < @to
            GROUP BY k ORDER BY k NULLS LAST
            """, r => new SlaRow(r.IsDBNull(0) ? "none" : r.GetString(0), null, null,
                new SlaFigures(I(r, 1), I(r, 2), I(r, 3)), new SlaFigures(I(r, 4), I(r, 5), I(r, 6))), cancellationToken).ConfigureAwait(false);
        return rows;
    }

    // Ratings of tickets in scope, and general portal ratings when the scope allows them and no
    // group, category or record type was chosen.
    private static string RatingsFrom(Query q, ReportFilter filter, ReportScope scope)
    {
        var portal = scope.PortalRatings && filter.Types.Count == 0 && filter.Groups.Count == 0 && filter.Categories.Count == 0;
        return $"""
            happiness_ratings h LEFT JOIN tickets t ON t.id = h.ticket_id
            WHERE h.created_at >= @from AND h.created_at < @to
              AND ((h.ticket_id IS NOT NULL AND {q.Where("t")}){(portal ? " OR h.ticket_id IS NULL" : string.Empty)})
            """;
    }

    private static async Task<(int Ratings, double? Percent)> HappinessTotalsAsync(Query q, ReportFilter filter, ReportScope scope, CancellationToken cancellationToken)
    {
        var rows = await q.RowsAsync($"SELECT count(*), avg((h.score - 1) * 100.0 / (h.points - 1)) FROM {RatingsFrom(q, filter, scope)}",
            r => (I(r, 0), r.IsDBNull(1) ? (double?)null : Math.Round(D(r, 1), 1)), cancellationToken).ConfigureAwait(false);
        return rows.Single();
    }

    private static Task<List<HappinessGroup>> HappinessByAsync(Query q, string from, string key, CancellationToken cancellationToken) =>
        q.RowsAsync($"""
            SELECT {key} AS k, count(*), avg((h.score - 1) * 100.0 / (h.points - 1))
            FROM {from} AND {key} IS NOT NULL
            GROUP BY k ORDER BY count(*) DESC, k
            """, r => new HappinessGroup(r.GetString(0), null, null, I(r, 1), r.IsDBNull(2) ? null : Math.Round(D(r, 2), 1)), cancellationToken);

    private static async Task<IReadOnlyList<NamedCount>> NamedAsync(
        Query q, string column, string condition, Func<List<Guid>, CancellationToken, Task<Dictionary<Guid, ReportName>>> names, CancellationToken cancellationToken)
    {
        var rows = await q.RowsAsync($"""
            SELECT {column}, count(*) FROM tickets t WHERE {q.Where("t")} AND {condition}
            GROUP BY {column} ORDER BY count(*) DESC LIMIT 30
            """, r => (Id: r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), Count: I(r, 1)), cancellationToken).ConfigureAwait(false);
        var found = await names([.. rows.Where(r => r.Id is not null).Select(r => r.Id!.Value)], cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new NamedCount(r.Id, r.Id is { } id ? found.GetValueOrDefault(id) : null, r.Count))];
    }

    private Task<Dictionary<Guid, ReportName>> Groups(List<Guid> ids, CancellationToken cancellationToken) =>
        db.SupportGroups.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => ReportName.From(g.Name), cancellationToken);

    private Task<Dictionary<Guid, ReportName>> Categories(List<Guid> ids, CancellationToken cancellationToken) =>
        db.Categories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => ReportName.From(c.Name), cancellationToken);

    private static List<Guid> Ids<T>(IEnumerable<T> rows)
        where T : notnull =>
        [.. rows.Select(r => r switch { SlaRow s => s.Key, HappinessGroup h => h.Key, _ => null }).Select(k => Guid.TryParse(k, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];

    // Agents a scope may list: everyone (null), members of the groups it covers, or the one agent.
    private async Task<HashSet<Guid>?> AgentsInScopeAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        if (scope.Agent is { } agent)
        {
            return [agent];
        }

        if (scope.Groups is null)
        {
            return null;
        }

        var groups = scope.Groups.ToList();
        return [.. await db.Set<SupportGroupMember>().AsNoTracking().Where(m => groups.Contains(m.GroupId)).Select(m => m.UserId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false)];
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    // Counts are bigint and averages numeric in PostgreSQL.
    private static int I(DbDataReader reader, int ordinal) => Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static double D(DbDataReader reader, int ordinal) => Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private async Task<Query> QueryAsync(ReportFilter filter, ReportScope scope, CancellationToken cancellationToken)
    {
        if (filter.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(filter));
        }

        var zone = await TimeZoneAsync(cancellationToken).ConfigureAwait(false);
        var types = (filter.Types.Count == 0 ? scope.Types : filter.Types.Where(scope.Types.Contains)).Select(t => t.ToString()).Distinct().ToArray();
        Guid[]? groups = filter.Groups.Count == 0 ? scope.Groups?.ToArray() : [.. filter.Groups.Where(g => scope.Groups is null || scope.Groups.Contains(g)).Distinct()];
        Guid[]? categories = null;
        if (filter.Categories.Count > 0)
        {
            // A category includes the ones below it (trees are at most three levels deep).
            var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var chosen = filter.Categories.ToHashSet();
            for (var level = 0; level < 3; level++)
            {
                chosen.UnionWith(all.Where(c => c.ParentId is { } parent && chosen.Contains(parent)).Select(c => c.Id));
            }

            categories = [.. chosen];
        }

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new Query((NpgsqlConnection)db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction,
            filter, zone, types, groups, categories, scope.Agent);
    }

    /// <summary>One report's SQL: the shared ticket condition, the period and its parameters.</summary>
    private sealed class Query
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction? _transaction;
        private readonly List<NpgsqlParameter> _parameters = [];
        private readonly string _condition;
        private readonly TimeZoneInfo _zone;

        public Query(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, ReportFilter filter, TimeZoneInfo zone, string[] types, Guid[]? groups, Guid[]? categories, Guid? agent)
        {
            _connection = connection;
            _transaction = transaction;
            _zone = zone;
            _parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = Instant(filter.From) });
            _parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = Instant(filter.To.AddDays(1)) });
            _parameters.Add(new NpgsqlParameter("tz", NpgsqlDbType.Text) { Value = zone.Id });
            _parameters.Add(new NpgsqlParameter("unit", NpgsqlDbType.Text) { Value = filter.EffectiveInterval.ToString().ToLowerInvariant() });
            var condition = new StringBuilder($"{{0}}.type = ANY({Add(types, NpgsqlDbType.Array | NpgsqlDbType.Text)})");
            if (groups is not null)
            {
                condition.Append(CultureInfo.InvariantCulture, $" AND {{0}}.group_id = ANY({Add(groups, NpgsqlDbType.Array | NpgsqlDbType.Uuid)})");
            }

            if (categories is not null)
            {
                condition.Append(CultureInfo.InvariantCulture, $" AND {{0}}.category_id = ANY({Add(categories, NpgsqlDbType.Array | NpgsqlDbType.Uuid)})");
            }

            if (agent is { } id)
            {
                condition.Append(CultureInfo.InvariantCulture, $" AND {{0}}.assignee_id = {Add(id)}");
            }

            _condition = condition.ToString();
        }

        /// <summary>The ticket condition for the table alias.</summary>
        public string Where(string alias) => string.Format(CultureInfo.InvariantCulture, _condition, alias);

        /// <summary>The UTC instant of local midnight of <paramref name="day"/> in the tenant's time zone.</summary>
        public DateTimeOffset Instant(DateOnly day)
        {
            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            if (_zone.IsInvalidTime(local))
            {
                local = local.AddHours(1);
            }

            return new DateTimeOffset(local, _zone.GetUtcOffset(local)).ToUniversalTime();
        }

        /// <summary>The first day of the bucket of a timestamp column, as text.</summary>
        public static string BucketExpression(string column) => $"to_char(date_trunc(@unit, {column} AT TIME ZONE @tz), 'YYYY-MM-DD')";

        /// <summary>Adds a parameter and returns its placeholder.</summary>
        public string Add(object value, NpgsqlDbType? type = null)
        {
            var name = $"p{_parameters.Count}";
            var parameter = new NpgsqlParameter(name, value);
            if (type is { } t)
            {
                parameter.NpgsqlDbType = t;
            }

            _parameters.Add(parameter);
            return "@" + name;
        }

        /// <summary>Counts per bucket of tickets whose <paramref name="column"/> falls in the period.</summary>
        public async Task<Dictionary<DateOnly, int>> BucketsAsync(string column, CancellationToken cancellationToken)
        {
            var rows = await RowsAsync($"""
                SELECT {BucketExpression(column)} AS b, count(*) FROM tickets t
                WHERE {Where("t")} AND {column} >= @from AND {column} < @to
                GROUP BY b
                """, r => (Bucket: DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), Count: I(r, 1)), cancellationToken).ConfigureAwait(false);
            return rows.ToDictionary(r => r.Bucket, r => r.Count);
        }

        /// <summary>Counts per key of tickets that meet <paramref name="condition"/>.</summary>
        public Task<List<KeyCount>> KeysAsync(string key, string condition, CancellationToken cancellationToken) =>
            RowsAsync($"""
                SELECT {key} AS k, count(*) FROM tickets t WHERE {Where("t")} AND {condition}
                GROUP BY k ORDER BY count(*) DESC, k
                """, r => new KeyCount(r.IsDBNull(0) ? "none" : r.GetString(0), I(r, 1)), cancellationToken);

        /// <summary>Runs <paramref name="sql"/> with every parameter of this query and reads its rows.</summary>
        public async Task<List<T>> RowsAsync<T>(string sql, Func<DbDataReader, T> read, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            foreach (var parameter in _parameters)
            {
                command.Parameters.Add(parameter.Clone());
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(read(reader));
            }

            return rows;
        }
    }
}
