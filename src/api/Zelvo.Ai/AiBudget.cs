using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;

namespace Zelvo.Ai;

/// <summary>Where the tenant stands against its monthly AI spending limit.</summary>
public enum AiBudgetState
{
    /// <summary>No limit is set.</summary>
    NoLimit,

    /// <summary>Below the warning threshold.</summary>
    Ok,

    /// <summary>At or above <see cref="AiBudget.WarningPercent"/> % of the limit: Settings shows a warning.</summary>
    Warning,

    /// <summary>The limit is reached: AI calls stop until next month or until the limit is raised.</summary>
    Reached,
}

/// <summary>Spending of the current month.</summary>
/// <param name="LimitUsd">The monthly limit in US dollars, or null when none is set.</param>
/// <param name="SpentUsd">Cost of this month's calls (calls of models without a price count as 0).</param>
/// <param name="Percent">Spent as a percentage of the limit (rounded down), or null without a limit.</param>
/// <param name="State">Where the tenant stands.</param>
/// <param name="PeriodStart">Start of the month (UTC).</param>
/// <param name="PeriodEnd">Start of next month (UTC), when the limit starts again.</param>
public sealed record AiBudgetStatus(decimal? LimitUsd, decimal SpentUsd, int? Percent, AiBudgetState State, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);

/// <summary>
/// The monthly spending limit (product owner decision 2026-11-23): in US dollars, a warning at
/// 80 %, and at 100 % AI calls stop until the next calendar month (UTC) or until the limit is
/// raised. The limit is a tenant setting (<see cref="MonthlyLimitKey"/>, 0 = no limit).
/// The check runs before each call, so calls running at the same time can pass the limit by
/// their own cost.
/// </summary>
public static class AiBudget
{
    /// <summary>Tenant setting key: the monthly limit in US dollars; 0 means no limit.</summary>
    public const string MonthlyLimitKey = "ai.monthlyLimitUsd";

    /// <summary>Highest limit that can be set.</summary>
    public const decimal MaxLimit = 1_000_000m;

    /// <summary>Share of the limit at which Settings warns (product owner decision 2026-11-23).</summary>
    public const int WarningPercent = 80;

    /// <summary>Whether <paramref name="limit"/> can be set (0 means no limit).</summary>
    public static bool IsLimit(decimal limit) => limit is >= 0 and <= MaxLimit && decimal.Round(limit, 2) == limit;

    /// <summary>The current tenant's monthly limit; 0 when none is set.</summary>
    public static async Task<decimal> ReadLimitAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == MonthlyLimitKey, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return 0;
        }

        try
        {
            using var json = JsonDocument.Parse(row.Value);
            return json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetDecimal(out var limit) && IsLimit(limit) ? limit : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>The month containing <paramref name="now"/>: its start and the start of the next (UTC).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) MonthOf(DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddMonths(1));
    }

    /// <summary>The current tenant's spending this month.</summary>
    public static async Task<AiBudgetStatus> StatusAsync(ZelvoDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var (start, end) = MonthOf(now);
        var limit = await ReadLimitAsync(db, cancellationToken).ConfigureAwait(false);
        var spent = await db.AiCalls.AsNoTracking()
            .Where(c => c.OccurredAt >= start && c.OccurredAt < end && c.CostUsd != null)
            .SumAsync(c => c.CostUsd, cancellationToken).ConfigureAwait(false) ?? 0m;
        return Evaluate(limit, spent, start, end);
    }

    /// <summary>Where <paramref name="spent"/> stands against <paramref name="limit"/> (0 = no limit).</summary>
    public static AiBudgetStatus Evaluate(decimal limit, decimal spent, DateTimeOffset start, DateTimeOffset end)
    {
        if (limit <= 0)
        {
            return new AiBudgetStatus(null, spent, null, AiBudgetState.NoLimit, start, end);
        }

        var percent = (int)Math.Min(int.MaxValue, decimal.Floor(spent * 100m / limit));
        var state = spent >= limit ? AiBudgetState.Reached : percent >= WarningPercent ? AiBudgetState.Warning : AiBudgetState.Ok;
        return new AiBudgetStatus(limit, spent, percent, state, start, end);
    }

    /// <summary>Cost of a call in US dollars, or null when no price is known.</summary>
    public static decimal? Cost(int inputTokens, int outputTokens, decimal? inputPerMillion, decimal? outputPerMillion) =>
        inputPerMillion is { } input && outputPerMillion is { } output
            ? decimal.Round((inputTokens * input + outputTokens * output) / 1_000_000m, 8, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>Formats an amount for messages.</summary>
    public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
