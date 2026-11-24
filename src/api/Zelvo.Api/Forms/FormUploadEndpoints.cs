using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Forms;

/// <summary>
/// Files for form fields (task C1): file upload answers and drawn signatures are uploaded while the
/// form is filled, before the ticket exists. Each upload follows the tenant's attachment rules
/// (size, types, content matching the type) and is scanned for malware like any attachment. It
/// belongs only to its uploader until the form is sent, when it becomes the ticket's; unsent uploads
/// are removed after a day. Audited.
/// </summary>
internal static partial class FormUploadEndpoints
{
    /// <summary>Most unsent uploads one person may hold.</summary>
    public const int MaxPendingPerUser = 50;

    private const long RequestLimit = (AttachmentPolicy.InstallationMaxSizeMb + 1) * 1024L * 1024L;

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldKey();

    public static RouteGroupBuilder MapFormUploadEndpoints(this RouteGroupBuilder group)
    {
        var uploads = group.MapGroup("/form-uploads").WithTags("Forms").RequireTenant();
        uploads.MapPost("/", UploadAsync).WithName("UploadFormFile")
            .WithSummary("Upload one file for a form field (multipart fields 'file' and 'field', the field key). Within the tenant's size limit and file types, and its content must match its type; scanned for malware. Send its id as the field's answer. Signed-in people only; audited.")
            .RequirePerson()
            .RequireAuthorization()
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(RequestLimit))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<FormUploadResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        uploads.MapGet("/{id:guid}", GetAsync).WithName("GetFormFile")
            .WithSummary("One of your unsent uploads, with its malware scan status.")
            .RequirePerson().RequireAuthorization().Produces<FormUploadResponse>();
        uploads.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteFormFile")
            .WithSummary("Remove one of your unsent uploads; audited.")
            .RequirePerson().RequireAuthorization().Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<Results<Created<FormUploadResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        [FromForm] string? field, IFormFile? file, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAttachmentStore store,
        IOptions<AttachmentScanningOptions> scanning, CancellationToken cancellationToken)
    {
        if (field is null || !FieldKey().IsMatch(field))
        {
            return UserEndpoints.Validation("field", "Give the key of the form field.");
        }

        if (file is null || file.Length == 0)
        {
            return UserEndpoints.Validation("file", "Choose a file to upload.");
        }

        var me = user.Account!.Id;
        if (await db.TicketAttachments.CountAsync(a => a.TicketId == null && a.ArticleId == null && a.ServiceId == null && a.BrandingSlot == null && a.UploadedById == me, cancellationToken) >= MaxPendingPerUser)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "too-many-uploads",
                $"You have {MaxPendingPerUser} files waiting to be sent with a form; send or remove some first.");
        }

        var policy = await settings.GetAsync(IncidentSettings.Attachments, cancellationToken);
        if (file.Length > policy.MaxBytes)
        {
            return Refused("attachment-too-large", $"Files can be at most {policy.MaxSizeMb} MB.");
        }

        var name = FileTypes.CleanFileName(file.FileName);
        var type = FileTypes.Find(FileTypes.ExtensionOf(name));
        // A drawn signature is always a PNG, even when the tenant does not accept PNG attachments.
        var signatureImage = type?.Extension == "png";
        if (name.Length == 0 || type is null || (!policy.AllowedExtensions.Contains(type.Extension, StringComparer.Ordinal) && !signatureImage))
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
            FormFieldKey = field,
            FileName = name,
            ContentType = type.ContentType,
            Size = content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            ScanStatus = scanning.Value.Enabled ? AttachmentScanStatus.Pending : AttachmentScanStatus.NotScanned,
            UploadedById = me,
        };
        db.TicketAttachments.Add(attachment);
        store.Add(attachment.Id, content);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/form-uploads/{attachment.Id}", FormUploadResponse.From(attachment));
    }

    internal static async Task<Results<Ok<FormUploadResponse>, ProblemHttpResult>> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var upload = await FindAsync(db, user, id, cancellationToken);
        return upload is null ? FormsCommon.NotFound("form-upload") : TypedResults.Ok(FormUploadResponse.From(upload));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, CancellationToken cancellationToken)
    {
        var upload = await FindAsync(db, user, id, cancellationToken);
        if (upload is null)
        {
            return FormsCommon.NotFound("form-upload");
        }

        await store.RemoveAsync(upload.Id, cancellationToken);
        db.TicketAttachments.Remove(upload);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Task<TicketAttachment?> FindAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken) =>
        db.TicketAttachments.SingleOrDefaultAsync(a => a.Id == id && a.TicketId == null && a.ArticleId == null && a.ServiceId == null && a.BrandingSlot == null && a.UploadedById == user.Account!.Id, cancellationToken);

    private static ProblemHttpResult Refused(string code, string title) =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, code, title);
}

/// <summary>An uploaded file waiting to be sent with a form.</summary>
internal sealed record FormUploadResponse(Guid Id, string Field, string FileName, string ContentType, long Size, AttachmentScanStatus ScanStatus)
{
    public static FormUploadResponse From(TicketAttachment a) => new(a.Id, a.FormFieldKey ?? string.Empty, a.FileName, a.ContentType, a.Size, a.ScanStatus);
}
