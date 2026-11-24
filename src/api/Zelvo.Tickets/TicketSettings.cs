using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets;

/// <summary>A bilingual name as stored in a tenant setting (LANG-5).</summary>
/// <param name="En">English.</param>
/// <param name="Ar">Arabic.</param>
public sealed record SettingText(string? En, string? Ar)
{
    /// <summary>Maximum length of each language.</summary>
    public const int MaxLength = 100;

    /// <summary>As a bilingual value.</summary>
    public LocalizedText ToText() => new(En, Ar);

    /// <summary>At least one language, each within <see cref="MaxLength"/>.</summary>
    public bool IsValid => ToText().HasAnyValue && (En?.Length ?? 0) <= MaxLength && (Ar?.Length ?? 0) <= MaxLength;
}

/// <summary>
/// The tenant's impact × urgency matrix (module 1; product owner decision 2026-11-22: 3 × 3 giving
/// P1–P5, editable by administrators). Level 1 is the highest; <c>Matrix[impact - 1][urgency - 1]</c>
/// is the priority.
/// </summary>
public sealed record PriorityMatrix(
    IReadOnlyList<SettingText> Impacts,
    IReadOnlyList<SettingText> Urgencies,
    IReadOnlyList<SettingText> Priorities,
    IReadOnlyList<IReadOnlyList<int>> Matrix)
{
    /// <summary>Number of impact and of urgency levels.</summary>
    public const int Levels = 3;

    /// <summary>Number of priorities.</summary>
    public const int PriorityLevels = 5;

    /// <summary>The ITIL-style default: high impact and high urgency give P1, low and low give P5.</summary>
    public static PriorityMatrix Default { get; } = new(
        [new("High", "عالٍ"), new("Medium", "متوسط"), new("Low", "منخفض")],
        [new("High", "عاجل"), new("Medium", "متوسط"), new("Low", "غير عاجل")],
        [new("P1 Critical", "P1 حرجة"), new("P2 High", "P2 عالية"), new("P3 Medium", "P3 متوسطة"), new("P4 Low", "P4 منخفضة"), new("P5 Planning", "P5 للتخطيط")],
        [[1, 2, 3], [2, 3, 4], [3, 4, 5]]);

    /// <summary>Priority of an incident with <paramref name="impact"/> and <paramref name="urgency"/>.</summary>
    public int PriorityOf(int impact, int urgency) => Matrix[impact - 1][urgency - 1];

    /// <summary>Whether <paramref name="level"/> is an impact or urgency level.</summary>
    public static bool IsLevel(int level) => level is >= 1 and <= Levels;

    /// <summary>Three named impacts and urgencies, five named priorities, and a 3 × 3 matrix of priorities.</summary>
    public bool IsValid() =>
        Impacts is { Count: Levels } && Impacts.All(n => n is { IsValid: true })
        && Urgencies is { Count: Levels } && Urgencies.All(n => n is { IsValid: true })
        && Priorities is { Count: PriorityLevels } && Priorities.All(n => n is { IsValid: true })
        && Matrix is { Count: Levels } && Matrix.All(row => row is { Count: Levels } && row.All(p => p is >= 1 and <= PriorityLevels));
}

/// <summary>A reason an incident is on hold. Inactive reasons stay readable on old incidents but cannot be chosen.</summary>
/// <param name="Key">Stable key, for example <c>awaiting-requester</c>.</param>
/// <param name="Name">Name in English and Arabic.</param>
/// <param name="Active">Whether agents can choose it.</param>
/// <param name="PausesSla">
/// Whether SLA clocks pause while a ticket is on hold for this reason (module 8: "pause on waiting
/// for customer"). Missing in settings saved before B3: only <see cref="AwaitingRequester"/> pauses.
/// </param>
public sealed partial record OnHoldReason(string Key, SettingText Name, bool Active, bool? PausesSla = null)
{
    /// <summary>Key of the default "waiting for the requester" reason.</summary>
    public const string AwaitingRequester = "awaiting-requester";

    /// <summary>Whether SLA clocks pause for this reason.</summary>
    [JsonIgnore]
    public bool Pauses => PausesSla ?? string.Equals(Key, AwaitingRequester, StringComparison.Ordinal);

    /// <summary>Most reasons a tenant can have.</summary>
    public const int MaxCount = 30;

    /// <summary>The default reasons.</summary>
    public static IReadOnlyList<OnHoldReason> Defaults { get; } =
    [
        new(AwaitingRequester, new("Waiting for the requester", "بانتظار ردّ مقدّم الطلب"), true, true),
        new("awaiting-supplier", new("Waiting for a supplier", "بانتظار المورّد"), true, false),
        new("awaiting-change", new("Waiting for a planned change", "بانتظار تغيير مخطط له"), true, false),
        new("scheduled", new("Scheduled for a later date", "مجدول لموعد لاحق"), true, false),
    ];

    /// <summary>Whether <paramref name="key"/> is a valid reason key.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>1–30 reasons with unique keys and names, at least one active.</summary>
    public static bool AreValid(IReadOnlyList<OnHoldReason>? reasons) =>
        reasons is { Count: > 0 and <= MaxCount }
        && reasons.All(r => r is not null && IsKey(r.Key) && r.Name is { IsValid: true })
        && reasons.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() == reasons.Count
        && reasons.Any(r => r.Active);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$")]
    private static partial Regex KeyPattern();
}

/// <summary>Ticket number prefix of each record type (product owner decision 2026-11-22: <c>INC-000123</c>).</summary>
public static partial class TicketNumbers
{
    /// <summary>Default prefixes.</summary>
    public static IReadOnlyDictionary<TicketType, string> DefaultPrefixes { get; } =
        new Dictionary<TicketType, string> { [TicketType.Incident] = "INC" };

