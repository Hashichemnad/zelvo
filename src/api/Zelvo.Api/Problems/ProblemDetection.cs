using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Ai;
using Zelvo.Ai.Features;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Problems;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Problems;

/// <summary>
/// Problem detection settings of a tenant (module 16, task H2), in Settings → Problems.
/// </summary>
/// <param name="SimilarEnabled">Suggest problems from groups of similar incidents (needs the AI feature <c>problemDetection</c>).</param>
/// <param name="WindowDays">How far back similar incidents are compared, 30–90 days.</param>
/// <param name="MinimumIncidents">Fewest similar incidents for a suggestion, 3–100.</param>
/// <param name="TrendsEnabled">Suggest problems from rising trends.</param>
/// <param name="TrendMinimum">Fewest incidents in the last 30 days for a trend, 2–10,000 (decision 10: 10).</param>
/// <param name="TrendFactor">How many times the average of the three 30-day periods before, 1.1–10 (decision 10: 2).</param>
public sealed record ProblemDetectionSettingsValue(
    bool SimilarEnabled, int WindowDays, int MinimumIncidents, bool TrendsEnabled, int TrendMinimum, decimal TrendFactor)
{
    /// <summary>Product owner decision 10 of 2026-11-26: at least 10 incidents in a month and at least twice the average of the three months before.</summary>
    public static ProblemDetectionSettingsValue Default { get; } = new(true, 60, 5, true, 10, 2.0m);

    /// <summary>Every value in its range.</summary>
    public bool IsValid() =>
        WindowDays is >= 30 and <= 90 && MinimumIncidents is >= 3 and <= 100 && TrendMinimum is >= 2 and <= 10_000 && TrendFactor is >= 1.1m and <= 10m;
}

/// <summary>What one detection run found.</summary>
/// <param name="Similar">New suggestions from similar incidents.</param>
/// <param name="Trends">New suggestions from trends.</param>
/// <param name="Updated">Open suggestions that new incidents joined.</param>
/// <param name="SimilarSkipped">Why similar incidents were not looked at: <c>off</c>, <c>ai-off</c> or <c>limit-reached</c>; null when they were.</param>
public sealed record ProblemDetectionRun(int Similar, int Trends, int Updated, string? SimilarSkipped);

