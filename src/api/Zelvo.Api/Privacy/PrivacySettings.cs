using Zelvo.Api.Settings;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Privacy;

/// <summary>
/// Retention periods (GOV-5, task F5; decided 2026-11-25): how long tickets are kept after they
/// close, per record type, and how long requester accounts may stay unused. When a period ends the
/// privacy jobs anonymise the record. Nothing is set by default, so nothing changes until the
/// administrator chooses a period.
/// </summary>
/// <param name="TicketDays">Days after closing, per record type; a type left out is kept.</param>
/// <param name="InactiveAccountDays">Days without sign-in after which a requester account (no staff role, no open ticket) is erased; null keeps them.</param>
internal sealed record RetentionSettings(IReadOnlyDictionary<TicketType, int> TicketDays, int? InactiveAccountDays)
{
    /// <summary>Shortest period accepted: a month, so a mistake cannot empty the tenant overnight.</summary>
    public const int MinimumDays = 30;

    /// <summary>Longest period accepted: 50 years.</summary>
    public const int MaximumDays = 365 * 50;

    public static RetentionSettings None { get; } = new(new Dictionary<TicketType, int>(), null);

    public bool IsValid() =>
        TicketDays.Values.All(d => d is >= MinimumDays and <= MaximumDays)
        && InactiveAccountDays is null or (>= MinimumDays and <= MaximumDays);

    public bool Any => TicketDays.Count > 0 || InactiveAccountDays is not null;
}

/// <summary>Settings → Privacy (task F5).</summary>
internal static class PrivacySettings
{
    /// <summary>Retention periods; the privacy jobs look for tenants that have this key.</summary>
    public static SettingDefinition<RetentionSettings> Retention { get; } =
        new("privacy.retention", RetentionSettings.None, Identity.Permissions.PrivacyManage, r => r.IsValid());

    /// <summary>Days within which a data subject request is answered (30 by default).</summary>
    public static SettingDefinition<int> RequestDueDays { get; } =
        new("privacy.requestDueDays", 30, Identity.Permissions.PrivacyManage, d => d is >= 1 and <= 90);
}
