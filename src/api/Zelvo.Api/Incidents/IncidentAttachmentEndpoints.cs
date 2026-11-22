using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Incidents;

/// <summary>
/// Files attached to incidents (product owner decision 2026-11-22): the tenant's size limit and
/// file-type allowlist, content that must match its type, and a malware scan before anyone can
/// download the file. Uploads, downloads and removals are audited.
/// </summary>
internal static class IncidentAttachmentEndpoints
{
    // The tenant's limit is checked per file; this only stops oversized requests early.
    private const long RequestLimit = (AttachmentPolicy.InstallationMaxSizeMb + 1) * 1024L * 1024L;

    public static RouteGroupBuilder MapIncidentAttachmentEndpoints(this RouteGroupBuilder incidents)
    {
        incidents.MapGet("/{id:guid}/attachments", ListAsync)
            .WithName("ListIncidentAttachments")
            .WithSummary("Files attached to the incident, with their malware scan status. Only clean files (or, with scanning off, unscanned ones) can be downloaded.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<AttachmentResponse>>();

        incidents.MapPost("/{id:guid}/attachments", UploadAsync)
            .WithName("UploadIncidentAttachment")
            .WithSummary("Attach one file (multipart field 'file'). The requester or holders of incidents.edit; within the tenant's size limit and file types, and its content must match its type. The file is scanned for malware before anyone can download it; audited.")
            .RequireAuthorization()
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(RequestLimit))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<AttachmentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        incidents.MapGet("/{id:guid}/attachments/{attachmentId:guid}/content", DownloadAsync)
            .WithName("DownloadIncidentAttachment")
            .WithSummary("Download a file that was scanned clean. Files still being scanned answer 409 attachment-not-available; audited as a view.")
            .RequireAuthorization()
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");

        incidents.MapDelete("/{id:guid}/attachments/{attachmentId:guid}", DeleteAsync)
            .WithName("DeleteIncidentAttachment")
            .WithSummary("Remove a file: its uploader, or holders of incidents.edit, while the incident is not closed; audited.")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent);

