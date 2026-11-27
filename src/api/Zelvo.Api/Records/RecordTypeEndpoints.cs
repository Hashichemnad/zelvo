using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Records;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Records;

/// <summary>
/// Settings → Record types (module 20, task K2; product owner decision 20 of 2026-11-26): custom
/// record types on the shared ticket core, each with a bilingual name, its own number prefix and
/// series, its states mapped onto the core's state categories, a portal entry and the permissions
/// made for it (<c>records.&lt;key&gt;.read</c>, <c>.edit</c>, <c>.assign</c>), granted to the tenant
/// administrators and the roles chosen when it is made.
/// </summary>
internal static partial class RecordTypeEndpoints
{
    /// <summary>Most record types per tenant.</summary>
    public const int MaxTypes = 50;

    public static RouteGroupBuilder MapRecordTypeEndpoints(this RouteGroupBuilder group)
    {
        var types = group.MapGroup("/record-types").WithTags("Record types").RequireTenant();
        types.MapGet("/", ListAsync).WithName("ListRecordTypes")
            .WithSummary("The tenant's custom record types with what the caller may do with each. Signed-in people see the active ones they may raise or read; holders of recordtypes.manage see all.")
            .RequireAuthorization().Produces<IReadOnlyList<RecordTypeResponse>>();
        types.MapGet("/{key}", GetAsync).WithName("GetRecordType")
            .WithSummary("One record type, by key. Returns an ETag to send as If-Match with changes.")
            .RequireAuthorization().Produces<RecordTypeResponse>();
        types.MapPost("/", CreateAsync).WithName("CreateRecordType")
            .WithSummary("Make a record type: key (never changes), bilingual name and description, number prefix (different from every other record type's), states mapped onto New, InProgress, OnHold, Resolved and Closed (3–20, at least one New, Resolved and Closed), portal entry, default priority, and the roles to grant its read, edit and assign permissions (tenant administrators always get them). Needs recordtypes.manage; audited.")
            .RequirePermission(Permissions.RecordTypesManage).Produces<RecordTypeResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        types.MapPatch("/{key}", UpdateAsync).WithName("UpdateRecordType")
            .WithSummary("Change a record type: names, description, prefix (new numbers only), states (states in use keep their category and cannot be removed), portal entry, default priority, active. Needs recordtypes.manage; audited.")
            .RequirePermission(Permissions.RecordTypesManage).Produces<RecordTypeResponse>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var manage = user.Has(Permissions.RecordTypesManage);
        var types = await db.RecordTypes.AsNoTracking().OrderBy(t => t.Key).ToListAsync(cancellationToken);
        var used = await UsedStatesAsync(db, null, cancellationToken);
        return TypedResults.Ok(types
            .Where(t => manage || (t.Active && (t.PortalEntry || RecordAccess.Of(user, t).Read || RecordAccess.Of(user, t).Edit)))
            .Select(t => Response(t, user, used.GetValueOrDefault(t.Id) ?? [])).ToList());
    }

    internal static async Task<IResult> GetAsync(string key, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var type = await db.RecordTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Key == key, cancellationToken);
        if (type is null || (!user.Has(Permissions.RecordTypesManage) && !type.Active && !RecordAccess.Of(user, type).Read))
        {
            return FormsCommon.NotFound("record-type");
        }

