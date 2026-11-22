using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Incidents;

/// <summary>Tenant settings of incidents and attachments (module 1), changed by administrators in Settings → Incidents.</summary>
internal static class IncidentSettings
{
    public static SettingDefinition<PriorityMatrix> PriorityMatrix { get; } =
        new(TicketSettingKeys.PriorityMatrix, Tickets.PriorityMatrix.Default, Permissions.SettingsManage, m => m.IsValid());

    public static SettingDefinition<IReadOnlyList<OnHoldReason>> OnHoldReasons { get; } =
        new(TicketSettingKeys.OnHoldReasons, OnHoldReason.Defaults, Permissions.SettingsManage, OnHoldReason.AreValid);

    public static SettingDefinition<IReadOnlyDictionary<TicketType, string>> NumberPrefixes { get; } =
        new(TicketSettingKeys.NumberPrefixes, TicketNumbers.DefaultPrefixes, Permissions.SettingsManage, TicketNumbers.AreValid);

    public static SettingDefinition<int> AutoCloseDays { get; } =
        new(TicketSettingKeys.AutoCloseDaysKey, TicketSettingKeys.DefaultAutoCloseDays, Permissions.SettingsManage, TicketSettingKeys.IsAutoCloseDays);

    public static SettingDefinition<AttachmentPolicy> Attachments { get; } =
        new(TicketSettingKeys.AttachmentPolicyKey, AttachmentPolicy.Default, Permissions.SettingsManage, p => p.IsValid());

    public static IReadOnlyList<SettingDefinition> IncidentKeys { get; } = [PriorityMatrix, OnHoldReasons, NumberPrefixes, AutoCloseDays];

    public static RouteGroupBuilder MapIncidentSettingsEndpoints(this RouteGroupBuilder group)
    {
        var incidents = group.MapGroup("/settings/incidents").WithTags("Settings").RequireTenant();
        incidents.MapGet("/", GetAsync)
            .WithName("GetIncidentSettings")
            .WithSummary("The tenant's impact × urgency matrix, on-hold reasons, number prefixes and auto-close period. Every signed-in user can read them (screens show their names).")
            .RequireAuthorization();
        incidents.MapPut("/", UpdateAsync)
            .WithName("UpdateIncidentSettings")
            .WithSummary("Change the matrix (3 impact × 3 urgency levels, 5 priorities), on-hold reasons (reasons in use can be made inactive but not removed), number prefixes and auto-close period (1–365 days). Needs settings.manage; audited. New priorities apply to incidents as they change.")
            .RequirePermission(Permissions.SettingsManage)
            .ProducesValidationProblem();

        var attachments = group.MapGroup("/settings/attachments").WithTags("Settings").RequireTenant();
        attachments.MapGet("/", GetAttachmentsAsync)
            .WithName("GetAttachmentSettings")
            .WithSummary("Largest file and accepted file types for attachments, the file types Zelvo can check, and whether malware scanning is on.")
            .RequireAuthorization();
        attachments.MapPut("/", UpdateAttachmentsAsync)
            .WithName("UpdateAttachmentSettings")
            .WithSummary("Change the largest file (1–100 MB) and the accepted file types. Needs settings.manage; audited.")
            .RequirePermission(Permissions.SettingsManage)
            .ProducesValidationProblem();
        return group;
    }

    internal static async Task<Ok<IncidentSettingsResponse>> GetAsync(TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await IncidentSettingsResponse.LoadAsync(store, cancellationToken));

    internal static async Task<Results<Ok<IncidentSettingsResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        UpdateIncidentSettingsRequest request, TenantSettingsStore store, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var matrix = request.PriorityMatrix!.ToValue();
        if (!matrix.IsValid())
        {
            return UserEndpoints.Validation("priorityMatrix", "Name 3 impacts, 3 urgencies and 5 priorities (English or Arabic, up to 100 characters), and give each of the 3 × 3 combinations a priority from 1 to 5.");
        }

        var reasons = request.OnHoldReasons!.Select(r => r.ToValue()).ToList();
        if (!OnHoldReason.AreValid(reasons))
        {
            return UserEndpoints.Validation("onHoldReasons", "Give 1–30 reasons with unique keys (lower-case letters, digits and hyphens) and names; at least one must be active.");
        }

        var prefixes = new Dictionary<TicketType, string>();
        foreach (var (key, value) in request.NumberPrefixes!)
        {
            if (!Enum.TryParse<TicketType>(key, ignoreCase: false, out var type) || !Enum.IsDefined(type))
            {
                return UserEndpoints.Validation("numberPrefixes", $"Unknown record type '{key}'.");
            }

            prefixes[type] = value;
        }

        if (!TicketNumbers.AreValid(prefixes))
        {
            return UserEndpoints.Validation("numberPrefixes", "Give every record type a different prefix of 1–10 capital letters or digits, starting with a letter.");
        }

