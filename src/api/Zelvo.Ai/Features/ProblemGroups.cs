using System.Globalization;
using System.Text.Json;

namespace Zelvo.Ai.Features;

/// <summary>An incident as the similar-incident grouping sees it.</summary>
/// <param name="Id">The incident.</param>
/// <param name="Title">Its title.</param>
/// <param name="Description">Its description.</param>
/// <param name="CategoryId">Its category.</param>
/// <param name="CreatedAt">When it was raised.</param>
public sealed record GroupedIncident(Guid Id, string Title, string? Description, Guid? CategoryId, DateTimeOffset CreatedAt);

/// <summary>
/// Groups of similar incidents (module 16, task H2), found on Zelvo's side before anything goes to
/// the AI. E5's duplicate shortlist compares one new incident with the open ones of 14 days; this
/// compares every incident of the period with every other, with the same English and Arabic word
/// forms (<see cref="AiText.Words"/>), and gathers the incidents around the ones most others resemble.
/// </summary>
public static class SimilarIncidentGroups
{
    /// <summary>Lowest word overlap (Jaccard index) for two incidents to count as similar.</summary>
    public const double Threshold = 0.3;

    /// <summary>Added to the overlap when both incidents have the same category.</summary>
    public const double SameCategory = 0.05;

    /// <summary>
    /// The groups of at least <paramref name="minimum"/> incidents, largest first. Each incident is
    /// in one group at most: the incident with the most similar others seeds a group with them,
    /// then the next, among those not yet taken.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<GroupedIncident>> Find(IReadOnlyList<GroupedIncident> incidents, int minimum, double threshold = Threshold)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        minimum = Math.Max(2, minimum);
        var words = incidents.Select(i => AiText.Words($"{i.Title} {i.Description}")).ToList();

        // Only incidents that share a word can be similar: an index of who has which word.
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < words.Count; i++)
        {
            foreach (var word in words[i])
            {
                if (!index.TryGetValue(word, out var list))
                {
                    index[word] = list = [];
                }

                list.Add(i);
            }
        }

        var neighbours = new List<int>[incidents.Count];
        var shared = new Dictionary<int, int>();
        for (var i = 0; i < words.Count; i++)
        {
            shared.Clear();
            foreach (var word in words[i])
            {
                foreach (var j in index[word])
                {
                    if (j != i)
                    {
                        shared[j] = shared.GetValueOrDefault(j) + 1;
                    }
                }
            }

            neighbours[i] = [];
            foreach (var (j, count) in shared)
            {
                var overlap = (double)count / (words[i].Count + words[j].Count - count);
                if (incidents[i].CategoryId is not null && incidents[i].CategoryId == incidents[j].CategoryId)
                {
                    overlap += SameCategory;
                }

                if (overlap >= threshold)
                {
                    neighbours[i].Add(j);
                }
            }
        }

        var taken = new bool[incidents.Count];
        var groups = new List<IReadOnlyList<GroupedIncident>>();
        foreach (var seed in Enumerable.Range(0, incidents.Count).OrderByDescending(i => neighbours[i].Count).ThenBy(i => incidents[i].CreatedAt))
        {
            if (taken[seed] || neighbours[seed].Count + 1 < minimum)
            {
                continue;
            }

            var members = neighbours[seed].Where(j => !taken[j]).Prepend(seed).ToList();
            if (members.Count < minimum)
            {
                continue;
            }

            foreach (var member in members)
            {
                taken[member] = true;
            }

            groups.Add([.. members.Select(m => incidents[m]).OrderBy(m => m.CreatedAt)]);
        }

        return [.. groups.OrderByDescending(g => g.Count)];
    }
}

