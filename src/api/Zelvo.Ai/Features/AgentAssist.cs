using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Identity;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Ai.Features;

/// <summary>A ticket's conversation as text for the AI, with the people in it (masked before sending).</summary>
/// <param name="Text">Title, description and comments, oldest first.</param>
/// <param name="People">Names and email addresses of the people in it.</param>
/// <param name="Basis">What it was made from: changes when a comment is added.</param>
/// <param name="Language">Language most of it is written in.</param>
public sealed record TicketThread(string Text, IReadOnlyCollection<string> People, string Basis, string Language)
{
    /// <summary>Most characters given to the model.</summary>
    public const int MaxLength = 24_000;

    /// <summary>
    /// The thread of a ticket. Internal notes are included only for staff (<paramref name="withNotes"/>);
    /// people appear as "Requester", "Agent" or "Agent (internal note)", never by name.
    /// </summary>
    public static async Task<TicketThread?> LoadAsync(ZelvoDbContext db, Guid ticketId, bool withNotes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester).Include(t => t.Assignee)
            .SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return null;
        }

        var comments = await db.TicketComments.AsNoTracking()
            .Where(c => c.TicketId == ticketId && (withNotes || c.Visibility == CommentVisibility.Public))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var authorIds = comments.Select(c => c.AuthorId).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Ticket {ticket.Number}, logged {ticket.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, state {ticket.State}\n");
        text.Append(CultureInfo.InvariantCulture, $"Title: {ticket.Title}\n");
        text.Append(CultureInfo.InvariantCulture, $"Description: {ticket.Description ?? "(none)"}\n");
        if (ticket.ResolutionNotes is { Length: > 0 } notes)
        {
            text.Append(CultureInfo.InvariantCulture, $"Resolution notes: {notes}\n");
        }

        // The newest comments matter most: drop the oldest when the thread is too long.
        var lines = comments.Select(c =>
            $"\n[{(c.ByStaff ? c.Visibility == CommentVisibility.Internal ? "Agent (internal note)" : "Agent" : "Requester")}, {c.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}]\n{c.Body}\n").ToList();
        var budget = MaxLength - text.Length;
        var kept = new List<string>();
        for (var i = lines.Count - 1; i >= 0 && budget > 0; i--)
        {
            var line = lines[i].Length > budget ? lines[i][..budget] : lines[i];
            kept.Insert(0, line);
            budget -= line.Length;
        }

        if (kept.Count < lines.Count)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n({lines.Count - kept.Count} earlier messages left out)\n");
        }

        text.AppendJoin(string.Empty, kept);
        var people = TicketTriage.Personal([ticket.Requester, ticket.Assignee, .. authors]);
        var basis = $"{comments.Count}:{comments.LastOrDefault()?.Id.ToString("N")[..12]}:{ticket.UpdatedAt.ToUnixTimeSeconds()}";
        return new TicketThread(text.ToString(), people, basis, AiText.LanguageOf(ticket.Title + ticket.Description + string.Concat(comments.Select(c => c.Body))));
    }
}

/// <summary>A knowledge article given to the AI to answer from.</summary>
/// <param name="Id">The article.</param>
/// <param name="Number">Its number, for example <c>KB-000012</c>.</param>
/// <param name="Title">Its title in the language asked for (else the other).</param>
/// <param name="Text">Summary and body (Markdown), shortened.</param>
/// <param name="External">Whether visitors may read it.</param>
public sealed record GroundingArticle(Guid Id, string Number, string Title, string Text, bool External);

/// <summary>
/// Finds published knowledge articles to answer from (suggested replies, the virtual agent). Uses
/// the search index with the caller's readers, so nobody is answered from an article they may not
/// read; when the index is unavailable it falls back to comparing words in the database. Only
/// published articles are used, and only external ones for visitors.
/// </summary>
public sealed class KnowledgeGrounding(ZelvoDbContext db, SearchService search)
{
    /// <summary>Most characters of one article given to the model.</summary>
    public const int ArticleLength = 3000;

    /// <summary>Articles for <paramref name="query"/>, best first.</summary>
    public async Task<IReadOnlyList<GroundingArticle>> FindAsync(
        string query, IReadOnlyList<string> readers, bool externalOnly, string language, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readers);
        if (string.IsNullOrWhiteSpace(query) || readers.Count == 0)
        {
            return [];
        }

