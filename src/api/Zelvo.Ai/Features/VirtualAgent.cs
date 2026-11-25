using System.Text.Json;
using Zelvo.Data.Localization;

namespace Zelvo.Ai.Features;

/// <summary>One turn of a chat with the virtual agent.</summary>
/// <param name="FromPerson">Written by the person (else by the virtual agent).</param>
/// <param name="Text">The message.</param>
public sealed record VirtualAgentTurn(bool FromPerson, string Text);

/// <summary>What the virtual agent answers.</summary>
/// <param name="Text">The answer, in the chat's language.</param>
/// <param name="Articles">The articles it answered from.</param>
/// <param name="Handover">It wants a person to take over (the person asked, or it cannot help).</param>
/// <param name="CallId">The AI call.</param>
public sealed record VirtualAgentAnswer(string Text, IReadOnlyList<GroundingArticle> Articles, bool Handover, long? CallId);

/// <summary>
/// The virtual agent in the portal and the chat widget (module 9; the full one, with requests and
/// actions, is module 23 in Phase 3). It answers only from published knowledge articles the
/// person may read (external ones for visitors), in the chat's language, and hands over to a
/// person when asked, when the person is frustrated, or when the articles do not answer. It never
/// changes anything itself. When the tenant turned on tools of its own MCP servers (task E6,
/// decided 2026-11-24), the virtual agent may use the read-only ones to look things up; a tool
/// that changes data is never run from the chat: the conversation goes to a person instead
/// (approval in the conversation comes with the full virtual agent, module 23).
/// </summary>
public sealed class VirtualAgent(IAiGateway gateway, KnowledgeGrounding knowledge, IAiToolRunner tools)
{
    /// <summary>How many earlier turns are sent.</summary>
    public const int HistoryTurns = 12;

    /// <summary>Answers the person's last turn; null with an error code when the AI cannot answer.</summary>
    public async Task<(VirtualAgentAnswer? Answer, string? ErrorCode)> AnswerAsync(
        IReadOnlyList<VirtualAgentTurn> conversation, string language, IReadOnlyList<string> readers, bool signedIn,
        IReadOnlyCollection<string> people, string chatId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var turns = conversation.TakeLast(HistoryTurns).ToList();
        while (turns.Count > 0 && !turns[0].FromPerson)
        {
            turns.RemoveAt(0);
        }

        if (turns.Count == 0 || !turns[^1].FromPerson)
        {
            return (null, "nothing-to-answer");
        }

        // Search with the person's recent words, newest first.
        var query = string.Join(' ', turns.Where(t => t.FromPerson).Reverse().Take(3).Select(t => t.Text));
        var articles = await knowledge.FindAsync(query, readers, externalOnly: !signedIn, language, 3, cancellationToken).ConfigureAwait(false);
        var request = new AiRequest
        {
            Feature = AiFeatures.VirtualAgent,
            Instructions = $$"""
                Task: virtualAgent.
                You are the virtual agent of a service desk, chatting with a person in its portal or chat window. The person may write in English or Arabic, Modern Standard or Gulf dialect.
                Answer only from the knowledge articles in the data block named knowledge-articles; each starts with its number. Do not use other knowledge, do not guess, and never say you did something: you cannot create, change or look up tickets or accounts.
                If the articles do not answer the question, say so in one sentence and offer to pass the chat to a person. Set handover to true when the person asks for a person, sounds frustrated, or needs something the articles do not cover.
                Reply in {{AiText.LanguageName(language)}}, briefly (at most 120 words), politely and formally.
                Answer as JSON: {"answer": "your reply", "articles": ["numbers of the articles you used"], "handover": false}.
                """,
            Conversation = [.. turns.Select(t => new AiMessage(t.FromPerson ? AiRole.User : AiRole.Assistant, t.Text))],
            Inputs = [new AiInput("knowledge-articles", KnowledgeGrounding.Block(articles))],
            PersonalValues = people,
            Json = true,
            MaxOutputTokens = 600,
            EntityType = "chat",
            EntityId = chatId,
        };

        AiResult result;
        if ((await tools.ToolsAsync(cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            var run = await tools.RunAsync(new AiRequest
            {
                Feature = request.Feature,
                Instructions = request.Instructions + "\nWhen you answer, your JSON object may also carry \"articles\" and \"handover\" as above. You cannot use tools that change data: when the person needs one, set handover to true.",
                Conversation = request.Conversation,
                Inputs = request.Inputs,
                PersonalValues = request.PersonalValues,
                Json = true,
                MaxOutputTokens = request.MaxOutputTokens,
                EntityType = request.EntityType,
                EntityId = request.EntityId,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (run.ApprovalNeeded is not null)
            {
                // A change in another system needs a person: hand over, never run it from the chat.
                return (new VirtualAgentAnswer(
                    language == LocalizedText.Arabic ? "يحتاج هذا الطلب إلى أحد الموظفين، وسأحوّل المحادثة إليه." : "This needs a member of staff; I will pass the chat on.",
                    [], true, run.Result.CallId), null);
            }

            if (run.Answer is null)
            {
                return (null, run.Result.Succeeded ? "no-answer" : run.Result.ErrorCode);
            }

            result = run.Result;
        }
        else
        {
            result = await gateway.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return (null, result.ErrorCode);
            }
        }

        using var json = JsonDocument.Parse(result.Text!);
        var handover = AiText.Bool(json.RootElement, "handover");
        var text = AiText.Text(json.RootElement, "answer");
        if (text is null && !handover)
        {
            return (null, "invalid-json");
        }

        var used = AiText.Strings(json.RootElement, "articles");
        return (new VirtualAgentAnswer(
            text ?? (language == LocalizedText.Arabic ? "سأحوّل المحادثة إلى أحد الموظفين." : "I will pass you to a member of staff."),
            [.. articles.Where(a => used.Contains(a.Number, StringComparer.OrdinalIgnoreCase))], handover, result.CallId), null);
    }
}
