using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Conventions;
using Zelvo.Data;
using Zelvo.Data.Changes;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Changes;

/// <summary>What a change reaches: G1's impact traversal from its configuration items.</summary>
/// <param name="Items">The change's own configuration items.</param>
/// <param name="Impact">What depends on them (affected items, business services, users, departments and customer groups that use them).</param>
/// <param name="AssetUsers">People with an affected asset assigned to them (decision 6).</param>
/// <param name="PeopleCount">People affected: users of the services, members of their departments and people with an affected asset, each counted once.</param>
internal sealed record BlastRadius(IReadOnlyList<ConflictItem> Items, CiImpactResponse Impact, int AssetUsers, int PeopleCount);

/// <summary>A past change like this one, with its review outcome.</summary>
/// <param name="Reasons">Why it is similar: <c>items</c> (shared configuration items), <c>class</c>, <c>team</c>, <c>words</c>.</param>
internal sealed record SimilarChange(
    Guid Id, string Number, string Title, ChangeKind Kind, ChangeOutcome Outcome, string? ReviewNotes, DateTimeOffset? ImplementedAt, int Score,
    IReadOnlyList<string> Reasons, IReadOnlyList<ConflictItem> SharedItems);

/// <summary>The past changes of one cohort behind the predicted risk.</summary>
/// <param name="Cohort"><c>items</c> (the same configuration items), <c>class</c> (items of the same classes) or <c>team</c> (the same group).</param>
/// <param name="Changes">Reviewed changes in the cohort.</param>
/// <param name="Failed">Of those, failed or backed out.</param>
/// <param name="WithProblems">Of those, successful with problems.</param>
/// <param name="FailureRate">Their failure rate in percent (problems count half), smoothed toward the tenant's usual rate.</param>
internal sealed record RiskReason(string Cohort, int Changes, int Failed, int WithProblems, int FailureRate);

/// <summary>
/// The predicted risk (decision 7): statistics of past changes' outcomes on the same items, classes
/// and team, beside the questionnaire's level; never a decision.
/// </summary>
/// <param name="FailureChance">Chance in percent that the change fails or is backed out, judging by the past.</param>
/// <param name="BaseRate">The tenant's usual failure rate in percent (all reviewed changes), or the starting value when there are too few.</param>
/// <param name="History">Whether any past change of the cohorts was found.</param>
internal sealed record PredictedRisk(ChangeRiskLevel Level, int FailureChance, int BaseRate, bool History, IReadOnlyList<RiskReason> Reasons, ChangeRiskLevel? Questionnaire);

/// <summary>The AI's explanation, when asked for.</summary>
internal sealed record WhatIfExplanation(string Text, string Language, DateTimeOffset At);

/// <summary>
/// The what-if simulation of a change (module 17, task H5): the blast radius, similar past changes
/// with their outcomes and the predicted risk. Kept with the change when it is approved
/// (<see cref="Kept"/>); worked out live until then.
/// </summary>
internal sealed record WhatIfResponse(
    Guid ChangeId, BlastRadius Blast, IReadOnlyList<SimilarChange> Similar, PredictedRisk Predicted, WhatIfExplanation? Explanation, DateTimeOffset ComputedAt, bool Kept);

/// <summary>Works out what-if simulations (task H5).</summary>
internal static partial class ChangeSimulator
{
    /// <summary>Steps of the impact traversal.</summary>
    public const int Depth = 3;

    /// <summary>Most past changes read.</summary>
    public const int MaxHistory = 2000;

    /// <summary>Most similar changes shown.</summary>
    public const int MaxSimilar = 8;

    /// <summary>Failure rate assumed while the tenant has too few reviewed changes, in percent.</summary>
    public const int StartingRate = 15;