        var ids = new List<Guid>();
        try
        {
            var results = await search.SearchAsync(new SearchQuery(AiText.Cut(query, 500), ["article"], readers, PageSize: limit * 2), cancellationToken).ConfigureAwait(false);
            ids.AddRange(results.Items.Select(h => Guid.TryParse(h.Id, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty));
        }
        catch (Exception exception) when (exception is OpenSearchException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            ids.Clear();
        }

        var published = db.KbArticles.AsNoTracking()
            .Where(a => a.State == ArticleState.Published && a.PublishedVersionId != null && (!externalOnly || a.Visibility == ArticleVisibility.External));
        List<(KbArticle Article, KbArticleVersion Version)> found;
        if (ids.Count > 0)
        {
            var rows = await published.Where(a => ids.Contains(a.Id))
                .Join(db.KbArticleVersions, a => a.PublishedVersionId, v => v.Id, (a, v) => new { a, v })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            found = [.. ids.Select(id => rows.FirstOrDefault(r => r.a.Id == id)).Where(r => r is not null).Select(r => (r!.a, r.v))];
        }
        else
        {
            // Without the index: compare words with the newest published articles.
            var rows = await published.OrderByDescending(a => a.PublishedAt).Take(300)
                .Join(db.KbArticleVersions, a => a.PublishedVersionId, v => v.Id, (a, v) => new { a, v })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var words = AiText.Words(query);
            found = [.. rows
                .Select(r => (r.a, r.v, Score: AiText.Overlap(words, AiText.Words($"{r.v.Title.En} {r.v.Title.Ar} {string.Join(' ', r.v.Keywords)} {r.v.Summary.En} {r.v.Summary.Ar}"))))
                .Where(r => r.Score > 0).OrderByDescending(r => r.Score).Select(r => (r.a, r.v))];
            // Readers still apply: internal articles only for callers who read them through the index.
            if (!readers.Any(r => r.StartsWith("role:", StringComparison.Ordinal)))
            {
                found = [.. found.Where(f => f.Item1.Visibility == ArticleVisibility.External)];
            }
        }

        return [.. found.Take(limit).Select(f => new GroundingArticle(
            f.Item1.Id, f.Item1.Number, f.Item2.Title.Get(language) ?? f.Item2.Title.En ?? f.Item2.Title.Ar ?? f.Item1.Number,
            AiText.Cut(Join(f.Item2.Summary.Get(language) ?? f.Item2.Summary.En ?? f.Item2.Summary.Ar, f.Item2.Body.Get(language) ?? f.Item2.Body.En ?? f.Item2.Body.Ar), ArticleLength),
            f.Item1.Visibility == ArticleVisibility.External))];
    }

    /// <summary>The articles as one data block.</summary>
    public static string Block(IReadOnlyList<GroundingArticle> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return articles.Count == 0 ? "(no articles found)" : string.Join("\n\n", articles.Select(a => $"{a.Number}: {a.Title}\n{a.Text}"));
    }

