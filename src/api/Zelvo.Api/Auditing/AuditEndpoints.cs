using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Security;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Auditing;

/// <summary>Reading and verifying the current tenant's audit log.</summary>
internal static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder group)
    {
        var audit = group.MapGroup("/audit").WithTags("Audit").RequireTenant()
            .RequireAuthorization(ZelvoPolicies.AuditRead);

        audit.MapGet("/events", ListAsync)
            .WithName("ListAuditEvents")
            .WithSummary("Audit log entries, newest first. Viewing them is itself audited.")
            .ProducesValidationProblem();

        audit.MapGet("/verification", VerifyAsync)
            .WithName("VerifyAuditChain")
            .WithSummary("Recomputes the hash chain and reports the first changed, removed or reordered entry.");

        return group;
    }

    internal static async Task<Ok<PageResult<AuditEventResponse>>> ListAsync(
        [AsParameters] PageQuery paging,
        [AsParameters] AuditEventFilter filter,
        ZelvoDbContext db,
        IAuditLog auditLog,
        CancellationToken cancellationToken)
    {
        var query = db.AuditEvents.AsNoTracking();
        if (filter.Category is { } category)
        {
            query = query.Where(e => e.Category == category);
        }

        if (!string.IsNullOrEmpty(filter.Action))
        {
            query = query.Where(e => e.Action == filter.Action);
        }

        if (!string.IsNullOrEmpty(filter.EntityType))
        {
            query = query.Where(e => e.EntityType == filter.EntityType);
        }

        if (!string.IsNullOrEmpty(filter.EntityId))
        {
            query = query.Where(e => e.EntityId == filter.EntityId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.OccurredAt < to);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var events = await query
            .OrderByDescending(e => e.Sequence)
            .Skip(paging.Skip)
            .Take(paging.Size)
            .ToListAsync(cancellationToken);

        await auditLog.RecordAsync(AuditCategory.View, "audit.events.viewed", "audit_event", details: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["filter"] = filter,
            ["page"] = paging.Number,
            ["pageSize"] = paging.Size,
            ["returned"] = events.Count,
        }, cancellationToken: cancellationToken);

        return TypedResults.Ok(new PageResult<AuditEventResponse>(
            events.Select(AuditEventResponse.From).ToList(), paging.Number, paging.Size, total));
    }

    internal static async Task<Ok<AuditVerificationResponse>> VerifyAsync(
        ZelvoDbContext db, ITenantContext tenantContext, IAuditLog auditLog, CancellationToken cancellationToken)
    {
        var result = await db.VerifyAuditChainAsync(tenantContext.TenantId!.Value, cancellationToken);
        await auditLog.RecordAsync(AuditCategory.Admin, "audit.chain.verified", details: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["valid"] = result.IsValid,
            ["eventsChecked"] = result.EventsChecked,
            ["firstInvalidSequence"] = result.FirstInvalidSequence,
        }, cancellationToken: cancellationToken);

        return TypedResults.Ok(new AuditVerificationResponse(
            result.IsValid,
            result.EventsChecked,
            result.FirstInvalidSequence,
            result.HeadSequence,
            result.HeadHash is null ? null : Convert.ToHexStringLower(result.HeadHash)));
    }
}

/// <summary>Filters for <c>GET /api/v1/audit/events</c>.</summary>
public sealed class AuditEventFilter
{
    /// <summary>Only entries of this category.</summary>
    public AuditCategory? Category { get; init; }

    /// <summary>Only entries with this action, for example <c>tenant.updated</c>.</summary>
    [StringLength(AuditEvent.TextMaxLength)]
    public string? Action { get; init; }

    /// <summary>Only entries about this record type.</summary>
    [StringLength(AuditEvent.TextMaxLength)]
    public string? EntityType { get; init; }

    /// <summary>Only entries about this record id.</summary>
    [StringLength(AuditEvent.TextMaxLength)]
    public string? EntityId { get; init; }

    /// <summary>Only entries at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only entries before this time.</summary>
    public DateTimeOffset? To { get; init; }
}

/// <summary>Who acted.</summary>
internal sealed record AuditActorResponse(AuditActorType Type, string? Id, string? Name);

/// <summary>One audit log entry.</summary>
internal sealed record AuditEventResponse(
    long Sequence,
    DateTimeOffset OccurredAt,
    AuditCategory Category,
    string Action,
    AuditActorResponse Actor,
    string? EntityType,
    string? EntityId,
    JsonElement? Changes,
    JsonElement? Metadata,
    string Hash,
    string? PreviousHash)
{
    public static AuditEventResponse From(AuditEvent e) => new(
        e.Sequence,
        e.OccurredAt,
        e.Category,
        e.Action,
        new AuditActorResponse(e.ActorType, e.ActorId, e.ActorName),
        e.EntityType,
        e.EntityId,
        Parse(e.Changes),
        Parse(e.Metadata),
        Convert.ToHexStringLower(e.Hash ?? []),
        e.PreviousHash is null ? null : Convert.ToHexStringLower(e.PreviousHash));

    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Result of an audit chain check.</summary>
/// <param name="Valid">No entry was changed, removed or reordered.</param>
/// <param name="EventsChecked">Intact entries from the start of the chain.</param>
/// <param name="FirstInvalidSequence">First broken entry, when not valid.</param>
/// <param name="HeadSequence">Newest entry, when valid.</param>
/// <param name="HeadHash">Hash of the newest entry (hex). Keep a copy outside Zelvo to detect removal of the newest entries later.</param>
internal sealed record AuditVerificationResponse(bool Valid, long EventsChecked, long? FirstInvalidSequence, long? HeadSequence, string? HeadHash);