        if (!TicketSettingKeys.IsAutoCloseDays(request.AutoCloseDays!.Value))
        {
            return UserEndpoints.Validation("autoCloseDays", "Use 1 to 365 days.");
        }

        var removed = (await store.GetAsync(OnHoldReasons, cancellationToken)).Select(r => r.Key)
            .Except(reasons.Select(r => r.Key), StringComparer.Ordinal).ToList();
        if (removed.Count > 0 && await db.Tickets.AnyAsync(t => t.OnHoldReason != null && removed.Contains(t.OnHoldReason), cancellationToken))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "on-hold-reason-in-use",
                "Tickets on hold use a reason you removed. Make it inactive instead.");
        }

        await store.SetAsync(PriorityMatrix, matrix, cancellationToken);
        await store.SetAsync<IReadOnlyList<OnHoldReason>>(OnHoldReasons, reasons, cancellationToken);
        await store.SetAsync<IReadOnlyDictionary<TicketType, string>>(NumberPrefixes, prefixes, cancellationToken);
        await store.SetAsync(AutoCloseDays, request.AutoCloseDays.Value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await IncidentSettingsResponse.LoadAsync(store, cancellationToken));
    }

    internal static async Task<Ok<AttachmentSettingsResponse>> GetAttachmentsAsync(
        TenantSettingsStore store, IOptions<AttachmentScanningOptions> scanning, CancellationToken cancellationToken) =>
        TypedResults.Ok(await AttachmentSettingsResponse.LoadAsync(store, scanning.Value, cancellationToken));

    internal static async Task<Results<Ok<AttachmentSettingsResponse>, ValidationProblem>> UpdateAttachmentsAsync(
        UpdateAttachmentSettingsRequest request, TenantSettingsStore store, ZelvoDbContext db,
        IOptions<AttachmentScanningOptions> scanning, CancellationToken cancellationToken)
    {
        var policy = new AttachmentPolicy(request.MaxSizeMb!.Value,
            [.. request.AllowedExtensions!.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal)]);
        if (!policy.IsValid())
        {
            return UserEndpoints.Validation("allowedExtensions", $"Use 1–{AttachmentPolicy.InstallationMaxSizeMb} MB and at least one of the file types Zelvo can check.");
        }

        await store.SetAsync(Attachments, policy, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await AttachmentSettingsResponse.LoadAsync(store, scanning.Value, cancellationToken));
    }
}