    private static string Join(string? a, string? b) => string.Join("\n\n", new[] { a, b }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>A drafted reply.</summary>
/// <param name="Text">The reply, for the agent to edit and send.</param>
/// <param name="Language">Its language.</param>
/// <param name="Articles">Knowledge articles it is based on.</param>
/// <param name="CallId">The AI call.</param>
public sealed record ReplyDraft(string Text, string Language, IReadOnlyList<GroundingArticle> Articles, long? CallId);

/// <summary>A translation.</summary>
/// <param name="Text">The translated text.</param>
/// <param name="Language">Language translated into.</param>
/// <param name="Cached">Taken from an earlier translation.</param>
public sealed record TranslationResult(string Text, string Language, bool Cached);

/// <summary>A drafted knowledge article (both languages; either may be empty if the answer lacked it).</summary>
public sealed record ArticleDraft(LocalizedText Title, LocalizedText Summary, LocalizedText Body, IReadOnlyList<string> Keywords, long? CallId);

/// <summary>
/// The AI help agents ask for on a ticket (module 9, LANG-7, LANG-10): thread summaries, suggested
/// replies in the agent's chosen language drafted from the conversation and the knowledge base,
/// one-click translation, and knowledge article drafts from resolved tickets. Nothing is sent or
/// saved to the ticket: the agent reads, edits and decides.
/// </summary>
public sealed class AgentAssist(ZelvoDbContext db, IAiGateway gateway, KnowledgeGrounding knowledge, TimeProvider time)
{
    /// <summary>Result of an assist call: the value, or the gateway's error code.</summary>
    public sealed record Outcome<T>(T? Value, string? ErrorCode, string? Error)
        where T : class;

    /// <summary>
    /// A summary of the ticket's thread in <paramref name="language"/>, cached until a comment is
    /// added or the ticket changes (<paramref name="refresh"/> makes a new one).
    /// </summary>
    public async Task<Outcome<AiTicketInsight>> SummarizeAsync(Guid ticketId, string language, bool refresh, CancellationToken cancellationToken)
    {
        var thread = await TicketThread.LoadAsync(db, ticketId, withNotes: true, cancellationToken).ConfigureAwait(false);
        if (thread is null)
        {
            return new(null, "not-found", "The ticket does not exist.");
        }

        var insight = await db.AiTicketInsights.SingleOrDefaultAsync(i => i.TicketId == ticketId, cancellationToken).ConfigureAwait(false);
        if (!refresh && insight is { Summary: not null } && insight.SummaryLanguage == language && insight.SummaryBasis == thread.Basis)
        {
            return new(insight, null, null);
        }

        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.Summary,
            Instructions = $"""
                Task: summary.
                Summarise the ticket in the data block for a service desk agent who has not read it: the problem, what was tried, what was agreed, what is still open, and who is waiting for whom. The messages may be in English or Arabic, Modern Standard or Gulf dialect.
                Write at most six short bullet points starting with "- ", in {AiText.LanguageName(language)}, whatever language the messages are in. Plain text only.
                """,
            Inputs = [new AiInput("ticket", thread.Text)],
            PersonalValues = thread.People,
            MaxOutputTokens = 700,
            EntityType = "ticket",
            EntityId = ticketId.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new(null, result.ErrorCode, result.Error);
        }

        if (insight is null)
        {
            insight = new AiTicketInsight { TicketId = ticketId };
            db.AiTicketInsights.Add(insight);
        }

        insight.Summary = AiText.Cut(result.Text!, AiTicketInsight.SummaryMaxLength);
        insight.SummaryLanguage = language;
        insight.SummaryBasis = thread.Basis;
        insight.SummaryAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(insight, null, null);
    }

    /// <summary>A suggested public reply in <paramref name="language"/>, from the conversation and the knowledge articles <paramref name="readers"/> may read.</summary>
    public async Task<Outcome<ReplyDraft>> DraftReplyAsync(Guid ticketId, string language, IReadOnlyList<string> readers, CancellationToken cancellationToken)
    {
        var thread = await TicketThread.LoadAsync(db, ticketId, withNotes: true, cancellationToken).ConfigureAwait(false);
        if (thread is null)
        {
            return new(null, "not-found", "The ticket does not exist.");
        }

        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, cancellationToken).ConfigureAwait(false);
        var articles = await knowledge.FindAsync($"{ticket.Title} {ticket.Description}", readers, externalOnly: false, language, 3, cancellationToken).ConfigureAwait(false);
        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.Reply,
            Instructions = $$"""
                Task: reply.
                Draft the service desk agent's next public reply to the requester of the ticket in the first data block. The second block holds knowledge articles, each starting with its number.
                Use only facts from the ticket and the articles. If the articles answer the problem, give the steps; if not, ask for the details still missing. Never promise anything the ticket does not show, and never mention internal notes.
                Write in {{AiText.LanguageName(language)}}, whatever language the ticket is in: polite, clear and formal{{(language == LocalizedText.Arabic ? ", in the formal tone of official correspondence" : string.Empty)}}. Address the requester directly; no subject line and no signature.
                Answer as JSON: {"reply": "the reply", "articles": ["numbers of the articles you used"]}.
                """,
            Inputs = [new AiInput("ticket", thread.Text), new AiInput("knowledge-articles", KnowledgeGrounding.Block(articles))],
            PersonalValues = thread.People,
            Json = true,
            MaxOutputTokens = 1200,
            EntityType = "ticket",
            EntityId = ticketId.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new(null, result.ErrorCode, result.Error);
        }

        using var json = JsonDocument.Parse(result.Text!);
        if (AiText.Text(json.RootElement, "reply") is not { } reply)
        {
            return new(null, "invalid-json", "The answer had no reply.");
        }