        return incidents;
    }

    internal static async Task<Results<Ok<List<AttachmentResponse>>, ProblemHttpResult>> ListAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var attachments = await db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == id)
            .OrderBy(a => a.UploadedAt).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, attachments.Where(a => a.UploadedById is not null).Select(a => a.UploadedById!.Value), cancellationToken);
        return TypedResults.Ok(attachments.Select(a => AttachmentResponse.From(a, names, user, ticket)).ToList());
    }

    internal static async Task<Results<Created<AttachmentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        Guid id, IFormFile? file, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAttachmentStore store,
        IAuditLog audit, IOptions<AttachmentScanningOptions> scanning, CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        if (!user.Has(Permissions.IncidentsEdit) && ticket.RequesterId != user.Account!.Id)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You may not attach files to this incident.");
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        if (file is null || file.Length == 0)
        {
            return UserEndpoints.Validation("file", "Choose a file to attach.");
        }

        var policy = await settings.GetAsync(IncidentSettings.Attachments, cancellationToken);
        if (file.Length > policy.MaxBytes)
        {
            return Refused("attachment-too-large", $"Files can be at most {policy.MaxSizeMb} MB.");
        }

        var name = FileTypes.CleanFileName(file.FileName);
        var extension = FileTypes.ExtensionOf(name);
        var type = FileTypes.Find(extension);
        if (name.Length == 0 || type is null || !policy.AllowedExtensions.Contains(type.Extension, StringComparer.Ordinal))
        {
            return Refused("attachment-type-not-allowed", $"This type of file is not accepted. Accepted: {string.Join(", ", policy.AllowedExtensions)}.");
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        if (!type.Check(content))
        {
            return Refused("attachment-content-mismatch", "The file's content does not match its type.");
        }

        var attachment = new TicketAttachment
        {
            TicketId = ticket.Id,
            FileName = name,
            ContentType = type.ContentType,
            Size = content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            ScanStatus = scanning.Value.Enabled ? AttachmentScanStatus.Pending : AttachmentScanStatus.NotScanned,
            UploadedById = user.Account!.Id,
        };
        db.TicketAttachments.Add(attachment);
        store.Add(attachment.Id, content);
        audit.Add(AuditCategory.Change, "tickets.attachment.added", "ticket", ticket.Id.ToString(), Details(attachment));
        await db.SaveChangesAsync(cancellationToken);

        var names = await IncidentEndpoints.Names(db, [attachment.UploadedById.Value], cancellationToken);
        return TypedResults.Created($"/api/v1/incidents/{ticket.Id}/attachments/{attachment.Id}", AttachmentResponse.From(attachment, names, user, ticket));
    }

    internal static async Task<Results<FileContentHttpResult, ProblemHttpResult>> DownloadAsync(
        Guid id, Guid attachmentId, HttpContext http, ZelvoDbContext db, CurrentUser user, IAttachmentStore store,
        IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        var attachment = ticket is null ? null
            : await db.TicketAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == id, cancellationToken);
        if (attachment is null)
        {
            return AttachmentNotFound();
        }

        if (!IsDownloadable(attachment))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "attachment-not-available",
                attachment.ScanStatus == AttachmentScanStatus.Infected
                    ? "The file was rejected: malware was found in it."
                    : "The file is still being checked for malware. Try again in a moment.");
        }

        var content = await store.ReadAsync(attachment.Id, cancellationToken);
        if (content is null)
        {
            return AttachmentNotFound();
        }

        await audit.RecordAsync(AuditCategory.View, "tickets.attachment.downloaded", "ticket", id.ToString(), Details(attachment), cancellationToken);
        // Never shown inline: the browser saves it, and nothing in it can run in Zelvo's origin.
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.File(content, attachment.ContentType, attachment.FileName);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, Guid attachmentId, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        var attachment = ticket is null ? null
            : await db.TicketAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == id, cancellationToken);
        if (attachment is null)
        {
            return AttachmentNotFound();
        }

        if (!user.Has(Permissions.IncidentsEdit) && attachment.UploadedById != user.Account!.Id)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the person who attached a file, or staff, may remove it.");
        }

        if (ticket!.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        await store.RemoveAsync(attachment.Id, cancellationToken);
        db.TicketAttachments.Remove(attachment);
        audit.Add(AuditCategory.Change, "tickets.attachment.removed", "ticket", id.ToString(), Details(attachment));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static bool IsDownloadable(TicketAttachment attachment) =>
        attachment.ScanStatus is AttachmentScanStatus.Clean or AttachmentScanStatus.NotScanned;

    private static Dictionary<string, object?> Details(TicketAttachment attachment) => new(StringComparer.Ordinal)
    {
        ["attachmentId"] = attachment.Id.ToString(),
        ["fileName"] = attachment.FileName,
        ["size"] = attachment.Size,
        ["sha256"] = attachment.Sha256,
    };

    private static ProblemHttpResult Refused(string code, string title) =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, code, title);

    private static ProblemHttpResult AttachmentNotFound() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "attachment-not-found", "The file does not exist.");
}

/// <summary>A file attached to a ticket.</summary>
/// <param name="Id">Attachment id.</param>
/// <param name="FileName">File name.</param>
/// <param name="ContentType">Media type.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ScanStatus">Pending, Clean, Infected or NotScanned.</param>
/// <param name="Downloadable">Whether it can be downloaded now.</param>
/// <param name="CanDelete">Whether the caller may remove it.</param>
/// <param name="UploadedBy">Who attached it.</param>
/// <param name="UploadedAt">When.</param>
internal sealed record AttachmentResponse(
    Guid Id, string FileName, string ContentType, long Size, AttachmentScanStatus ScanStatus, bool Downloadable, bool CanDelete,
    PersonResponse? UploadedBy, DateTimeOffset UploadedAt)
{
    public static AttachmentResponse From(TicketAttachment a, Dictionary<Guid, string?> names, CurrentUser user, Ticket ticket) => new(
        a.Id, a.FileName, a.ContentType, a.Size, a.ScanStatus, IncidentAttachmentEndpoints.IsDownloadable(a),
        ticket.State != TicketState.Closed && (user.Has(Permissions.IncidentsEdit) || a.UploadedById == user.Account?.Id),
        a.UploadedById is { } uploader ? new PersonResponse(uploader, names.GetValueOrDefault(uploader)) : null,
        a.UploadedAt);
}
