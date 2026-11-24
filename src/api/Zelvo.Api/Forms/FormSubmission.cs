using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Forms;

/// <summary>Names and details of what form answers refer to, for showing them.</summary>
/// <param name="Users">Display names by user id.</param>
/// <param name="Locations">Location paths by id.</param>
/// <param name="Files">Uploaded files by id.</param>
internal sealed record FormReferences(
    IReadOnlyDictionary<string, string?> Users, IReadOnlyDictionary<string, LocalizedTextDto> Locations, IReadOnlyDictionary<string, FormFileRef> Files)
{
    public static FormReferences Empty { get; } = new(new Dictionary<string, string?>(), new Dictionary<string, LocalizedTextDto>(), new Dictionary<string, FormFileRef>());

    /// <summary>Looks up the users, locations and files that answers refer to.</summary>
    public static async Task<FormReferences> LoadAsync(
        ZelvoDbContext db, IReadOnlyList<FormField> fields, JsonObject answers, IReadOnlyCollection<Guid> extraFiles, string? language, CancellationToken cancellationToken)
    {
        var users = new HashSet<Guid>();
        var locations = new HashSet<Guid>();
        var files = new HashSet<Guid>(extraFiles);
        foreach (var field in fields)
        {
            var value = answers[field.Key];
            switch (field.Type)
            {
                case FieldType.User when value is JsonValue v && Guid.TryParse(v.ToString(), out var u):
                    users.Add(u);
                    break;
                case FieldType.Location when value is JsonValue v && Guid.TryParse(v.ToString(), out var l):
                    locations.Add(l);
                    break;
                case FieldType.File when value is JsonArray array:
                    foreach (var item in array)
                    {
                        if (Guid.TryParse(item?.ToString(), out var f)) files.Add(f);
                    }

                    break;
                case FieldType.Signature when value is JsonObject signature && Guid.TryParse(signature["attachmentId"]?.ToString(), out var s):
                    files.Add(s);
                    break;
            }
        }

        var names = await FormEndpoints.PeopleNamesAsync(db, users, cancellationToken);
        var tree = new TreeIndex<Data.Directory.Location>(locations.Count == 0 ? [] : await db.Locations.AsNoTracking().ToListAsync(cancellationToken));
        var fileRows = files.Count == 0 ? [] : await db.TicketAttachments.AsNoTracking().Where(a => files.Contains(a.Id)).ToListAsync(cancellationToken);
        return new FormReferences(
            names.ToDictionary(n => n.Key.ToString(), n => n.Value),
            locations.Select(l => tree.RefOf(l)).Where(r => r is not null).ToDictionary(r => r!.Id.ToString(), r => r!.Path),
            fileRows.ToDictionary(f => f.Id.ToString(), f => new FormFileRef(f.Id, f.FileName, f.ContentType, f.Size, f.ScanStatus, f.TicketId)));
    }
}

/// <summary>An uploaded file an answer refers to.</summary>
internal sealed record FormFileRef(Guid Id, string FileName, string ContentType, long Size, AttachmentScanStatus ScanStatus, Guid? TicketId);

/// <summary>Answers checked and ready to be saved with a new ticket.</summary>
internal sealed class PreparedFormResponse(ResolvedForm form, FormAnswerResult answers, IReadOnlyList<TicketAttachment> uploads, Guid? submittedBy)
{
    /// <summary>Saves the answers with <paramref name="ticket"/> and gives it the uploaded files.</summary>
    public void Attach(ZelvoDbContext db, Ticket ticket)
    {
        db.TicketFormResponses.Add(new TicketFormResponse
        {
            TicketId = ticket.Id,
            FormId = form.Form.Id,
            FormVersionId = form.Version.Id,
            Fields = FormJson.Write(form.Fields),
            Answers = answers.Answers.ToJsonString(),
            PersonalAnswers = answers.PersonalAnswers.ToJsonString(),
            SubmittedById = submittedBy,
        });
        foreach (var upload in uploads)
        {
            upload.TicketId = ticket.Id;
        }
    }
}

/// <summary>
/// Filling a form with a new ticket (task C1). The server applies defaults and rules again and
/// checks every answer and what it refers to; nothing is saved while any answer is refused.
/// </summary>
internal static class FormSubmission
{
    private static readonly (ProfileSource Source, string Field)[] PersonalProfile =
        [(ProfileSource.Email, "email"), (ProfileSource.Phone, "phone"), (ProfileSource.EmiratesId, "emiratesId")];