        var used = AiText.Strings(json.RootElement, "articles");
        return new(new ReplyDraft(reply, language, [.. articles.Where(a => used.Contains(a.Number, StringComparer.OrdinalIgnoreCase))], result.CallId), null, null);
    }

    /// <summary>
    /// Translates a ticket, comment or chat message into <paramref name="language"/> (LANG-7). The
    /// text understands Modern Standard Arabic and Gulf dialect (LANG-10). Cached per language
    /// while the text is unchanged.
    /// </summary>
    public async Task<Outcome<TranslationResult>> TranslateAsync(
        TranslationSource source, Guid sourceId, string text, string language, IReadOnlyCollection<string> people, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = AiText.Hash(text);
        var cached = await db.AiTranslations.SingleOrDefaultAsync(t => t.Source == source && t.SourceId == sourceId && t.Language == language, cancellationToken).ConfigureAwait(false);
        if (cached is not null && cached.SourceHash == hash)
        {
            return new(new TranslationResult(cached.Text, language, true), null, null);
        }

        var translated = await TranslateTextAsync(text, language, people, source.ToString().ToLowerInvariant(), sourceId.ToString(), cancellationToken).ConfigureAwait(false);
        if (translated.Value is not { } value)
        {
            return translated;
        }

        if (cached is null)
        {
            db.AiTranslations.Add(new AiTranslation { Source = source, SourceId = sourceId, Language = language, SourceHash = hash, Text = AiText.Cut(value.Text, AiTranslation.TextMaxLength), CreatedAt = time.GetUtcNow() });
        }
        else
        {
            cached.SourceHash = hash;
            cached.Text = AiText.Cut(value.Text, AiTranslation.TextMaxLength);
            cached.CreatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return translated;
    }

    /// <summary>Translates text that is not saved anywhere (an agent's draft reply), without caching.</summary>
    public async Task<Outcome<TranslationResult>> TranslateTextAsync(
        string text, string language, IReadOnlyCollection<string> people, string entityType, string? entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.Translation,
            Instructions = $"""
                Task: translation.
                Translate the text in the data block into {AiText.LanguageName(language)}. It may be in English or Arabic, Modern Standard or Gulf dialect, or a mix.
                Keep the meaning and tone. Keep names, numbers, ticket and article numbers, links, Markdown and placeholders such as [PERSON_1] exactly as written. If it is already in that language, return it unchanged.
                Answer with the translation only.
                """,
            Inputs = [new AiInput("text", text)],
            PersonalValues = people,
            MaxOutputTokens = Math.Clamp(text.Length, 200, 8000),
            EntityType = entityType,
            EntityId = entityId,
        }, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new(new TranslationResult(result.Text!, language, false), null, null)
            : new(null, result.ErrorCode, result.Error);
    }

    /// <summary>
    /// A knowledge article drafted from a resolved ticket, in English and Arabic, written for
    /// everyone: personal data stays masked (placeholders are not put back into the article).
    /// </summary>
    public async Task<Outcome<ArticleDraft>> DraftArticleAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var thread = await TicketThread.LoadAsync(db, ticketId, withNotes: true, cancellationToken).ConfigureAwait(false);
        if (thread is null)
        {
            return new(null, "not-found", "The ticket does not exist.");
        }

        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.ArticleDrafts,
            Instructions = """
                Task: articleDraft.
                Write a knowledge base article from the resolved ticket in the data block, so that other people with the same problem can solve it, or agents can solve it faster.
                Write it for everyone: leave out who reported it, names, contact details, dates and anything about that one person. Keep placeholders such as [PERSON_1] out of the article.
                Give a short title, a one-sentence summary, and a body in Markdown with the sections "Problem", "Cause" (if known) and "Solution" as numbered steps; in Arabic use "المشكلة" و"السبب" و"الحل".
                Write both an English and an Arabic version (formal Modern Standard Arabic), whatever language the ticket is in, and up to five keywords.
                Answer as JSON: {"title": {"en": "", "ar": ""}, "summary": {"en": "", "ar": ""}, "body": {"en": "", "ar": ""}, "keywords": [""]}.
                """,
            Inputs = [new AiInput("ticket", thread.Text)],
            PersonalValues = thread.People,
            Json = true,
            MaxOutputTokens = 3000,
            EntityType = "ticket",
            EntityId = ticketId.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new(null, result.ErrorCode, result.Error);
        }

        // The gateway put personal values back; an article is for everyone, so mask them again.
        var mask = new Integrations.PersonalDataPseudonymizer();
        foreach (var person in thread.People)
        {
            mask.AddKnownValue(person);
        }

        using var json = JsonDocument.Parse(result.Text!);
        LocalizedText Text(string name, int max) =>
            json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
                ? new LocalizedText(Clean(AiText.Text(value, "en"), max), Clean(AiText.Text(value, "ar"), max))
                : new LocalizedText(null, null);
        string? Clean(string? value, int max) => value is null ? null : AiText.Cut(mask.Mask(value), max);

        var title = Text("title", KbArticleVersion.TitleMaxLength);
        if (title.En is null && title.Ar is null)
        {
            return new(null, "invalid-json", "The answer had no title.");
        }

        return new(new ArticleDraft(title, Text("summary", KbArticleVersion.SummaryMaxLength), Text("body", KbArticleVersion.BodyMaxLength),
            [.. AiText.Strings(json.RootElement, "keywords").Take(5).Select(k => AiText.Cut(mask.Mask(k), 50))], result.CallId), null, null);
    }

    /// <summary>The people to mask in a chat: the visitor and the agents.</summary>
    public static IReadOnlyCollection<string> People(params UserAccount?[] people) => TicketTriage.Personal(people);
}