/// <summary>
/// Finds problems worth suggesting (module 16, task H2) and puts them in the problem managers'
/// queue (<c>problem_suggestions</c>); never raises a problem itself.
/// <list type="bullet">
/// <item><b>Similar incidents:</b> incidents of the last 30–90 days not linked to a problem are
/// grouped by their English and Arabic words on Zelvo's side (<see cref="SimilarIncidentGroups"/>);
/// groups of the minimum size go to the AI (<see cref="ProblemGroupJudge"/>, masked), at most
/// <see cref="MaxJudgedPerRun"/> a run. A group mostly made of an earlier suggestion's incidents is
/// not suggested again: new incidents join an open one, dismissed and accepted ones stay decided,
/// and one the AI did not confirm is judged again only once it has doubled.</item>
/// <item><b>Trends</b> (decision 10): per category, configuration item and business service, the
/// incidents of the last 30 days against the average of the three 30-day periods before; a subject
/// with at least the minimum and the factor times its usual level is suggested, unless an open
/// problem already covers it or it was suggested in the last 30 days.</item>
/// </list>
/// </summary>
internal sealed class ProblemDetector(
    ZelvoDbContext db, TenantSettingsStore settings, IAiGateway gateway, ProblemGroupJudge judge, IAuditLog audit)
{
    private readonly ProblemTrendDetector _trends = new(db, audit);

    /// <summary>Most groups sent to the AI in one run.</summary>
    public const int MaxJudgedPerRun = 5;

    /// <summary>Most incidents compared for similar groups.</summary>
    public const int MaxCompared = 3000;

    public static SettingDefinition<ProblemDetectionSettingsValue> Settings { get; } =
        new("problems.detection", ProblemDetectionSettingsValue.Default, Permissions.ProblemsManage, v => v.IsValid());

    public async Task<ProblemDetectionRun> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(Settings, cancellationToken).ConfigureAwait(false);
        var similar = 0;
        var trends = 0;
        var updated = 0;
        string? skipped = "off";
        if (value.SimilarEnabled)
        {
            var availability = (await gateway.AvailabilityAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.Feature == AiFeatures.ProblemDetection);
            skipped = availability is { Available: true } ? null : availability?.Reason ?? "ai-off";
            if (skipped is null)
            {
                (similar, updated) = await SimilarAsync(value, now, cancellationToken).ConfigureAwait(false);
            }
        }

        if (value.TrendsEnabled)
        {
            var (made, refreshed) = await _trends.RunAsync(value, now, cancellationToken).ConfigureAwait(false);
            trends = made;
            updated += refreshed;
        }

        return new ProblemDetectionRun(similar, trends, updated, skipped);
    }

    private async Task<(int Made, int Updated)> SimilarAsync(ProblemDetectionSettingsValue value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now.AddDays(-value.WindowDays);
        var linked = db.ProblemIncidents.Select(l => l.IncidentId);
        var rows = await db.Tickets.AsNoTracking()
            .Where(t => t.Type == TicketType.Incident && t.CreatedAt >= since && t.CreatedAt <= now && t.AnonymisedAt == null && !linked.Contains(t.Id))
            .OrderByDescending(t => t.CreatedAt).Take(MaxCompared)
            .Select(t => new { t.Id, t.Title, t.Description, t.CategoryId, t.CreatedAt, t.RequesterId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var groups = SimilarIncidentGroups.Find([.. rows.Select(r => new GroupedIncident(r.Id, r.Title, r.Description, r.CategoryId, r.CreatedAt))], value.MinimumIncidents);
        if (groups.Count == 0)
        {
            return (0, 0);
        }

        var requesters = rows.ToDictionary(r => r.Id, r => r.RequesterId);
        var earlier = await db.ProblemSuggestions
            .Where(s => s.Kind == ProblemSuggestionKind.SimilarIncidents && s.WindowEnd >= since)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var earlierIds = earlier.Select(s => s.Id).ToList();
        var members = (await db.ProblemSuggestionIncidents.AsNoTracking().Where(l => earlierIds.Contains(l.SuggestionId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(l => l.SuggestionId).ToDictionary(g => g.Key, g => g.Select(l => l.IncidentId).ToHashSet());

        var made = 0;
        var updated = 0;
        var judged = 0;
        foreach (var group in groups)
        {
            var ids = group.Select(i => i.Id).ToHashSet();
            var best = earlier.Select(s => (Suggestion: s, Shared: members.GetValueOrDefault(s.Id)?.Count(ids.Contains) ?? 0))
                .OrderByDescending(x => x.Shared).FirstOrDefault();
            if (best.Suggestion is { } previous && best.Shared * 2 >= ids.Count)
            {
                var known = members.GetValueOrDefault(previous.Id) ?? [];
                if (previous.Status == ProblemSuggestionStatus.Open)
                {
                    // Only incidents raised since: those the AI left out stay out.
                    var added = group.Where(i => !known.Contains(i.Id) && i.CreatedAt > previous.WindowEnd).ToList();
                    if (added.Count > 0)
                    {
                        AddIncidents(previous, known, added);
                        previous.IncidentCount = known.Count;
                        previous.WindowEnd = added.Max(i => i.CreatedAt);
                        previous.UpdatedAt = DateTimeOffset.UtcNow;
                        updated++;
                    }

                    continue;
                }

                // Decided ones stay decided; one the AI did not confirm is judged again once it doubled.
                if (previous.Status != ProblemSuggestionStatus.NotConfirmed || ids.Count < known.Count * 2)
                {
                    continue;
                }
            }

            if (judged >= MaxJudgedPerRun)
            {
                break;
            }

            var people = await db.Users.AsNoTracking().Where(u => group.Select(i => requesters[i.Id]).Contains(u.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var judgement = await judge.JudgeAsync(group, AgentAssist.People([.. people]), cancellationToken).ConfigureAwait(false);
            judged++;
            if (!judgement.Call.Succeeded)
            {
                if (judgement.Call.ErrorCode is "ai-off" or "limit-reached")
                {
                    break;
                }

                // A provider failure: the same group is tried again in the next run.
                continue;
            }

            var related = judgement.Related && judgement.Members.Count >= value.MinimumIncidents;
            var kept = related ? judgement.Members : group;
            var categories = kept.Select(i => i.CategoryId).Distinct().ToList();
            var suggestion = new ProblemSuggestion
            {
                Kind = ProblemSuggestionKind.SimilarIncidents,
                Status = related ? ProblemSuggestionStatus.Open : ProblemSuggestionStatus.NotConfirmed,
                Title = judgement.Title,
                Language = judgement.Language,
                Reason = judgement.Reason,
                Confidence = judgement.Confidence,
                CategoryId = categories is [{ } only] ? only : null,
                WindowStart = kept.Min(i => i.CreatedAt),
                WindowEnd = kept.Max(i => i.CreatedAt),
                IncidentCount = kept.Count,
                AiCallId = judgement.Call.CallId,
            };
            db.ProblemSuggestions.Add(suggestion);
            var set = new HashSet<Guid>();
            AddIncidents(suggestion, set, kept);
            earlier.Add(suggestion);
            members[suggestion.Id] = set;
            if (related)
            {
                Audit(suggestion, "problems.suggestion.made");
                made++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (made, updated);
    }

    private void AddIncidents(ProblemSuggestion suggestion, HashSet<Guid> known, IEnumerable<GroupedIncident> incidents)
    {
        foreach (var incident in incidents.OrderByDescending(i => i.CreatedAt))
        {
            if (known.Count >= ProblemSuggestion.MaxIncidents)
            {
                break;
            }

            if (known.Add(incident.Id))
            {
                db.ProblemSuggestionIncidents.Add(new ProblemSuggestionIncident { SuggestionId = suggestion.Id, IncidentId = incident.Id });
            }
        }
    }

    private void Audit(ProblemSuggestion suggestion, string action) => Audit(audit, suggestion, action);

    internal static void Audit(IAuditLog audit, ProblemSuggestion suggestion, string action) =>
        audit.Add(AuditCategory.Change, action, "problem_suggestion", suggestion.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = suggestion.Kind.ToString(),
            ["dimension"] = suggestion.Dimension?.ToString(),
            ["incidents"] = suggestion.IncidentCount,
            ["baseline"] = suggestion.Baseline,
            ["confidence"] = suggestion.Confidence,
            ["aiCallId"] = suggestion.AiCallId,
        });
}

/// <summary>
/// Trends (decision 10 of 2026-11-26), without AI: per category, configuration item and business
/// service, the incidents of the last 30 days against the average of the three 30-day periods
/// before. A subject with at least the minimum and the factor times its usual level is suggested,
/// unless an open problem already covers it or it was suggested in the last 30 days; an open
/// suggestion of the subject is brought up to date instead. Also run by the demo seeder.
/// </summary>
internal sealed class ProblemTrendDetector(ZelvoDbContext db, IAuditLog audit)
{
    /// <summary>A trend's period.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromDays(30);

    /// <summary>Periods before that make a trend's usual level.</summary>
    public const int BaselinePeriods = 3;

    /// <summary>Most configuration items whose impact is followed to business services in one run.</summary>
    public const int MaxTracedItems = 200;

    /// <summary>Steps followed from an item to the business services it reaches.</summary>
    public const int ServiceDepth = 3;

    public async Task<(int Made, int Updated)> RunAsync(ProblemDetectionSettingsValue value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var windowStart = now - Period;
        var since = now - (Period * (BaselinePeriods + 1));
        var incidents = db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Incident && t.CreatedAt >= since && t.CreatedAt < now);

        var counts = new List<(ProblemTrendDimension Dimension, Guid Subject, int Recent, int Before)>();
        var byCategory = await incidents.Where(t => t.CategoryId != null).GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { g.Key, Recent = g.Count(t => t.CreatedAt >= windowStart), Before = g.Count(t => t.CreatedAt < windowStart) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        counts.AddRange(byCategory.Select(c => (ProblemTrendDimension.Category, c.Key, c.Recent, c.Before)));

        // Configuration items: incidents linked to each; business services count as services.
        var links = await db.TicketConfigurationItems.AsNoTracking()
            .Join(incidents, l => l.TicketId, t => t.Id, (l, t) => new { l.ConfigurationItemId, l.TicketId, t.CreatedAt })
            .Join(db.ConfigurationItems.AsNoTracking().Where(i => i.Status != CiStatus.Retired), x => x.ConfigurationItemId, i => i.Id, (x, i) => new { x.ConfigurationItemId, x.TicketId, x.CreatedAt, i.ClassId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var serviceClasses = (await db.CiClasses.AsNoTracking().Where(c => c.Family == CiFamily.BusinessService).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        foreach (var item in links.Where(l => !serviceClasses.Contains(l.ClassId)).GroupBy(l => l.ConfigurationItemId))
        {
            counts.Add((ProblemTrendDimension.ConfigurationItem, item.Key, item.Count(l => l.CreatedAt >= windowStart), item.Count(l => l.CreatedAt < windowStart)));
        }

        // Business services: linked directly, or reached by the impact of a linked item (G1's traversal).
        var services = new Dictionary<Guid, Dictionary<Guid, DateTimeOffset>>();
        foreach (var link in links.Where(l => serviceClasses.Contains(l.ClassId)))
        {
            Reach(services, link.ConfigurationItemId, link.TicketId, link.CreatedAt);
        }

        var traced = links.Where(l => !serviceClasses.Contains(l.ClassId)).GroupBy(l => l.ConfigurationItemId)
            .OrderByDescending(g => g.Count(l => l.CreatedAt >= windowStart)).Take(MaxTracedItems).ToList();
        if (serviceClasses.Count > 0)
        {
            foreach (var item in traced)
            {
                var impact = await CmdbGraph.ImpactAsync(db, [item.Key], ServiceDepth, cancellationToken).ConfigureAwait(false);
                var reached = impact.Items.Select(i => i.Id).ToList();
                if (reached.Count == 0)
                {
                    continue;
                }

                var reachedServices = await db.ConfigurationItems.AsNoTracking().Where(i => reached.Contains(i.Id) && serviceClasses.Contains(i.ClassId))
                    .Select(i => i.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var service in reachedServices)
                {
                    foreach (var link in item)
                    {
                        Reach(services, service, link.TicketId, link.CreatedAt);
                    }
                }
            }
        }

        counts.AddRange(services.Select(s => (ProblemTrendDimension.Service, s.Key, s.Value.Values.Count(c => c >= windowStart), s.Value.Values.Count(c => c < windowStart))));

        var rising = counts.Where(c => c.Recent >= value.TrendMinimum && c.Recent >= (double)value.TrendFactor * c.Before / BaselinePeriods).ToList();
        if (rising.Count == 0)
        {
            return (0, 0);
        }

        var earlier = await db.ProblemSuggestions.Where(s => s.Kind == ProblemSuggestionKind.Trend && (s.Status == ProblemSuggestionStatus.Open || s.CreatedAt >= windowStart))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openProblems = db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Problem && t.State != TicketState.Resolved && t.State != TicketState.Closed);
        var coveredCategories = (await openProblems.Where(t => t.CategoryId != null).Select(t => t.CategoryId!.Value).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var coveredItems = (await db.TicketConfigurationItems.AsNoTracking().Where(l => openProblems.Any(p => p.Id == l.TicketId))
            .Select(l => l.ConfigurationItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var made = 0;
        var updated = 0;
        foreach (var (dimension, subject, recent, before) in rising.OrderByDescending(r => r.Recent))
        {
            var previous = earlier.Where(s => s.Dimension == dimension && (dimension == ProblemTrendDimension.Category ? s.CategoryId == subject : s.ConfigurationItemId == subject))
                .OrderByDescending(s => s.CreatedAt).FirstOrDefault();
            var baseline = Math.Round((double)before / BaselinePeriods, 2);
            var ids = await TrendIncidentsAsync(dimension, subject, windowStart, now, services, cancellationToken).ConfigureAwait(false);
            if (previous is { Status: ProblemSuggestionStatus.Open })
            {
                var known = (await db.ProblemSuggestionIncidents.AsNoTracking().Where(l => l.SuggestionId == previous.Id).Select(l => l.IncidentId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
                var added = ids.Where(id => !known.Contains(id)).ToList();
                if (added.Count > 0 || previous.IncidentCount != recent)
                {
                    foreach (var id in added.Take(Math.Max(0, ProblemSuggestion.MaxIncidents - known.Count)))
                    {
                        db.ProblemSuggestionIncidents.Add(new ProblemSuggestionIncident { SuggestionId = previous.Id, IncidentId = id });
                    }

                    previous.IncidentCount = recent;
                    previous.Baseline = baseline;
                    previous.WindowStart = windowStart;
                    previous.WindowEnd = now;
                    previous.UpdatedAt = DateTimeOffset.UtcNow;
                    updated++;
                }

                continue;
            }

            // Suggested (and decided) in the last 30 days, or already a problem being worked on.
            if (previous is not null
                || (dimension == ProblemTrendDimension.Category ? coveredCategories.Contains(subject) : coveredItems.Contains(subject)))
            {
                continue;
            }

            var suggestion = new ProblemSuggestion
            {
                Kind = ProblemSuggestionKind.Trend,
                Dimension = dimension,
                CategoryId = dimension == ProblemTrendDimension.Category ? subject : null,
                ConfigurationItemId = dimension == ProblemTrendDimension.Category ? null : subject,
                WindowStart = windowStart,
                WindowEnd = now,
                IncidentCount = recent,
                Baseline = baseline,
            };
            db.ProblemSuggestions.Add(suggestion);
            foreach (var id in ids.Take(ProblemSuggestion.MaxIncidents))
            {
                db.ProblemSuggestionIncidents.Add(new ProblemSuggestionIncident { SuggestionId = suggestion.Id, IncidentId = id });
            }

            Audit(suggestion, "problems.suggestion.made");
            made++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (made, updated);
    }

    /// <summary>The newest incidents of a trend's period for its subject.</summary>
    private async Task<List<Guid>> TrendIncidentsAsync(
        ProblemTrendDimension dimension, Guid subject, DateTimeOffset from, DateTimeOffset to, Dictionary<Guid, Dictionary<Guid, DateTimeOffset>> services,
        CancellationToken cancellationToken)
    {
        if (dimension == ProblemTrendDimension.Service)
        {
            return [.. services[subject].Where(x => x.Value >= from).OrderByDescending(x => x.Value).Take(ProblemSuggestion.MaxIncidents).Select(x => x.Key)];
        }

        var incidents = db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Incident && t.CreatedAt >= from && t.CreatedAt < to);
        incidents = dimension == ProblemTrendDimension.Category
            ? incidents.Where(t => t.CategoryId == subject)
            : incidents.Where(t => db.TicketConfigurationItems.Any(l => l.TicketId == t.Id && l.ConfigurationItemId == subject));
        return await incidents.OrderByDescending(t => t.CreatedAt).Take(ProblemSuggestion.MaxIncidents).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Reach(Dictionary<Guid, Dictionary<Guid, DateTimeOffset>> services, Guid service, Guid ticketId, DateTimeOffset createdAt)
    {
        if (!services.TryGetValue(service, out var tickets))
        {
            services[service] = tickets = [];
        }

        tickets[ticketId] = createdAt;
    }

    private void Audit(ProblemSuggestion suggestion, string action) => ProblemDetector.Audit(audit, suggestion, action);
}

/// <summary>
/// Looks for problems to suggest (module 16, task H2) every <c>Problems:Detection:Interval</c>
/// (6 hours by default) in the tenants that have detection on (<c>zelvo_problem_detection_tenants()</c>),
/// under an advisory lock per tenant so several API instances never do it twice.
/// </summary>
internal sealed partial class ProblemDetectionJobs(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ProblemDetectionJobs> logger, TimeProvider time) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Problems:Detection:Jobs:Enabled", true))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var (id, slug) in await TenantsAsync(stoppingToken))
                {
                    await RunTenantAsync(id, slug, time.GetUtcNow(), stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(configuration.GetValue("Problems:Detection:Interval", TimeSpan.FromHours(6)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tenant's run (tests and "Look now" call it); null when another run holds the lock.</summary>
    public async Task<ProblemDetectionRun?> RunTenantAsync(Guid tenantId, string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "problems.detection", "Problem detection");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await LockAsync(db, tenantId, "SELECT pg_try_advisory_lock(hashtext('zelvo.problem-detection'), hashtext(@tenant))", cancellationToken))
            {
                return null;
            }

            try
            {
                return await scope.ServiceProvider.GetRequiredService<ProblemDetector>().RunAsync(now, cancellationToken);
            }
            finally
            {
                await LockAsync(db, tenantId, "SELECT pg_advisory_unlock(hashtext('zelvo.problem-detection'), hashtext(@tenant))", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> LockAsync(ZelvoDbContext db, Guid tenantId, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("tenant", tenantId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<List<(Guid Id, string Slug)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_problem_detection_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The problem detection job failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