    /// <summary>
    /// Defaults for <paramref name="requester"/>. Staff filling a form for someone else get that
    /// person's email, phone and Emirates ID only when their roles may see them, and that view is audited.
    /// </summary>
    public static async Task<JsonObject> DefaultsForAsync(
        ZelvoDbContext db, CurrentUser user, IAuditLog audit, IReadOnlyList<FormField> fields, UserAccount? requester, string? language,
        CancellationToken cancellationToken)
    {
        var defaults = await FormResolver.DefaultsAsync(db, fields, requester, language ?? requester?.PreferredLanguage ?? "en", cancellationToken);
        if (requester is null || requester.Id == user.Account?.Id)
        {
            return defaults;
        }

        var shown = new List<string>();
        foreach (var field in fields)
        {
            if (PersonalProfile.FirstOrDefault(p => p.Source == field.ProfileDefault) is { Field: { } userField } && defaults.ContainsKey(field.Key))
            {
                if (FieldFilter.IsVisible(user, PermissionCatalog.Users, userField))
                {
                    shown.Add(userField);
                }
                else
                {
                    defaults.Remove(field.Key);
                }
            }
        }

        if (shown.Count > 0)
        {
            await audit.RecordAsync(AuditCategory.View, "users.personal-data.viewed", "user_account", requester.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["fields"] = shown.Distinct().ToArray(), ["purpose"] = "form-defaults" }, cancellationToken);
        }

