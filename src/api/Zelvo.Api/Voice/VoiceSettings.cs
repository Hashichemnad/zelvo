using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Voice;

/// <summary>A complaint severity: its name and the priority its complaints get for SLA targets.</summary>
/// <param name="Name">Name in English and Arabic.</param>
/// <param name="Priority">Priority 1 (highest) to 5.</param>
public sealed record VoiceSeverity(SettingText Name, int Priority);

/// <summary>A complaint root-cause category. Inactive ones stay readable on old complaints but cannot be chosen.</summary>
public sealed partial record VoiceRootCause(string Key, SettingText Name, bool Active)
{
    /// <summary>Whether <paramref name="key"/> is a valid key.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// Customer voice settings of a tenant (module 3, task C6), in Settings → Customer voice.
/// </summary>
/// <param name="AnonymousEnabled">
/// Visitors may send complaints, inquiries and suggestions without signing in and follow them with
/// the number and a tracking code (product owner decision 2026-11-24). Off by default.
/// </param>
/// <param name="Severities">Three complaint severities, 1 (highest) first.</param>
/// <param name="RootCauses">Root-cause categories of complaints.</param>
/// <param name="InquiryPriority">Priority of inquiries for SLA targets.</param>
/// <param name="SuggestionPriority">Priority of suggestions for SLA targets.</param>
public sealed record VoiceSettingsValue(
    bool AnonymousEnabled,
    IReadOnlyList<VoiceSeverity> Severities,
    IReadOnlyList<VoiceRootCause> RootCauses,
    int InquiryPriority,
    int SuggestionPriority)
{
    /// <summary>Number of severities.</summary>
    public const int SeverityLevels = 3;

    /// <summary>Most root causes.</summary>
    public const int MaxRootCauses = 30;

    /// <summary>The defaults.</summary>
    public static VoiceSettingsValue Default { get; } = new(
        false,
        [new(new("High", "عالية"), 2), new(new("Medium", "متوسطة"), 3), new(new("Low", "منخفضة"), 4)],
        [
            new("service-delay", new("Delay in delivering the service", "تأخر في تقديم الخدمة"), true),
            new("staff-conduct", new("Staff conduct", "تعامل الموظفين"), true),
            new("process", new("Procedure or requirements", "الإجراءات أو المتطلبات"), true),
            new("information", new("Wrong or missing information", "معلومات غير صحيحة أو ناقصة"), true),
            new("system", new("Digital channel or system", "القناة الرقمية أو النظام"), true),
            new("policy", new("Policy or regulation", "السياسة أو اللوائح"), true),
            new("other", new("Other", "أخرى"), true),
        ],
        3,
        4);

    /// <summary>Priority 1–5.</summary>
    public static bool IsPriority(int p) => p is >= 1 and <= 5;

    /// <summary>Three named severities with priorities; 1–30 root causes with unique keys, at least one active.</summary>
    public bool IsValid() =>
        Severities is { Count: SeverityLevels } && Severities.All(s => s is not null && s.Name is { IsValid: true } && IsPriority(s.Priority))
        && RootCauses is { Count: > 0 and <= MaxRootCauses }
        && RootCauses.All(r => r is not null && VoiceRootCause.IsKey(r.Key) && r.Name is { IsValid: true })
        && RootCauses.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() == RootCauses.Count
        && RootCauses.Any(r => r.Active)
        && IsPriority(InquiryPriority) && IsPriority(SuggestionPriority);

    /// <summary>The priority for SLA targets of a record of <paramref name="type"/> (complaints: of their severity).</summary>
    public int PriorityOf(Data.Tickets.TicketType type, int? severity) => type switch
    {
        Data.Tickets.TicketType.Complaint => Severities[Math.Clamp(severity ?? 2, 1, SeverityLevels) - 1].Priority,
        Data.Tickets.TicketType.Inquiry => InquiryPriority,
        _ => SuggestionPriority,
    };
}

/// <summary>
/// The happiness meter (GOV-2, product owner decision 2026-11-24): three faces by default (or five
/// points), asked when any of the requester's tickets is resolved and as a general rating in the
/// portal; question and point labels are wording the administrator sets to the entity's charter.
/// </summary>
/// <param name="Enabled">Whether the meter is shown at all.</param>
/// <param name="Points">3 or 5.</param>
/// <param name="Question">The question asked about a ticket.</param>
/// <param name="PortalQuestion">The question of the general rating in the portal.</param>
/// <param name="Labels">One label per point, worst (1) first.</param>
/// <param name="AskOnClosure">Asked on resolved and closed tickets.</param>
/// <param name="InPortal">The general rating is shown in the portal.</param>
public sealed record HappinessSettingsValue(
    bool Enabled, int Points, SettingText Question, SettingText PortalQuestion, IReadOnlyList<SettingText> Labels, bool AskOnClosure, bool InPortal)
{
    /// <summary>Three faces: unhappy, neutral, happy.</summary>
    public static IReadOnlyList<SettingText> ThreeLabels { get; } =
        [new("Unhappy", "غير سعيد"), new("Neutral", "محايد"), new("Happy", "سعيد")];

    /// <summary>Five points.</summary>
    public static IReadOnlyList<SettingText> FiveLabels { get; } =
        [new("Very unhappy", "غير سعيد إطلاقًا"), new("Unhappy", "غير سعيد"), new("Neutral", "محايد"), new("Happy", "سعيد"), new("Very happy", "سعيد جدًا")];

    /// <summary>The defaults.</summary>
    public static HappinessSettingsValue Default { get; } = new(
        true, 3,
        new("How happy are you with how we handled this?", "ما مدى سعادتك بطريقة معالجتنا لهذا الطلب؟"),
        new("How happy are you with our services?", "ما مدى سعادتك بخدماتنا؟"),
        ThreeLabels, true, true);

    /// <summary>3 or 5 points with one label each, and both questions named.</summary>
    public bool IsValid() =>
        Points is 3 or 5
        && Question is { IsValid: true } && PortalQuestion is { IsValid: true }
        && Labels is not null && Labels.Count == Points && Labels.All(l => l is { IsValid: true });
}

/// <summary>Setting definitions and Settings → Customer voice endpoints.</summary>
internal static class VoiceSettings
{
    public static SettingDefinition<VoiceSettingsValue> Voice { get; } =
        new("voice.settings", VoiceSettingsValue.Default, Permissions.VoiceManage, v => v.IsValid());