/// <summary>A bilingual name in settings requests and responses.</summary>
public sealed class SettingTextInput
{
    /// <summary>English, up to 100 characters.</summary>
    [StringLength(SettingText.MaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic, up to 100 characters.</summary>
    [StringLength(SettingText.MaxLength)]
    public string? Ar { get; init; }

    internal SettingText ToValue() => new(Blank(En), Blank(Ar));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The impact × urgency matrix.</summary>
public sealed class PriorityMatrixInput
{
    /// <summary>Names of impact levels 1–3 (1 is the highest).</summary>
    [Required]
    public IReadOnlyList<SettingTextInput>? Impacts { get; init; }

    /// <summary>Names of urgency levels 1–3.</summary>
    [Required]
    public IReadOnlyList<SettingTextInput>? Urgencies { get; init; }

    /// <summary>Names of priorities 1–5.</summary>
    [Required]
    public IReadOnlyList<SettingTextInput>? Priorities { get; init; }

    /// <summary>Priority of each impact (row) and urgency (column).</summary>
    [Required]
    public IReadOnlyList<IReadOnlyList<int>>? Matrix { get; init; }

    internal PriorityMatrix ToValue() => new(
        [.. (Impacts ?? []).Select(n => n?.ToValue() ?? new SettingText(null, null))],
        [.. (Urgencies ?? []).Select(n => n?.ToValue() ?? new SettingText(null, null))],
        [.. (Priorities ?? []).Select(n => n?.ToValue() ?? new SettingText(null, null))],
        Matrix ?? []);
}

/// <summary>An on-hold reason.</summary>
public sealed class OnHoldReasonInput
{
    /// <summary>Stable key: lower-case letters, digits and hyphens.</summary>
    [Required]
    [StringLength(Ticket.ReasonKeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public SettingTextInput? Name { get; init; }

    /// <summary>Whether agents can choose it.</summary>
    public bool Active { get; init; } = true;

    internal OnHoldReason ToValue() => new(Key ?? string.Empty, Name?.ToValue() ?? new SettingText(null, null), Active);
}

/// <summary>New incident settings. Every field is required.</summary>
public sealed class UpdateIncidentSettingsRequest
{
    /// <summary>The impact × urgency matrix.</summary>
    [Required]
    public PriorityMatrixInput? PriorityMatrix { get; init; }

    /// <summary>On-hold reasons.</summary>
    [Required]
    [MaxLength(OnHoldReason.MaxCount)]
    public IReadOnlyList<OnHoldReasonInput>? OnHoldReasons { get; init; }

    /// <summary>Number prefix of each record type, for example <c>{"Incident": "INC"}</c>.</summary>
    [Required]
    public IReadOnlyDictionary<string, string>? NumberPrefixes { get; init; }

    /// <summary>Days after which resolved tickets close, 1–365.</summary>
    [Required]
    public int? AutoCloseDays { get; init; }
}

/// <summary>A bilingual name.</summary>
internal sealed record NameResponse(int Level, LocalizedTextDto Name);

/// <summary>An on-hold reason.</summary>
internal sealed record OnHoldReasonResponse(string Key, LocalizedTextDto Name, bool Active);

/// <summary>The matrix: names of each level and the priority of each impact (row) and urgency (column).</summary>
internal sealed record PriorityMatrixResponse(
    IReadOnlyList<NameResponse> Impacts, IReadOnlyList<NameResponse> Urgencies, IReadOnlyList<NameResponse> Priorities,
    IReadOnlyList<IReadOnlyList<int>> Matrix)
{
    public static PriorityMatrixResponse From(PriorityMatrix matrix) => new(
        Names(matrix.Impacts), Names(matrix.Urgencies), Names(matrix.Priorities), matrix.Matrix);

    private static NameResponse[] Names(IReadOnlyList<SettingText> names) =>
        [.. names.Select((n, i) => new NameResponse(i + 1, LocalizedTextDto.From(n.ToText())))];
}

/// <summary>The tenant's incident settings.</summary>
internal sealed record IncidentSettingsResponse(
    PriorityMatrixResponse PriorityMatrix,
    IReadOnlyList<OnHoldReasonResponse> OnHoldReasons,
    IReadOnlyDictionary<string, string> NumberPrefixes,
    int AutoCloseDays,
    DateTimeOffset? UpdatedAt)
{
    public static async Task<IncidentSettingsResponse> LoadAsync(TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var matrix = await store.GetAsync(IncidentSettings.PriorityMatrix, cancellationToken).ConfigureAwait(false);
        var reasons = await store.GetAsync(IncidentSettings.OnHoldReasons, cancellationToken).ConfigureAwait(false);
        var prefixes = await store.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var days = await store.GetAsync(IncidentSettings.AutoCloseDays, cancellationToken).ConfigureAwait(false);
        return new(
            PriorityMatrixResponse.From(matrix),
            [.. reasons.Select(r => new OnHoldReasonResponse(r.Key, LocalizedTextDto.From(r.Name.ToText()), r.Active))],
            prefixes.ToDictionary(p => p.Key.ToString(), p => p.Value, StringComparer.Ordinal),
            days,
            await store.LastChangedAsync(IncidentSettings.IncidentKeys, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>New attachment settings.</summary>
public sealed class UpdateAttachmentSettingsRequest
{
    /// <summary>Largest file in megabytes, 1–100.</summary>
    [Required]
    [Range(1, AttachmentPolicy.InstallationMaxSizeMb)]
    public int? MaxSizeMb { get; init; }

    /// <summary>Accepted file types (extensions without the dot), from <c>availableExtensions</c>.</summary>
    [Required]
    [MaxLength(50)]
    public IReadOnlyList<string>? AllowedExtensions { get; init; }
}

/// <summary>The tenant's attachment settings.</summary>
/// <param name="MaxSizeMb">Largest file, in megabytes.</param>
/// <param name="AllowedExtensions">Accepted file types.</param>
/// <param name="AvailableExtensions">File types Zelvo can check, which an administrator can allow.</param>
/// <param name="ScanningEnabled">Whether files are scanned for malware before anyone can open them.</param>
/// <param name="UpdatedAt">Last change; null while the defaults apply.</param>
internal sealed record AttachmentSettingsResponse(
    int MaxSizeMb, IReadOnlyList<string> AllowedExtensions, IReadOnlyList<string> AvailableExtensions, bool ScanningEnabled, DateTimeOffset? UpdatedAt)
{
    public static async Task<AttachmentSettingsResponse> LoadAsync(TenantSettingsStore store, AttachmentScanningOptions scanning, CancellationToken cancellationToken)
    {
        var policy = await store.GetAsync(IncidentSettings.Attachments, cancellationToken).ConfigureAwait(false);
        return new(policy.MaxSizeMb, policy.AllowedExtensions, [.. FileTypes.All.Select(t => t.Extension)], scanning.Enabled,
            await store.LastChangedAsync([IncidentSettings.Attachments], cancellationToken).ConfigureAwait(false));
    }
}