        return defaults;
    }

    /// <summary>
    /// Finds the form for the new ticket and checks the answers. Returns nothing to save when no form
    /// applies and no answers were sent; a problem when answers are refused or the form changed.
    /// </summary>
    public static async Task<(PreparedFormResponse? Prepared, IResult? Problem)> PrepareAsync(
        ZelvoDbContext db, CurrentUser user, IAuditLog audit, TicketType recordType, Guid? categoryId, Guid requesterId,
        Guid? formVersionId, JsonObject? submitted, Tickets.AttachmentPolicy policy, CancellationToken cancellationToken)
    {
        var form = await FormResolver.ForTicketAsync(db, recordType, categoryId, cancellationToken);
        return await PrepareAsync(db, user, audit, form, requesterId, formVersionId, submitted, policy, cancellationToken);
    }

    /// <summary>
    /// Checks the answers to <paramref name="form"/> (task C2: the form a catalog service names).
    /// Returns nothing to save when there is no form and no answers were sent.
    /// </summary>
    public static async Task<(PreparedFormResponse? Prepared, IResult? Problem)> PrepareAsync(
        ZelvoDbContext db, CurrentUser user, IAuditLog audit, ResolvedForm? form, Guid requesterId,
        Guid? formVersionId, JsonObject? submitted, Tickets.AttachmentPolicy policy, CancellationToken cancellationToken)
    {
        if (form is null)
        {
            return submitted is { Count: > 0 } || formVersionId is not null
                ? (null, UserEndpoints.Validation("formAnswers", "No form applies to this category; leave out the answers."))
                : (null, null);
        }

        if (formVersionId is { } sentVersion && sentVersion != form.Version.Id)
        {
            return (null, ApiProblem.Create(StatusCodes.Status409Conflict, "form-changed",
                "The form was changed while it was being filled. Load it again; your answers are kept where the fields are the same."));
        }

        var requester = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == requesterId, cancellationToken);
        var defaults = await DefaultsForAsync(db, user, audit, form.PlainFields, requester, null, cancellationToken);
        var result = FormAnswers.Check(form.PlainFields, submitted ?? [], defaults);
        var uploads = new List<TicketAttachment>();
        if (result.Errors.Count == 0)
        {
            await CheckReferencesAsync(db, user, result, uploads, policy, cancellationToken);
        }

        if (result.Errors.Count > 0)
        {
            return (null, FormsCommon.AnswerErrors(result.Errors));
        }

        return (new PreparedFormResponse(form, result, uploads, user.Account?.Id), null);
    }

    private static async Task CheckReferencesAsync(
        ZelvoDbContext db, CurrentUser user, FormAnswerResult result, List<TicketAttachment> uploads, Tickets.AttachmentPolicy policy, CancellationToken cancellationToken)
    {
        var userIds = result.Users.Select(u => u.Id).Distinct().ToList();
        var active = userIds.Count == 0 ? [] : await db.Users.Where(u => userIds.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => u.Id).ToListAsync(cancellationToken);
        foreach (var (field, id) in result.Users.Where(u => !active.Contains(u.Id)))
        {
            result.Errors.Add(new(field, "user-unknown", "Choose an active user of this organisation."));
        }

        foreach (var (field, id) in result.Locations)
        {
            if (!await TreeEndpoints.IsSelectableAsync(db.Locations, id, cancellationToken))
            {
                result.Errors.Add(new(field, "location-unknown", "Choose an active location."));
            }
        }

        var fileIds = result.Files.Select(f => f.AttachmentId).Distinct().ToList();
        var files = fileIds.Count == 0 ? [] : await db.TicketAttachments.Where(a => fileIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        foreach (var reference in result.Files)
        {
            var root = reference.Field.Split('.')[0];
            if (!files.TryGetValue(reference.AttachmentId, out var file) || file.TicketId is not null || file.ArticleId is not null || file.ServiceId is not null
                || file.UploadedById != user.Account?.Id || file.FormFieldKey != root)
            {
                result.Errors.Add(new(reference.Field, "file-unknown", "Upload the file for this field again."));
                continue;
            }

            if (file.ScanStatus == AttachmentScanStatus.Infected)
            {
                result.Errors.Add(new(reference.Field, "file-infected", $"The file {file.FileName} was rejected by the malware scan."));
                continue;
            }

            if (reference.Signature)
            {
                if (file.ContentType != "image/png")
                {
                    result.Errors.Add(new(reference.Field, "signature-not-png", "A drawn signature is a PNG image."));
                    continue;
                }
            }
            else
            {
                var allowed = reference.Validation?.FileTypes is { Count: > 0 } types ? types.Intersect(policy.AllowedExtensions, StringComparer.Ordinal).ToList() : policy.AllowedExtensions;
                if (!allowed.Contains(Tickets.FileTypes.ExtensionOf(file.FileName), StringComparer.Ordinal))
                {
                    result.Errors.Add(new(reference.Field, "file-type", $"Accepted file types: {string.Join(", ", allowed)}."));
                    continue;
                }

                var maxBytes = Math.Min(policy.MaxBytes, (reference.Validation?.MaxFileSizeMb ?? int.MaxValue) * 1024L * 1024L);
                if (file.Size > maxBytes)
                {
                    result.Errors.Add(new(reference.Field, "file-too-large", $"Files can be at most {maxBytes / 1024 / 1024} MB."));
                    continue;
                }
            }

            uploads.Add(file);
        }
    }

    /// <summary>The form answers of a ticket for someone who may see the ticket; viewing personal answers of others is audited.</summary>
    public static async Task<TicketFormView?> ViewAsync(ZelvoDbContext db, CurrentUser user, IAuditLog audit, Ticket ticket, CancellationToken cancellationToken)
    {
        var response = await db.TicketFormResponses.AsNoTracking().SingleOrDefaultAsync(r => r.TicketId == ticket.Id, cancellationToken);
        if (response is null)
        {
            return null;
        }

        var form = await db.Forms.AsNoTracking().SingleAsync(f => f.Id == response.FormId, cancellationToken);
        var version = await db.FormVersions.AsNoTracking().Where(v => v.Id == response.FormVersionId).Select(v => v.Number).SingleAsync(cancellationToken);
        var fields = FormJson.ReadResolved(response.Fields);
        var answers = JsonNode.Parse(response.Answers) as JsonObject ?? [];
        var personal = JsonNode.Parse(response.PersonalAnswers) as JsonObject ?? [];
        foreach (var (key, value) in personal)
        {
            answers[key] = value?.DeepClone();
        }

        if (personal.Count > 0 && ticket.RequesterId != user.Account?.Id)
        {
            await audit.RecordAsync(AuditCategory.View, "forms.personal-data.viewed", "ticket", ticket.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["fields"] = personal.Select(p => p.Key).ToArray() }, cancellationToken);
        }

        var plain = fields.Select(f => f.Field).ToList();
        var references = await FormReferences.LoadAsync(db, plain, answers, [], null, cancellationToken);
        return new TicketFormView(
            new FormRef(form.Id, form.Key, LocalizedTextDto.From(form.Name), LocalizedTextDto.From(form.Description), response.FormVersionId, version),
            [.. fields.Select(f => System.Text.Json.JsonSerializer.SerializeToNode(f, FormJson.Options)!)], answers, references, response.SubmittedAt);
    }
}

/// <summary>A ticket's form answers.</summary>
/// <param name="Form">The form and the version filled.</param>
/// <param name="Fields">The fields as they were when it was filled.</param>
/// <param name="Answers">Answers by field key (personal data included for those who may see the ticket).</param>
/// <param name="References">Names of users, paths of locations and files the answers refer to.</param>
/// <param name="SubmittedAt">When it was filled.</param>
internal sealed record TicketFormView(FormRef Form, IReadOnlyList<JsonNode> Fields, JsonObject Answers, FormReferences References, DateTimeOffset SubmittedAt);
