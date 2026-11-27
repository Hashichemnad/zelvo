using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data.Wellbeing;

namespace Zelvo.Api.Wellbeing;

/// <summary>Whether wellbeing is on, and the administrator's confirmation that staff were told (UAE PDPL).</summary>
/// <param name="Enabled">On (off by default).</param>
/// <param name="EnabledAt">When it was last turned on.</param>
/// <param name="EnabledById">Who turned it on.</param>
/// <param name="StaffInformed">The administrator confirmed that staff were told what is recorded and why.</param>
internal sealed record WellbeingState(bool Enabled, DateTimeOffset? EnabledAt, Guid? EnabledById, bool StaffInformed)
{
    /// <summary>Off.</summary>
    public static WellbeingState Off { get; } = new(false, null, null, false);
}

/// <summary>Thresholds: a group's own or the tenant's defaults.</summary>
/// <param name="MaxWeightedLoad">Most weighted open work per agent.</param>
/// <param name="MaxNewPerDay">Most new assignments per day, on average over the last 7 days.</param>
/// <param name="MaxAfterHoursHours">Most after-hours hours in the last 7 days.</param>
/// <param name="MaxDifficultPercent">Highest share of angry or escalated tickets over the last 7 days.</param>
internal sealed record WellbeingThresholds(int MaxWeightedLoad, int MaxNewPerDay, int MaxAfterHoursHours, int MaxDifficultPercent)
{
    /// <summary>Zelvo's defaults until an administrator sets the tenant's.</summary>
    public static WellbeingThresholds Default { get; } = new(40, 12, 5, 50);

    /// <summary>Whether every value is in range.</summary>
    public bool IsValid() => MaxWeightedLoad is >= 1 and <= 1000 && MaxNewPerDay is >= 1 and <= 500 && MaxAfterHoursHours is >= 0 and <= 168
        && MaxDifficultPercent is >= 1 and <= 100;

    /// <summary>A group's thresholds: its own values, the rest from these defaults.</summary>
    public WellbeingThresholds For(WellbeingThreshold? group) => group is null
        ? this
        : new(group.MaxWeightedLoad ?? MaxWeightedLoad, group.MaxNewPerDay ?? MaxNewPerDay, group.MaxAfterHoursHours ?? MaxAfterHoursHours,
            group.MaxDifficultPercent ?? MaxDifficultPercent);
}

/// <summary>Tenant settings of agent wellbeing (module 21, task K4).</summary>
internal static class WellbeingSettings
{
    /// <summary>On or off; the wellbeing job looks for tenants where this is on (<c>zelvo_wellbeing_job_tenants()</c>).</summary>
    public static SettingDefinition<WellbeingState> State { get; } = new("wellbeing.state", WellbeingState.Off, Permissions.WellbeingManage);

    /// <summary>The tenant's default thresholds; team leads set their groups' own.</summary>
    public static SettingDefinition<WellbeingThresholds> Defaults { get; } =
        new("wellbeing.defaults", WellbeingThresholds.Default, Permissions.WellbeingManage, t => t.IsValid());

    /// <summary>Days of figures a page may ask for.</summary>
    public static IReadOnlyList<int> Periods { get; } = [7, 28, 90];
}
