namespace Zelvo.Ai;

/// <summary>
/// The AI features of module 9. Each has its own switch, provider and model in Settings → AI
/// ("model chosen per feature"). Task E4 builds the gateway and the settings; the features
/// themselves arrive in E5, which calls <see cref="IAiGateway"/> with these keys.
/// </summary>
public static class AiFeatures
{
    /// <summary>Auto-categorise, prioritise and route new tickets.</summary>
    public const string Triage = "triage";

    /// <summary>Summarise long ticket threads.</summary>
    public const string Summary = "summary";

    /// <summary>Suggested replies in the agent's chosen language.</summary>
    public const string Reply = "reply";

    /// <summary>One-click translation of messages (LANG-7).</summary>
    public const string Translation = "translation";

    /// <summary>Duplicate ticket detection.</summary>
    public const string Duplicates = "duplicates";

    /// <summary>Sentiment detection, to escalate frustrated customers.</summary>
    public const string Sentiment = "sentiment";

    /// <summary>Virtual agent in the portal and chat, answering from the knowledge base.</summary>
    public const string VirtualAgent = "virtualAgent";

    /// <summary>Knowledge article drafts from resolved tickets.</summary>
    public const string ArticleDrafts = "articleDrafts";

    /// <summary>Not a feature: the test button of Settings → AI. Recorded in usage like any call.</summary>
    public const string Test = "test";

    /// <summary>
    /// Task E3: transcribe WhatsApp voice notes. Off by default; the audio goes to the provider
    /// unmasked (it cannot be masked), the transcript is masked like any text for later AI use
    /// (product owner decision 2026-11-25). Not available with Anthropic (no audio input).
    /// </summary>
    public const string Transcription = "transcription";

    /// <summary>
    /// Task K1 (module 20): turn an administrator's plain-language description of a workflow rule,
    /// in English or Arabic, into a draft of the rule builder. Off until set up; a draft goes live
    /// only after the administrator approves it (product owner decision 2026-11-26).
    /// </summary>
    public const string RuleBuilder = "ruleBuilder";

    /// <summary>
    /// Task H5 (module 17): explain a change's what-if simulation (blast radius, similar past
    /// changes, predicted risk) in plain language for the requester, approvers and the board. Off
    /// until set up; the AI only explains, it never decides (product owner decision 7 of 2026-11-26).
    /// </summary>
    public const string ChangeWhatIf = "changeWhatIf";

    /// <summary>Every feature, in the order Settings shows them.</summary>
    public static IReadOnlyList<string> All { get; } = [Triage, Summary, Reply, Translation, Duplicates, Sentiment, VirtualAgent, ArticleDrafts, Transcription, RuleBuilder, ChangeWhatIf];

    /// <summary>Whether <paramref name="key"/> is a feature of <see cref="All"/>.</summary>
    public static bool IsFeature(string? key) => key is not null && All.Contains(key, StringComparer.Ordinal);
}
