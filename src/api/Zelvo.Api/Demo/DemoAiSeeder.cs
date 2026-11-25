using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task E5: what the AI found on some sample incidents, so the agent workspace shows the AI
/// assistant without a provider: a triage suggestion waiting for a decision, a frustrated requester
/// and a possible duplicate. The AI features themselves stay off until a provider is set up in
/// Settings → AI (Zelvo ships no model); the demo's administrator can add one.
/// </summary>
internal sealed class DemoAiSeeder : IDemoSeeder
{
    public string Key => "e5-ai";

    public int Order => 210;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var open = await db.Tickets.AsNoTracking().Include(t => t.Incident)
            .Where(t => t.Type == TicketType.Incident && t.State != TicketState.Resolved && t.State != TicketState.Closed && t.ParentId == null)
            .OrderByDescending(t => t.CreatedAt).Take(6).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var categories = await db.Categories.AsNoTracking().Where(c => c.Active).OrderBy(c => c.Depth).ThenBy(c => c.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
        var groups = await db.SupportGroups.AsNoTracking().Where(g => g.Active).OrderBy(g => g.Name.En).ToListAsync(cancellationToken).ConfigureAwait(false);

        // A suggestion for the newest incident.
        var newest = open[0];
        db.AiTriageSuggestions.Add(new AiTriageSuggestion
        {
            TicketId = newest.Id,
            CategoryId = newest.CategoryId ?? categories.FirstOrDefault()?.Id,
            GroupId = newest.GroupId ?? groups.FirstOrDefault()?.Id,
            Impact = 2,
            Urgency = 1,
            Confidence = 78,
            Reason = "Sample suggestion: several people describe the same service as unavailable.",
            CreatedAt = now.AddMinutes(-5),
        });

        // The requester of the next one sounds frustrated.
        if (open.Count > 1)
        {
            db.AiTicketInsights.Add(new AiTicketInsight
            {
                TicketId = open[1].Id,
                Sentiment = AiSentiment.Frustrated,
                SentimentReason = "Sample reading: the requester says it is the third time they ask.",
                SentimentAt = now.AddMinutes(-20),
                FrustratedSince = now.AddMinutes(-20),
            });
        }

        // The newest incident may duplicate an older open one.
        if (open.Count > 1)
        {
            db.AiDuplicateCandidates.Add(new AiDuplicateCandidate
            {
                TicketId = newest.Id,
                CandidateId = open[^1].Id,
                Confidence = 72,
                Reason = "Sample: reported around the same time about a similar service.",
                CreatedAt = now.AddMinutes(-5),
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