    /// <summary>Reviewed changes needed before the tenant's own rate is used.</summary>
    public const int MinimumForBaseRate = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The simulation of <paramref name="ticket"/> now.</summary>
    public static async Task<WhatIfResponse> SimulateAsync(ZelvoDbContext db, Ticket ticket, CancellationToken cancellationToken)
    {
        var change = ticket.Change!;
        var links = await db.TicketConfigurationItems.AsNoTracking().Where(l => l.TicketId == ticket.Id).Select(l => l.ConfigurationItemId).ToListAsync(cancellationToken);
        var items = await db.ConfigurationItems.AsNoTracking().Where(i => links.Contains(i.Id))
            .Select(i => new { i.Id, i.Number, i.Name, i.ClassId }).ToListAsync(cancellationToken);
        var blast = await BlastAsync(db, [.. items.Select(i => new ConflictItem(i.Id, i.Number, i.Name))], cancellationToken);

        // Reviewed past changes, with their items and classes.
        var past = await ChangeCore.Changes(db).AsNoTracking()
            .Where(t => t.Id != ticket.Id && t.Change!.Outcome != null)
            .OrderByDescending(t => t.ClosedAt).Take(MaxHistory).ToListAsync(cancellationToken);
        var pastIds = past.Select(p => p.Id).ToList();
        var pastItems = (await db.TicketConfigurationItems.AsNoTracking().Where(l => pastIds.Contains(l.TicketId))
                .Join(db.ConfigurationItems.AsNoTracking(), l => l.ConfigurationItemId, i => i.Id, (l, i) => new { l.TicketId, Item = new ConflictItem(i.Id, i.Number, i.Name), i.ClassId })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.TicketId).ToDictionary(g => g.Key, g => g.ToList());
        var itemIds = items.Select(i => i.Id).ToHashSet();
        var classIds = items.Select(i => i.ClassId).ToHashSet();
        var words = Words($"{ticket.Title} {ticket.Description}");

        var similar = new List<SimilarChange>();
        var cohorts = new Dictionary<string, List<ChangeOutcome>>(StringComparer.Ordinal) { ["items"] = [], ["class"] = [], ["team"] = [] };
        foreach (var p in past)
        {
            var theirs = pastItems.GetValueOrDefault(p.Id) ?? [];
            var shared = theirs.Where(x => itemIds.Contains(x.Item.Id)).Select(x => x.Item).DistinctBy(x => x.Id).ToList();
            var sameClass = theirs.Any(x => classIds.Contains(x.ClassId));
            var sameTeam = ticket.GroupId is not null && p.GroupId == ticket.GroupId;
            var outcome = p.Change!.Outcome!.Value;
            if (shared.Count > 0)
            {
                cohorts["items"].Add(outcome);
            }

            if (sameClass)
            {
                cohorts["class"].Add(outcome);
            }

            if (sameTeam)
            {
                cohorts["team"].Add(outcome);
            }

            var common = words.Count == 0 ? 0 : Words($"{p.Title} {p.Description}").Intersect(words, StringComparer.Ordinal).Count();
            var score = 3 * Math.Min(shared.Count, 3) + (sameClass ? 2 : 0) + (sameTeam ? 1 : 0) + Math.Min(common, 3);
            if (score < 2)
            {
                continue;
            }

            var reasons = new List<string>();
            if (shared.Count > 0)
            {
                reasons.Add("items");
            }

            if (sameClass)
            {
                reasons.Add("class");
            }

            if (sameTeam)
            {
                reasons.Add("team");
            }

            if (common > 0)
            {
                reasons.Add("words");
            }

            similar.Add(new SimilarChange(p.Id, p.Number, p.Title, p.Change.Kind, outcome, p.Change.ReviewNotes, p.Change.ActualEnd ?? p.ResolvedAt, score, reasons, shared));
        }

        var all = past.Select(p => p.Change!.Outcome!.Value).ToList();
        var baseRate = all.Count >= MinimumForBaseRate ? Rate(all) : StartingRate / 100d;
        var predicted = Predict(cohorts, baseRate, change.RiskLevel);
        return new WhatIfResponse(ticket.Id, blast, [.. similar.OrderByDescending(s => s.Score).ThenByDescending(s => s.ImplementedAt).Take(MaxSimilar)], predicted,
            Explanation(change), DateTimeOffset.UtcNow, false);
    }

    /// <summary>The simulation kept with an approved change, or the live one.</summary>
    public static async Task<WhatIfResponse> CurrentAsync(ZelvoDbContext db, Ticket ticket, CancellationToken cancellationToken)
    {
        if (Read(ticket.Change?.WhatIf) is { } kept)
        {
            return kept with { Explanation = Explanation(ticket.Change!) ?? kept.Explanation, Kept = true };
        }

        return await SimulateAsync(db, ticket, cancellationToken);
    }

    /// <summary>Keeps the simulation with the change (when it is approved or scheduled).</summary>
    public static async Task KeepAsync(ZelvoDbContext db, Ticket ticket, CancellationToken cancellationToken)
    {
        var result = await SimulateAsync(db, ticket, cancellationToken);
        ticket.Change!.WhatIf = JsonSerializer.Serialize(result with { Kept = true }, Json);
    }

    /// <summary>Forgets the kept simulation (the change went back to planning).</summary>
    public static void Forget(ChangeRecord change) => change.WhatIf = null;