/// <summary>What the AI made of a group of similar incidents.</summary>
/// <param name="Related">They share one underlying cause, with at least <see cref="ProblemGroupJudge.MinimumConfidence"/> confidence.</param>
/// <param name="Confidence">How sure, 0–100.</param>
/// <param name="Title">A short title for the problem, in the incidents' language.</param>
/// <param name="Language">That language (<c>en</c> or <c>ar</c>).</param>
/// <param name="Reason">Why, in one or two sentences.</param>
/// <param name="Members">The incidents that belong (those the AI left out are dropped).</param>
/// <param name="Call">The AI call.</param>
public sealed record ProblemGroupJudgement(bool Related, int Confidence, string? Title, string Language, string? Reason, IReadOnlyList<GroupedIncident> Members, AiResult Call);

/// <summary>
/// Asks the AI, through the gateway (masked, guarded, AI log and audit), whether a group of similar
/// incidents shares one underlying cause (feature <see cref="AiFeatures.ProblemDetection"/>, task H2).
/// The AI only judges: Zelvo found the group, and a problem manager decides.
/// </summary>
public sealed class ProblemGroupJudge(IAiGateway gateway)
{
    /// <summary>Most incidents of a group the AI sees.</summary>
    public const int MaxShown = 40;

    /// <summary>Lowest confidence kept.</summary>
    public const int MinimumConfidence = 60;

    private const string Instructions = """
        Task: problemDetection.
        The data block lists incidents raised over the last weeks, each with a reference such as i1. Texts may be in English or Arabic, Modern Standard or Gulf dialect.
        Zelvo grouped them because their words are similar. Do they share one underlying cause that a problem investigation should fix (the same fault of the same service, system or device), rather than merely a similar topic?
        Leave out incidents that do not fit the shared cause. Never name people.
        Answer as JSON: {"related": true or false, "confidence": 0 to 100, "title": "a short title for the problem, at most 12 words, in LANGUAGE", "reason": "one or two short sentences in LANGUAGE on what they share", "exclude": ["i3"]}.
        """;

    /// <summary>Judges one group; <paramref name="personalValues"/> are masked (the requesters' names and email addresses).</summary>
    public async Task<ProblemGroupJudgement> JudgeAsync(IReadOnlyList<GroupedIncident> group, IReadOnlyCollection<string> personalValues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var shown = group.OrderByDescending(i => i.CreatedAt).Take(MaxShown).OrderBy(i => i.CreatedAt).ToList();
        var language = AiText.LanguageOf(string.Join(' ', shown.Select(i => i.Title)));
        var refs = shown.Select((i, n) => (Ref: AiText.Ref('i', n), Incident: i)).ToList();
        var listing = string.Join("\n\n", refs.Select(r =>
            $"{r.Ref} ({r.Incident.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}): {AiText.Cut(r.Incident.Title, 200)}\n{AiText.Cut(r.Incident.Description ?? string.Empty, 300)}"));
        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.ProblemDetection,
            Instructions = Instructions.Replace("LANGUAGE", AiText.LanguageName(language), StringComparison.Ordinal),
            Inputs = [new AiInput("incidents", listing)],
            PersonalValues = personalValues,
            Json = true,
            MaxOutputTokens = 600,
            EntityType = "ticket",
            EntityId = shown[^1].Id.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new ProblemGroupJudgement(false, 0, null, language, null, [], result);
        }

        using var json = JsonDocument.Parse(result.Text!);
        var root = json.RootElement;
        var confidence = Math.Clamp(AiText.Number(root, "confidence") ?? 0, 0, 100);
        var excluded = AiText.Strings(root, "exclude").ToHashSet(StringComparer.Ordinal);
        var excludedIds = refs.Where(r => excluded.Contains(r.Ref)).Select(r => r.Incident.Id).ToHashSet();

        // Incidents beyond those shown stay: Zelvo's comparison found them, and the AI judged the shared cause.
        var members = group.Where(i => !excludedIds.Contains(i.Id)).ToList();
        var related = AiText.Bool(root, "related") && confidence >= MinimumConfidence;
        return new ProblemGroupJudgement(
            related, confidence, AiText.Text(root, "title") is { } title ? AiText.Cut(title, 300) : null, language,
            AiText.Text(root, "reason") is { } reason ? AiText.Cut(reason, 1000) : null, members, result);
    }
}
