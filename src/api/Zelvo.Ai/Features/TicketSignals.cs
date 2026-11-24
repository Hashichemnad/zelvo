using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Ai.Features;

/// <summary>
/// Sentiment detection (module 9: detect sentiment, escalate frustrated customers). Reads the
/// requester's newest message (or the description of a new incident) and records how they sound.
/// When they turn frustrated, the group's team leads are alerted (product owner decision
/// 2026-11-24) through the notification <c>incident.requester-frustrated</c>, which the
/// notification engine finds in the saved insight.
/// </summary>
public sealed class SentimentReader(ZelvoDbContext db, IAiGateway gateway, TimeProvider time)
{
    /// <summary>The last AI call this made, for the worker to tell a provider failure from "nothing to do".</summary>
    public AiResult? LastCall { get; private set; }

    private const string Instructions = """
        Task: sentiment.
        Read the requester's message to a service desk and say how they sound. The message may be in English or Arabic, Modern Standard or Gulf dialect, and may quote earlier messages.
        positive: pleased or thankful. neutral: matter-of-fact. negative: unhappy or disappointed. frustrated: angry, fed up, chasing again, threatening to complain or escalate, or using strong language.
        Answer as JSON: {"sentiment": "positive" | "neutral" | "negative" | "frustrated", "reason": "one short sentence in English"}.
        """;

    /// <summary>Reads the sentiment of <paramref name="commentId"/> (or the description when null); null when the AI is off or the call failed.</summary>
    public async Task<AiTicketInsight?> ReadAsync(Guid ticketId, Guid? commentId, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester).SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null || ticket.Type != TicketType.Incident)
        {
            return null;
        }

        string text;
        var writtenAt = ticket.CreatedAt;
        if (commentId is { } id)
        {
            var comment = await db.TicketComments.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.TicketId == ticketId, cancellationToken).ConfigureAwait(false);
            if (comment is null || comment.ByStaff || comment.Visibility != CommentVisibility.Public)
            {
                return null;
            }

            text = comment.Body;
            writtenAt = comment.CreatedAt;
        }
        else
        {
            text = $"{ticket.Title}\n\n{ticket.Description}";
        }

        var insight = await db.AiTicketInsights.SingleOrDefaultAsync(i => i.TicketId == ticketId, cancellationToken).ConfigureAwait(false);
        // A later message was read already (jobs can run out of order): an older one says nothing new.
        if (insight?.SentimentCommentId is { } readId
            && await db.TicketComments.AsNoTracking().AnyAsync(c => c.Id == readId && c.CreatedAt > writtenAt, cancellationToken).ConfigureAwait(false))
        {
            return insight;
        }

        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.Sentiment,
            Instructions = Instructions,
            Inputs = [new AiInput("message", AiText.Cut(text, 8000))],
            PersonalValues = TicketTriage.Personal(ticket.Requester),
            Json = true,
            MaxOutputTokens = 200,
            EntityType = "ticket",
            EntityId = ticketId.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        LastCall = result;
        if (!result.Succeeded)
        {
            return null;
        }

        using var json = JsonDocument.Parse(result.Text!);
        if (!Enum.TryParse<AiSentiment>(AiText.Text(json.RootElement, "sentiment"), ignoreCase: true, out var sentiment) || !Enum.IsDefined(sentiment))
        {
            return null;
        }

        if (insight is null)
        {
            insight = new AiTicketInsight { TicketId = ticketId };
            db.AiTicketInsights.Add(insight);
        }

        var now = time.GetUtcNow();
        insight.Sentiment = sentiment;
        insight.SentimentReason = AiText.Text(json.RootElement, "reason") is { } reason ? AiText.Cut(reason, AiTicketInsight.ReasonMaxLength) : null;
        insight.SentimentAt = now;
        insight.SentimentCommentId = commentId;
        insight.FrustratedSince = sentiment == AiSentiment.Frustrated ? insight.FrustratedSince ?? now : null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return insight;
    }
}

/// <summary>
/// Duplicate detection (module 9). Open incidents of the last <see cref="WindowDays"/> days whose
/// words overlap with a new incident's are shortlisted on Zelvo's side (English and Arabic word
/// forms unified), and the AI judges which report the same problem. Agents link a duplicate (the
/// new incident becomes its child) or dismiss it.
/// </summary>
public sealed class DuplicateFinder(ZelvoDbContext db, IAiGateway gateway, TimeProvider time)
{
    /// <summary>The last AI call this made, for the worker to tell a provider failure from "nothing to do".</summary>
    public AiResult? LastCall { get; private set; }