        http.Response.Headers.ETag = $"\"{type.Version}\"";
        var used = await UsedStatesAsync(db, type.Id, cancellationToken);
        return TypedResults.Ok(Response(type, user, used.GetValueOrDefault(type.Id) ?? []));
    }

    internal static async Task<IResult> CreateAsync(
        CreateRecordTypeRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAuditLog audit, CancellationToken cancellationToken)
    {
        var key = request.Key?.Trim() ?? "";
        if (!KeyPattern().IsMatch(key))
        {
            return UserEndpoints.Validation("key", "Give a key of lower-case letters, digits and hyphens, starting with a letter, up to 40 characters.");
        }

        if (await db.RecordTypes.CountAsync(cancellationToken) >= MaxTypes)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "too-many", $"A tenant has at most {MaxTypes} record types.");
        }

        if (await db.RecordTypes.AnyAsync(t => t.Key == key, cancellationToken))
        {
            return FormsCommon.KeyTaken();
        }

        var name = request.Name?.ToValue() ?? new LocalizedText(null, null);
        if (!DirectoryProblems.Fits(name, RecordType.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {RecordType.NameMaxLength} characters in each language.");
        }

        var (states, problem) = Parse(request.States, []);
        if (problem is not null)
        {
            return problem;
        }

        var prefix = request.NumberPrefix?.Trim().ToUpperInvariant() ?? "";
        if (await PrefixProblemAsync(db, settings, prefix, null, cancellationToken) is { } prefixProblem)
        {
            return prefixProblem;
        }

        var roleKeys = (request.GrantRoles ?? [BuiltInRoles.Agent, BuiltInRoles.TeamLead]).Append(BuiltInRoles.TenantAdmin).Distinct(StringComparer.Ordinal).ToList();
        var roles = await db.Roles.Include(r => r.Permissions).Where(r => roleKeys.Contains(r.Key)).ToListAsync(cancellationToken);
        if (roles.Count != roleKeys.Count)
        {
            return UserEndpoints.Validation("grantRoles", "Every role must exist.");
        }

        var type = new RecordType
        {
            Key = key,
            Name = name,
            Description = request.Description?.ToValue() ?? new LocalizedText(null, null),
            NumberPrefix = prefix,
            States = RecordStates.Write(states!),
            PortalEntry = request.PortalEntry ?? false,
            DefaultPriority = request.DefaultPriority ?? 3,
        };
        db.RecordTypes.Add(type);
        foreach (var role in roles)
        {
            foreach (var permission in new[] { RecordPermissions.Read(key), RecordPermissions.Edit(key), RecordPermissions.Assign(key) })
            {
                if (role.Permissions.All(p => p.Permission != permission))
                {
                    role.Permissions.Add(new RoleGrant { Permission = permission });
                }
            }

            role.UpdatedAt = DateTimeOffset.UtcNow;
        }

        audit.Add(AuditCategory.Admin, "records.type.created", "record_type", type.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["grantedTo"] = string.Join(",", roleKeys),
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/record-types/{key}", Response(type, user, []));
    }

    internal static async Task<IResult> UpdateAsync(
        string key, UpdateRecordTypeRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var type = await db.RecordTypes.SingleOrDefaultAsync(t => t.Key == key, cancellationToken);
        if (type is null)
        {
            return FormsCommon.NotFound("record-type");
        }

        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (ifMatch.Length > 0)
        {
            if (!uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != type.Version)
            {
                return FormsCommon.VersionMismatch();
            }

            db.Entry(type).Property(t => t.Version).OriginalValue = version;
        }

        var used = (await UsedStatesAsync(db, type.Id, cancellationToken)).GetValueOrDefault(type.Id) ?? [];
        if (request.States is not null)
        {
            var current = RecordStates.Read(type.States);
            var (states, problem) = Parse(request.States, used);
            if (problem is not null)
            {
                return problem;
            }

            // A state in use keeps its category: the records in it stay where they are on the core.
            var moved = used.Where(k => current.FirstOrDefault(s => s.Key == k)?.Category is { } before && states!.First(s => s.Key == k).Category != before).ToList();
            if (moved.Count > 0)
            {
                return UserEndpoints.Validation("states", $"States in use keep their category: {string.Join(", ", moved)}.");
            }

            type.States = RecordStates.Write(states!);
        }

        if (request.NumberPrefix is { } prefixInput)
        {
            var prefix = prefixInput.Trim().ToUpperInvariant();
            if (prefix != type.NumberPrefix && await PrefixProblemAsync(db, settings, prefix, type.Id, cancellationToken) is { } prefixProblem)
            {
                return prefixProblem;
            }

            type.NumberPrefix = prefix;
        }

        if (request.Name is { } name)
        {
            if (!DirectoryProblems.Fits(name.ToValue(), RecordType.NameMaxLength))
            {
                return UserEndpoints.Validation("name", $"Use at most {RecordType.NameMaxLength} characters in each language.");
            }

            type.Name = name.ToValue();
        }

        if (request.Description is { } description)
        {
            type.Description = description.ToValue();
        }

        type.PortalEntry = request.PortalEntry ?? type.PortalEntry;
        type.DefaultPriority = request.DefaultPriority ?? type.DefaultPriority;
        type.Active = request.Active ?? type.Active;
        type.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var saved = await db.RecordTypes.AsNoTracking().SingleAsync(t => t.Id == type.Id, cancellationToken);
        http.Response.Headers.ETag = $"\"{saved.Version}\"";
        return TypedResults.Ok(Response(saved, user, used));
    }

    /// <summary>The states of the request, or why they are refused; states in use cannot be left out.</summary>
    private static (List<RecordState>? States, IResult? Problem) Parse(IReadOnlyList<RecordStateInput>? input, IReadOnlyCollection<string> used)
    {
        var states = (input ?? []).Select(s => new RecordState(s.Key?.Trim() ?? "", new SettingText(Trim(s.Name?.En), Trim(s.Name?.Ar)), s.Category ?? (RecordStateCategory)(-1))).ToList();
        if (RecordStates.Problem(states) is { } problem)
        {
            return (null, UserEndpoints.Validation("states", problem));
        }

        var missing = used.Where(k => states.All(s => s.Key != k)).ToList();
        return missing.Count > 0
            ? (null, UserEndpoints.Validation("states", $"States in use cannot be removed: {string.Join(", ", missing)}."))
            : (states, null);
    }

    /// <summary>A prefix must be valid and different from the built-in record types' and every other custom type's.</summary>
    private static async Task<IResult?> PrefixProblemAsync(ZelvoDbContext db, TenantSettingsStore settings, string prefix, Guid? except, CancellationToken cancellationToken)
    {
        if (!PrefixPattern().IsMatch(prefix))
        {
            return UserEndpoints.Validation("numberPrefix", "Give 1–10 capital letters or digits starting with a letter.");
        }

        var builtIn = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken);
        var taken = TicketNumbers.BuiltInTypes.Select(t => TicketNumbers.PrefixOf(builtIn, t)).Contains(prefix, StringComparer.Ordinal)
            || await db.RecordTypes.AnyAsync(t => t.NumberPrefix == prefix && t.Id != except, cancellationToken);
        return taken ? ApiProblem.Create(StatusCodes.Status409Conflict, "prefix-taken", "Another record type already uses this prefix.") : null;
    }

    /// <summary>State keys used by records, per type.</summary>
    private static async Task<Dictionary<Guid, List<string>>> UsedStatesAsync(ZelvoDbContext db, Guid? typeId, CancellationToken cancellationToken) =>
        (await db.CustomRecords.AsNoTracking().Where(r => typeId == null || r.RecordTypeId == typeId)
            .Select(r => new { r.RecordTypeId, r.StateKey }).Distinct().ToListAsync(cancellationToken))
        .GroupBy(r => r.RecordTypeId).ToDictionary(g => g.Key, g => g.Select(r => r.StateKey).ToList());

    internal static RecordTypeResponse Response(RecordType t, CurrentUser user, IReadOnlyCollection<string> used)
    {
        var access = RecordAccess.Of(user, t);
        return new RecordTypeResponse(
            t.Id, t.Key, LocalizedTextDto.From(t.Name), LocalizedTextDto.From(t.Description), t.NumberPrefix,
            [.. RecordStates.Read(t.States).Select(s => new RecordStateResponse(s.Key, LocalizedTextDto.From(s.Name.ToText()), s.Category, used.Contains(s.Key)))],
            t.PortalEntry, t.DefaultPriority, t.Active,
            new RecordAccessResponse(access.Read, access.Edit, access.Assign, t.Active && (t.PortalEntry || access.Edit)),
            $"\"{t.Version}\"", t.CreatedAt, t.UpdatedAt);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9]{0,9}$")]
    private static partial Regex PrefixPattern();
}