    public static SettingDefinition<HappinessSettingsValue> Happiness { get; } =
        new("happiness.settings", HappinessSettingsValue.Default, Permissions.VoiceManage, v => v.IsValid());

    public static RouteGroupBuilder MapVoiceSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/settings/customer-voice").WithTags("Settings").RequireTenant();
        settings.MapGet("/", GetAsync).WithName("GetCustomerVoiceSettings")
            .WithSummary("Customer voice settings: anonymous submission, complaint severities (with their SLA priorities), root causes, priorities of inquiries and suggestions, and the happiness meter. Anyone, also visitors, can read them (forms and the meter show their names).")
            .AllowAnonymousAccess().Produces<VoiceSettingsResponse>();
        settings.MapPut("/", UpdateAsync).WithName("UpdateCustomerVoiceSettings")
            .WithSummary("Change the customer voice settings. Root causes in use can be made inactive but not removed. Needs voice.manage; audited.")
            .RequirePermission(Permissions.VoiceManage).Produces<VoiceSettingsResponse>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<Ok<VoiceSettingsResponse>> GetAsync(TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await VoiceSettingsResponse.LoadAsync(store, cancellationToken));

    internal static async Task<Results<Ok<VoiceSettingsResponse>, ValidationProblem>> UpdateAsync(
        UpdateVoiceSettingsRequest request, TenantSettingsStore store, Data.ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(Voice, cancellationToken);
        var voice = new VoiceSettingsValue(
            request.AnonymousEnabled ?? current.AnonymousEnabled,
            request.Severities is null ? current.Severities : [.. request.Severities.Select(s => new VoiceSeverity(new SettingText(Trim(s.Name?.En), Trim(s.Name?.Ar)), s.Priority ?? 0))],
            request.RootCauses is null ? current.RootCauses : [.. request.RootCauses.Select(r => new VoiceRootCause(r.Key?.Trim() ?? "", new SettingText(Trim(r.Name?.En), Trim(r.Name?.Ar)), r.Active ?? true))],
            request.InquiryPriority ?? current.InquiryPriority,
            request.SuggestionPriority ?? current.SuggestionPriority);
        if (!voice.IsValid())
        {
            return UserEndpoints.Validation("severities", "Name 3 severities with a priority 1–5 each, 1–30 root causes with unique keys (lower-case letters, digits and hyphens) and names with at least one active, and priorities 1–5 for inquiries and suggestions.");
        }

        // Root causes used by complaints may become inactive, not disappear.
        var used = await db.CustomerVoiceRecords.Where(v => v.RootCauseKey != null).Select(v => v.RootCauseKey!).Distinct().ToListAsync(cancellationToken);
        var missing = used.Where(k => voice.RootCauses.All(r => r.Key != k)).ToList();
        if (missing.Count > 0)
        {
            return UserEndpoints.Validation("rootCauses", $"Root causes in use cannot be removed; make them inactive instead: {string.Join(", ", missing)}.");
        }

        var happinessCurrent = await store.GetAsync(Happiness, cancellationToken);
        var happiness = happinessCurrent;
        if (request.Happiness is { } h)
        {
            var points = h.Points ?? happinessCurrent.Points;
            happiness = new HappinessSettingsValue(
                h.Enabled ?? happinessCurrent.Enabled,
                points,
                h.Question is { } q ? new SettingText(Trim(q.En), Trim(q.Ar)) : happinessCurrent.Question,
                h.PortalQuestion is { } pq ? new SettingText(Trim(pq.En), Trim(pq.Ar)) : happinessCurrent.PortalQuestion,
                h.Labels is { } labels ? [.. labels.Select(l => new SettingText(Trim(l.En), Trim(l.Ar)))]
                    : points == happinessCurrent.Points ? happinessCurrent.Labels
                    : points == 5 ? HappinessSettingsValue.FiveLabels : HappinessSettingsValue.ThreeLabels,
                h.AskOnClosure ?? happinessCurrent.AskOnClosure,
                h.InPortal ?? happinessCurrent.InPortal);
            if (!happiness.IsValid())
            {
                return UserEndpoints.Validation("happiness", "Choose 3 or 5 points with a label for each (English or Arabic, up to 100 characters), and word both questions.");
            }
        }

        await store.SetAsync(Voice, voice, cancellationToken);
        await store.SetAsync(Happiness, happiness, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await VoiceSettingsResponse.LoadAsync(store, cancellationToken));
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A bilingual text sent by the client.</summary>
public sealed class TextInput
{
    /// <summary>English.</summary>
    [StringLength(SettingText.MaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(SettingText.MaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A severity sent by the client.</summary>
public sealed class SeverityInput
{
    /// <summary>Name.</summary>
    public TextInput? Name { get; init; }

    /// <summary>Priority 1–5.</summary>
    public int? Priority { get; init; }
}

/// <summary>A root cause sent by the client.</summary>
public sealed class RootCauseInput
{
    /// <summary>Stable key.</summary>
    [StringLength(50)]
    public string? Key { get; init; }

    /// <summary>Name.</summary>
    public TextInput? Name { get; init; }

    /// <summary>Whether it can be chosen.</summary>
    public bool? Active { get; init; }
}

/// <summary>Happiness meter settings sent by the client; parts left out are kept.</summary>
public sealed class HappinessInput
{
    /// <summary>Shown at all.</summary>
    public bool? Enabled { get; init; }

    /// <summary>3 or 5.</summary>
    public int? Points { get; init; }

    /// <summary>Question about a ticket.</summary>
    public TextInput? Question { get; init; }

    /// <summary>Question of the portal rating.</summary>
    public TextInput? PortalQuestion { get; init; }

    /// <summary>One label per point, worst first; left out when the points change: the default labels.</summary>
    [MaxLength(5)]
    public IReadOnlyList<TextInput>? Labels { get; init; }

    /// <summary>Asked on resolved tickets.</summary>
    public bool? AskOnClosure { get; init; }

    /// <summary>The portal rating is shown.</summary>
    public bool? InPortal { get; init; }
}

/// <summary>New customer voice settings; parts left out are kept.</summary>
public sealed class UpdateVoiceSettingsRequest
{
    /// <summary>Anonymous submission.</summary>
    public bool? AnonymousEnabled { get; init; }

    /// <summary>Three severities.</summary>
    [MaxLength(3)]
    public IReadOnlyList<SeverityInput>? Severities { get; init; }

    /// <summary>Root causes.</summary>
    [MaxLength(30)]
    public IReadOnlyList<RootCauseInput>? RootCauses { get; init; }

    /// <summary>Priority of inquiries 1–5.</summary>
    public int? InquiryPriority { get; init; }

    /// <summary>Priority of suggestions 1–5.</summary>
    public int? SuggestionPriority { get; init; }

    /// <summary>Happiness meter.</summary>
    public HappinessInput? Happiness { get; init; }
}

/// <summary>A severity.</summary>
internal sealed record SeverityResponse(int Level, LocalizedTextDto Name, int Priority);

/// <summary>A root cause.</summary>
internal sealed record RootCauseResponse(string Key, LocalizedTextDto Name, bool Active);

/// <summary>The happiness meter.</summary>
internal sealed record HappinessSettingsResponse(
    bool Enabled, int Points, LocalizedTextDto Question, LocalizedTextDto PortalQuestion, IReadOnlyList<LocalizedTextDto> Labels, bool AskOnClosure, bool InPortal)
{
    public static HappinessSettingsResponse From(HappinessSettingsValue v) => new(
        v.Enabled, v.Points, LocalizedTextDto.From(v.Question.ToText()), LocalizedTextDto.From(v.PortalQuestion.ToText()),
        [.. v.Labels.Select(l => LocalizedTextDto.From(l.ToText()))], v.AskOnClosure, v.InPortal);
}

/// <summary>The tenant's customer voice settings.</summary>
internal sealed record VoiceSettingsResponse(
    bool AnonymousEnabled, IReadOnlyList<SeverityResponse> Severities, IReadOnlyList<RootCauseResponse> RootCauses,
    int InquiryPriority, int SuggestionPriority, HappinessSettingsResponse Happiness, DateTimeOffset? UpdatedAt)
{
    public static async Task<VoiceSettingsResponse> LoadAsync(TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var voice = await store.GetAsync(VoiceSettings.Voice, cancellationToken).ConfigureAwait(false);
        var happiness = await store.GetAsync(VoiceSettings.Happiness, cancellationToken).ConfigureAwait(false);
        return new(
            voice.AnonymousEnabled,
            [.. voice.Severities.Select((s, i) => new SeverityResponse(i + 1, LocalizedTextDto.From(s.Name.ToText()), s.Priority))],
            [.. voice.RootCauses.Select(r => new RootCauseResponse(r.Key, LocalizedTextDto.From(r.Name.ToText()), r.Active))],
            voice.InquiryPriority, voice.SuggestionPriority,
            HappinessSettingsResponse.From(happiness),
            await store.LastChangedAsync([VoiceSettings.Voice, VoiceSettings.Happiness], cancellationToken).ConfigureAwait(false));
    }
}