    /// <summary>How far back open incidents are compared.</summary>
    public const int WindowDays = 14;

    /// <summary>How many shortlisted incidents the AI sees.</summary>
    public const int Shortlist = 5;

    /// <summary>Lowest confidence kept.</summary>
    public const int MinimumConfidence = 60;

    private const string Instructions = """
        Task: duplicates.
        The first data block is a new incident; the next block lists open incidents, each with a reference such as d1. Texts may be in English or Arabic, Modern Standard or Gulf dialect.
        Which open incidents report the same problem as the new one: the same fault of the same service or device at about the same time, not merely a similar topic?
        Answer as JSON: {"duplicates": [{"ref": "d1", "confidence": 0 to 100, "reason": "one short sentence in English"}]}, with an empty list when there are none.
        """;

    /// <summary>Finds possible duplicates of a new incident; returns those recorded.</summary>
    public async Task<IReadOnlyList<AiDuplicateCandidate>> FindAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester)
            .SingleOrDefaultAsync(t => t.Id == ticketId && t.Type == TicketType.Incident, cancellationToken).ConfigureAwait(false);
        if (ticket is null || !TicketWorkflow.IsOpen(ticket.State) || ticket.ParentId is not null)
        {
            return [];
        }

        var since = ticket.CreatedAt.AddDays(-WindowDays);
        var open = await db.Tickets.AsNoTracking()
            .Where(t => t.Type == TicketType.Incident && t.Id != ticket.Id && t.CreatedAt >= since && t.CreatedAt <= ticket.CreatedAt
                && t.State != TicketState.Resolved && t.State != TicketState.Closed)
            .OrderByDescending(t => t.CreatedAt).Take(500)
            .Select(t => new { t.Id, t.Number, t.Title, t.Description, t.CategoryId, t.CreatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var words = AiText.Words($"{ticket.Title} {ticket.Description}");
        var shortlist = open
            .Select(t => new { t.Id, t.Number, t.Title, t.Description, t.CreatedAt, Score = AiText.Overlap(words, AiText.Words($"{t.Title} {t.Description}")) + (t.CategoryId is not null && t.CategoryId == ticket.CategoryId ? 0.05 : 0) })
            .Where(t => t.Score >= 0.1)
            .OrderByDescending(t => t.Score).Take(Shortlist).ToList();
        if (shortlist.Count == 0)
        {
            return [];
        }

        var refs = shortlist.Select((t, i) => (Ref: AiText.Ref('d', i), t.Id)).ToList();
        var listing = string.Join("\n\n", shortlist.Select((t, i) => $"{AiText.Ref('d', i)} ({t.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}): {t.Title}\n{AiText.Cut(t.Description ?? string.Empty, 600)}"));
        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.Duplicates,
            Instructions = Instructions,
            Inputs =
            [
                new AiInput("new-incident", $"{ticket.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: {ticket.Title}\n{AiText.Cut(ticket.Description ?? string.Empty, 2000)}"),
                new AiInput("open-incidents", listing),
            ],
            PersonalValues = TicketTriage.Personal(ticket.Requester),
            Json = true,
            MaxOutputTokens = 500,
            EntityType = "ticket",
            EntityId = ticket.Id.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        LastCall = result;
        if (!result.Succeeded)
        {
            return [];
        }

        using var json = JsonDocument.Parse(result.Text!);
        var known = await db.AiDuplicateCandidates.Where(c => c.TicketId == ticket.Id).Select(c => c.CandidateId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<AiDuplicateCandidate>();
        if (json.RootElement.TryGetProperty("duplicates", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var reference = AiText.Text(item, "ref");
                var match = refs.FirstOrDefault(r => r.Ref == reference);
                var confidence = Math.Clamp(AiText.Number(item, "confidence") ?? 0, 0, 100);
                if (match.Id == Guid.Empty || confidence < MinimumConfidence || known.Contains(match.Id) || found.Any(f => f.CandidateId == match.Id))
                {
                    continue;
                }

                found.Add(new AiDuplicateCandidate
                {
                    TicketId = ticket.Id,
                    CandidateId = match.Id,
                    Confidence = confidence,
                    Reason = AiText.Text(item, "reason") is { } reason ? AiText.Cut(reason, AiDuplicateCandidate.ReasonMaxLength) : null,
                    CreatedAt = time.GetUtcNow(),
                });
            }
        }

        db.AiDuplicateCandidates.AddRange(found);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }
}