    /// <summary>Reads a kept simulation.</summary>
    public static WhatIfResponse? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WhatIfResponse>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The predicted risk: each cohort's failure rate (failed and backed out count 1, successful
    /// with problems a half) smoothed toward the tenant's usual rate, weighted by the cohort's
    /// closeness (items 3, class 2, team 1) and its evidence. Low below 20 %, medium below 40 %, high from 40 %.
    /// </summary>
    public static PredictedRisk Predict(IReadOnlyDictionary<string, List<ChangeOutcome>> cohorts, double baseRate, ChangeRiskLevel? questionnaire)
    {
        ArgumentNullException.ThrowIfNull(cohorts);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal) { ["items"] = 3, ["class"] = 2, ["team"] = 1 };
        var reasons = new List<RiskReason>();
        double sum = 0, total = 0;
        foreach (var (cohort, weight) in weights)
        {
            var outcomes = cohorts.GetValueOrDefault(cohort) ?? [];
            if (outcomes.Count == 0)
            {
                continue;
            }

            const double prior = 2;
            var smoothed = (Failures(outcomes) + baseRate * prior) / (outcomes.Count + prior);
            var evidence = weight * outcomes.Count / (outcomes.Count + prior);
            sum += smoothed * evidence;
            total += evidence;
            reasons.Add(new RiskReason(cohort, outcomes.Count, outcomes.Count(o => o is ChangeOutcome.Failed or ChangeOutcome.BackedOut),
                outcomes.Count(o => o == ChangeOutcome.SuccessfulWithProblems), Percent(smoothed)));
        }

        var chance = total > 0 ? sum / total : baseRate;
        var level = chance >= 0.4 ? ChangeRiskLevel.High : chance >= 0.2 ? ChangeRiskLevel.Medium : ChangeRiskLevel.Low;
        return new PredictedRisk(level, Percent(chance), Percent(baseRate), total > 0, reasons, questionnaire);
    }

    /// <summary>The blast radius of <paramref name="items"/>: what depends on them, who uses it, and the people with an affected asset.</summary>
    public static async Task<BlastRadius> BlastAsync(ZelvoDbContext db, IReadOnlyList<ConflictItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new BlastRadius([], new CiImpactResponse([], [], [], 0, false), 0, 0);
        }

        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var ids = items.Select(i => i.Id).ToList();
        var result = await CmdbGraph.ImpactAsync(db, ids, Depth, cancellationToken);
        var impact = await CiImpactResponse.LoadAsync(db, result, cancellationToken);
        var reached = ids.Concat(result.Items.Select(i => i.Id)).ToList();
        var assetUsers = await db.Assets.AsNoTracking().Where(a => reached.Contains(a.ConfigurationItemId) && a.AssignedToId != null)
            .Select(a => a.AssignedToId!.Value).Distinct().ToListAsync(cancellationToken);

        // People counted once: the services' users and departments (as G1 counts them) and the asset holders.
        var people = new HashSet<Guid>(assetUsers);
        people.UnionWith(result.Consumers.Where(c => c.Kind == CiNodeKind.User).Select(c => c.Id));
        var departments = result.Consumers.Where(c => c.Kind == CiNodeKind.Department).Select(c => c.Id).ToList();
        if (departments.Count > 0)
        {
            people.UnionWith(await db.Users.AsNoTracking()
                .Where(u => u.DepartmentId != null && departments.Contains(u.DepartmentId.Value) && u.Status == Data.Identity.UserStatus.Active)
                .Select(u => u.Id).ToListAsync(cancellationToken));
        }

        return new BlastRadius(items, impact, assetUsers.Count, people.Count);
    }

    /// <summary>Words of a title and description for comparison, in English and Arabic: lower case, Arabic letters normalised, the definite article dropped, short and common words left out.</summary>
    public static HashSet<string> Words(string? text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in WordPattern().Split(Normalise(text)))
        {
            var word = raw.Length > 4 && raw.StartsWith("ال", StringComparison.Ordinal) ? raw[2..] : raw;
            if (word.Length >= 3 && !Stopwords.Contains(word))
            {
                result.Add(word.EndsWith('s') && word.Length > 4 ? word[..^1] : word);
            }
        }

        return result;
    }

    private static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
        {
            switch (c)
            {
                case >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ':
                    continue;
                case 'أ' or 'إ' or 'آ':
                    builder.Append('ا');
                    break;
                case 'ى':
                    builder.Append('ي');
                    break;
                case 'ة':
                    builder.Append('ه');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "into", "this", "that", "all", "new", "change", "update",
        "على", "الى", "الي", "من", "في", "عن", "مع", "هذا", "هذه", "تغيير", "تحديث",
    };

    private static double Failures(IEnumerable<ChangeOutcome> outcomes) =>
        outcomes.Sum(o => o switch { ChangeOutcome.Failed or ChangeOutcome.BackedOut => 1d, ChangeOutcome.SuccessfulWithProblems => 0.5, _ => 0 });

    private static double Rate(List<ChangeOutcome> outcomes) => outcomes.Count == 0 ? 0 : Failures(outcomes) / outcomes.Count;

    private static int Percent(double value) => (int)Math.Round(value * 100, MidpointRounding.AwayFromZero);

    private static WhatIfExplanation? Explanation(ChangeRecord change) =>
        change.WhatIfExplanation is { Length: > 0 } text ? new WhatIfExplanation(text, change.WhatIfExplanationLanguage ?? "en", change.WhatIfExplainedAt ?? DateTimeOffset.UtcNow) : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial System.Text.RegularExpressions.Regex WordPattern();
}