/// <summary>What a person may do with records of a type.</summary>
internal readonly record struct RecordAccess(bool Read, bool Edit, bool Assign)
{
    public static RecordAccess Of(CurrentUser user, RecordType type) =>
        new(user.Has(RecordPermissions.Read(type.Key)), user.Has(RecordPermissions.Edit(type.Key)), user.Has(RecordPermissions.Assign(type.Key)));
}

/// <summary>A state of a record type; <c>inUse</c> when records are in it.</summary>
internal sealed record RecordStateResponse(string Key, LocalizedTextDto Name, RecordStateCategory Category, bool InUse);

/// <summary>What the caller may do with records of the type; <c>raise</c>: may make new ones.</summary>
internal sealed record RecordAccessResponse(bool Read, bool Edit, bool Assign, bool Raise);

/// <summary>A custom record type.</summary>
internal sealed record RecordTypeResponse(
    Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, string NumberPrefix, IReadOnlyList<RecordStateResponse> States,
    bool PortalEntry, int DefaultPriority, bool Active, RecordAccessResponse Access, string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A state of a record type.</summary>
public sealed class RecordStateInput
{
    /// <summary>Key: lower-case letters, digits and hyphens.</summary>
    [StringLength(40)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public Voice.TextInput? Name { get; init; }

    /// <summary>The core's state category it is mapped onto.</summary>
    public RecordStateCategory? Category { get; init; }
}

/// <summary>A new record type.</summary>
public sealed class CreateRecordTypeRequest
{
    /// <summary>Key in addresses and permissions, for example <c>facilities</c>.</summary>
    [Required]
    [StringLength(RecordType.KeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What it is for.</summary>
    public RecordTextInput? Description { get; init; }

    /// <summary>Number prefix, for example <c>FAC</c>.</summary>
    [Required]
    [StringLength(10)]
    public string? NumberPrefix { get; init; }

    /// <summary>The states, in order.</summary>
    [Required]
    [MaxLength(RecordStates.MaxStates)]
    public IReadOnlyList<RecordStateInput>? States { get; init; }

    /// <summary>Requesters raise them in the portal.</summary>
    public bool? PortalEntry { get; init; }

    /// <summary>Priority of new records, 1–5 (default 3).</summary>
    [Range(1, 5)]
    public int? DefaultPriority { get; init; }

    /// <summary>Roles that get the type's read, edit and assign permissions (default agents and team leads; tenant administrators always).</summary>
    [MaxLength(20)]
    public IReadOnlyList<string>? GrantRoles { get; init; }
}

/// <summary>Changes to a record type.</summary>
public sealed class UpdateRecordTypeRequest
{
    /// <summary>Name in English and Arabic.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What it is for.</summary>
    public RecordTextInput? Description { get; init; }

    /// <summary>Number prefix for new numbers.</summary>
    [StringLength(10)]
    public string? NumberPrefix { get; init; }

    /// <summary>The states, in order (replaces the list).</summary>
    [MaxLength(RecordStates.MaxStates)]
    public IReadOnlyList<RecordStateInput>? States { get; init; }

    /// <summary>Requesters raise them in the portal.</summary>
    public bool? PortalEntry { get; init; }

    /// <summary>Priority of new records, 1–5.</summary>
    [Range(1, 5)]
    public int? DefaultPriority { get; init; }

    /// <summary>New records can be raised.</summary>
    public bool? Active { get; init; }
}

/// <summary>Text in English and Arabic, up to 1,000 characters each (either may be empty).</summary>
public sealed class RecordTextInput
{
    /// <summary>English.</summary>
    [StringLength(RecordType.DescriptionMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(RecordType.DescriptionMaxLength)]
    public string? Ar { get; init; }

    /// <summary>The value.</summary>
    public LocalizedText ToValue() => new(string.IsNullOrWhiteSpace(En) ? null : En.Trim(), string.IsNullOrWhiteSpace(Ar) ? null : Ar.Trim());
}