    /// <summary>The number shown to people: prefix, a hyphen and at least six digits.</summary>
    public static string Format(string prefix, long sequence) =>
        $"{prefix}-{sequence.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>One prefix per record type, 1–10 capital letters or digits starting with a letter, all different.</summary>
    public static bool AreValid(IReadOnlyDictionary<TicketType, string>? prefixes) =>
        prefixes is not null
        && Enum.GetValues<TicketType>().All(t => prefixes.TryGetValue(t, out var p) && p is not null && PrefixPattern().IsMatch(p))
        && prefixes.Count == Enum.GetValues<TicketType>().Length
        && prefixes.Values.Distinct(StringComparer.Ordinal).Count() == prefixes.Count;

    /// <summary>
    /// Takes the next number of <paramref name="type"/> for the current tenant, inside the caller's
    /// transaction: the row lock makes concurrent requests wait, and a rollback gives the number back.
    /// </summary>
    public static async Task<long> NextAsync(ZelvoDbContext db, Guid tenantId, TicketType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var typeName = type.ToString();
        var values = await db.Database.SqlQuery<long>($"""
            INSERT INTO ticket_number_sequences (tenant_id, type, last_value)
            VALUES ({tenantId}, {typeName}, 1)
            ON CONFLICT (tenant_id, type) DO UPDATE SET last_value = ticket_number_sequences.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return values.Single();
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]{0,9}$")]
    private static partial Regex PrefixPattern();
}

/// <summary>Which files tickets accept (product owner decision 2026-11-22: 25 MB by default, an allowlist of file types).</summary>
/// <param name="MaxSizeMb">Largest file, in megabytes (1–<see cref="InstallationMaxSizeMb"/>).</param>
/// <param name="AllowedExtensions">File types accepted, from <see cref="FileTypes"/>.</param>
public sealed record AttachmentPolicy(int MaxSizeMb, IReadOnlyList<string> AllowedExtensions)
{
    /// <summary>Largest file any tenant can allow. The API's request size limit follows it.</summary>
    public const int InstallationMaxSizeMb = 100;

    /// <summary>25 MB; documents, images, text and email files.</summary>
    public static AttachmentPolicy Default { get; } = new(25,
        ["pdf", "png", "jpg", "jpeg", "gif", "webp", "txt", "csv", "log", "docx", "xlsx", "pptx", "odt", "ods", "eml", "msg", "zip"]);

    /// <summary>Largest file in bytes.</summary>
    public long MaxBytes => MaxSizeMb * 1024L * 1024L;

    /// <summary>Size within limits; at least one type, each one Zelvo knows how to check.</summary>
    public bool IsValid() =>
        MaxSizeMb is >= 1 and <= InstallationMaxSizeMb
        && AllowedExtensions is { Count: > 0 }
        && AllowedExtensions.All(e => e is not null && FileTypes.Find(e) is not null)
        && AllowedExtensions.Distinct(StringComparer.Ordinal).Count() == AllowedExtensions.Count;
}

/// <summary>
/// Setting keys and defaults owned by tickets. The API defines who may change them; the worker
/// reads <see cref="AutoCloseDaysKey"/>.
/// </summary>
public static class TicketSettingKeys
{
    /// <summary>The impact × urgency matrix.</summary>
    public const string PriorityMatrix = "incidents.priorityMatrix";

    /// <summary>On-hold reasons.</summary>
    public const string OnHoldReasons = "incidents.onHoldReasons";

    /// <summary>Number prefix per record type.</summary>
    public const string NumberPrefixes = "tickets.numberPrefixes";

    /// <summary>Days after which resolved tickets close.</summary>
    public const string AutoCloseDaysKey = "tickets.autoCloseDays";

    /// <summary>Whether the auto-close period counts working days of the tenant's default calendar (task B3).</summary>
    public const string AutoCloseBusinessDaysKey = "tickets.autoCloseBusinessDays";

    /// <summary>Accepted attachments.</summary>
    public const string AttachmentPolicyKey = "attachments.policy";

    /// <summary>Default auto-close period (product owner decision 2026-11-22): 5 calendar days.</summary>
    public const int DefaultAutoCloseDays = 5;

    /// <summary>Allowed auto-close periods, in days.</summary>
    public static bool IsAutoCloseDays(int days) => days is >= 1 and <= 365;

    /// <summary>The tenant's auto-close period, read straight from <c>tenant_settings</c> (worker).</summary>
    public static Task<int> ReadAutoCloseDaysAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        ReadAsync(db, AutoCloseDaysKey, DefaultAutoCloseDays, IsAutoCloseDays, cancellationToken);

    /// <summary>Whether auto-close counts working days, read straight from <c>tenant_settings</c> (worker).</summary>
    public static Task<bool> ReadAutoCloseBusinessDaysAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        ReadAsync(db, AutoCloseBusinessDaysKey, false, _ => true, cancellationToken);

    /// <summary>The tenant's on-hold reasons, read straight from <c>tenant_settings</c> (SLA clocks).</summary>
    public static Task<IReadOnlyList<OnHoldReason>> ReadOnHoldReasonsAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        ReadAsync(db, OnHoldReasons, OnHoldReason.Defaults, OnHoldReason.AreValid, cancellationToken);

    /// <summary>A tenant setting read straight from <c>tenant_settings</c>; the default when missing or not valid.</summary>
    public static async Task<T> ReadAsync<T>(ZelvoDbContext db, string key, T fallback, Func<T, bool> isValid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(isValid);
        var row = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return fallback;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(row.Value, Json);
            return value is not null && isValid(value) ? value : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>JSON options of tenant settings (matches the API's settings store).</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
