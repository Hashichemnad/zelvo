using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Privacy;

/// <summary>
/// A copy of everything Zelvo holds about a person (GOV-5, task F5: the right of access and of
/// taking the data elsewhere), as a ZIP file: <c>personal-data.json</c>, a machine-readable JSON
/// document, and the files the person uploaded (scanned clean) under <c>files/</c>. Staff's
/// internal notes are not part of it: they are the organisation's work records, not the person's data.
/// </summary>
internal sealed class PersonalDataExport(ZelvoDbContext db, IAttachmentStore attachments)
{
    /// <summary>Most files put in one copy, and their total size (larger sets are listed, not copied).</summary>
    private const int MaxFiles = 500;
    private const long MaxFileBytes = 500L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Writes the ZIP of <paramref name="personId"/> to <paramref name="output"/>.</summary>
    public async Task WriteAsync(Guid personId, Stream output, CancellationToken cancellationToken)
    {
        var person = await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(r => r.Role).SingleAsync(u => u.Id == personId, cancellationToken);
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.RequesterId == personId).OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);
        var ticketIds = tickets.Select(t => t.Id).ToList();
        var comments = await db.TicketComments.AsNoTracking()
            .Where(c => ticketIds.Contains(c.TicketId) && c.Visibility == CommentVisibility.Public || c.AuthorId == personId && !c.ByStaff)
            .OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);
        var commentIds = comments.Select(c => c.Id).ToList();
        var forms = await db.TicketFormResponses.AsNoTracking().Where(f => ticketIds.Contains(f.TicketId)).ToDictionaryAsync(f => f.TicketId, cancellationToken);
        var files = await db.TicketAttachments.AsNoTracking()
            .Where(a => (a.TicketId != null && ticketIds.Contains(a.TicketId.Value) && (a.CommentId == null || commentIds.Contains(a.CommentId.Value))) || a.UploadedById == personId)
            .Where(a => a.ArticleId == null && a.ServiceId == null && a.BrandingSlot == null)
            .OrderBy(a => a.UploadedAt).ToListAsync(cancellationToken);
        var sessions = await db.ChatSessions.AsNoTracking().Where(s => s.VisitorUserId == personId).OrderBy(s => s.StartedAt).ToListAsync(cancellationToken);
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var messages = await db.ChatMessages.AsNoTracking().Where(m => sessionIds.Contains(m.SessionId)).OrderBy(m => m.Sequence).ToListAsync(cancellationToken);
        var consents = await db.ConsentRecords.AsNoTracking().Where(r => r.UserId == personId).OrderBy(r => r.CreatedAt).ToListAsync(cancellationToken);
        var purposes = await db.ConsentPurposes.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Key, cancellationToken);
        var ratings = await db.HappinessRatings.AsNoTracking().Where(r => r.UserId == personId).OrderBy(r => r.CreatedAt).ToListAsync(cancellationToken);
        var feedback = await db.KbFeedbackEntries.AsNoTracking().Where(f => f.UserId == personId).OrderBy(f => f.CreatedAt).ToListAsync(cancellationToken);
        var notifications = await db.Notifications.AsNoTracking().Where(n => n.RecipientId == personId).OrderByDescending(n => n.CreatedAt).Take(1000).ToListAsync(cancellationToken);
        var payments = await db.Payments.AsNoTracking().Where(p => p.TicketId != null && ticketIds.Contains(p.TicketId.Value)).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var decisions = await db.ApprovalDecisions.AsNoTracking().Where(d => d.ApproverId == personId).OrderBy(d => d.CreatedAt).ToListAsync(cancellationToken);
        var requests = await db.DataSubjectRequests.AsNoTracking().Where(r => r.SubjectId == personId).OrderBy(r => r.ReceivedAt).ToListAsync(cancellationToken);
        var groups = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == personId)
            .Join(db.SupportGroups.AsNoTracking(), m => m.GroupId, g => g.Id, (m, g) => new { g.Name, m.IsLead }).ToListAsync(cancellationToken);

        var copied = new List<(Guid Id, string Path)>();
        long bytes = 0;
        var document = new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.UtcNow,
            ["format"] = "zelvo-personal-data/1",
            ["profile"] = new JsonObject
            {
                ["id"] = person.Id,
                ["name"] = person.DisplayName,
                ["email"] = person.Email,
                ["phone"] = person.Phone,
                ["emiratesId"] = person.EmiratesId,
                ["employeeId"] = person.EmployeeId,
                ["status"] = person.Status.ToString(),
                ["preferredLanguage"] = person.PreferredLanguage,
                ["calendar"] = person.Calendar?.ToString(),
                ["digits"] = person.Digits?.ToString(),
                ["languages"] = new JsonArray([.. person.Languages.Select(l => JsonValue.Create(l))]),
                ["createdAt"] = person.CreatedAt,
                ["lastSeenAt"] = person.LastSeenAt,
                ["roles"] = new JsonArray([.. person.Roles.Select(r => JsonValue.Create(r.Role?.Key))]),
                ["groups"] = JsonSerializer.SerializeToNode(groups, Json),
            },
            ["tickets"] = new JsonArray([.. tickets.Select(t => (JsonNode)new JsonObject
            {
                ["number"] = t.Number,
                ["type"] = t.Type.ToString(),
                ["title"] = t.Title,
                ["description"] = t.Description,
                ["state"] = t.State.ToString(),
                ["channel"] = t.Channel.ToString(),
                ["createdAt"] = t.CreatedAt,
                ["resolvedAt"] = t.ResolvedAt,
                ["closedAt"] = t.ClosedAt,
                ["resolutionNotes"] = t.ResolutionNotes,
                ["anonymisedAt"] = t.AnonymisedAt,
                ["formAnswers"] = forms.TryGetValue(t.Id, out var f) ? Merge(f.Answers, f.PersonalAnswers) : null,
                ["messages"] = new JsonArray([.. comments.Where(c => c.TicketId == t.Id).Select(c => (JsonNode)new JsonObject
                {
                    ["writtenAt"] = c.CreatedAt,
                    ["byYou"] = c.AuthorId == personId,
                    ["source"] = c.Source.ToString(),
                    ["language"] = c.Language,
                    ["text"] = c.Body,
                })]),
            })]),
            ["messagesOnOtherTickets"] = new JsonArray([.. comments.Where(c => !ticketIds.Contains(c.TicketId)).Select(c => (JsonNode)new JsonObject
            {
                ["writtenAt"] = c.CreatedAt,
                ["text"] = c.Body,
            })]),
            ["chats"] = new JsonArray([.. sessions.Select(s => (JsonNode)new JsonObject
            {
                ["startedAt"] = s.StartedAt,
                ["endedAt"] = s.EndedAt,
                ["messages"] = new JsonArray([.. messages.Where(m => m.SessionId == s.Id && m.Sender != Zelvo.Data.Channels.ChatSender.System).Select(m => (JsonNode)new JsonObject
                {
                    ["sentAt"] = m.CreatedAt,
                    ["from"] = m.Sender.ToString(),
                    ["text"] = m.Body,
                })]),
            })]),
            ["consents"] = new JsonArray([.. consents.Select(r => (JsonNode)new JsonObject
            {
                ["at"] = r.CreatedAt,
                ["action"] = r.Action.ToString(),
                ["noticeVersion"] = r.NoticeVersion,
                ["purpose"] = r.PurposeId is { } p && purposes.TryGetValue(p, out var key) ? key : null,
                ["channel"] = r.Channel.ToString(),
            })]),
            ["happinessRatings"] = new JsonArray([.. ratings.Select(r => (JsonNode)new JsonObject { ["at"] = r.CreatedAt, ["score"] = r.Score, ["points"] = r.Points, ["comment"] = r.Comment })]),
            ["articleFeedback"] = new JsonArray([.. feedback.Select(r => (JsonNode)new JsonObject { ["at"] = r.CreatedAt, ["helpful"] = r.Helpful, ["comment"] = r.Comment })]),
            ["notifications"] = new JsonArray([.. notifications.Select(n => (JsonNode)new JsonObject { ["at"] = n.CreatedAt, ["event"] = n.EventType, ["title"] = n.Title, ["text"] = n.Text })]),
            ["payments"] = new JsonArray([.. payments.Select(p => (JsonNode)new JsonObject
            {
                ["ticket"] = tickets.First(t => t.Id == p.TicketId).Number,
                ["amountAed"] = p.AmountAed,
                ["state"] = p.State.ToString(),
                ["receipt"] = p.ReceiptNumber,
                ["paidAt"] = p.PaidAt,
                ["refundedAed"] = p.RefundedAed,
            })]),
            ["approvalDecisions"] = new JsonArray([.. decisions.Select(d => (JsonNode)new JsonObject { ["at"] = d.DecidedAt ?? d.CreatedAt, ["state"] = d.State.ToString(), ["comment"] = d.Comment })]),
            ["privacyRequests"] = new JsonArray([.. requests.Select(r => (JsonNode)new JsonObject
            {
                ["number"] = r.Number,
                ["type"] = r.Type.ToString(),
                ["state"] = r.State.ToString(),
                ["receivedAt"] = r.ReceivedAt,
                ["details"] = r.Details,
                ["resolution"] = r.Resolution,
            })]),
        };

        var fileList = new JsonArray();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files)
        {
            var ticket = tickets.FirstOrDefault(t => t.Id == file.TicketId)?.Number ?? "uploads";
            var path = $"files/{ticket}/{file.Id:N}-{SafeName(file.FileName)}";
            var include = file.ScanStatus == AttachmentScanStatus.Clean && copied.Count < MaxFiles && bytes + file.Size <= MaxFileBytes;
            fileList.Add(new JsonObject { ["name"] = file.FileName, ["ticket"] = ticket, ["uploadedAt"] = file.UploadedAt, ["size"] = file.Size, ["path"] = include ? path : null });
            if (!include || await attachments.ReadAsync(file.Id, cancellationToken) is not { } content)
            {
                continue;
            }

            var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
            await using (var stream = await entry.OpenAsync(cancellationToken))
            {
                await stream.WriteAsync(content, cancellationToken);
            }

            copied.Add((file.Id, path));
            bytes += file.Size;
        }

        document["files"] = fileList;
        var json = zip.CreateEntry("personal-data.json", CompressionLevel.Optimal);
        await using (var stream = await json.OpenAsync(cancellationToken))
        {
            await JsonSerializer.SerializeAsync(stream, document, Json, cancellationToken);
        }
    }

    private static JsonObject Merge(string answers, string personal)
    {
        var merged = JsonNode.Parse(answers) as JsonObject ?? [];
        if (JsonNode.Parse(personal) is JsonObject extra)
        {
            foreach (var (key, value) in extra)
            {
                merged[key] = value?.DeepClone();
            }
        }

        return merged;
    }

    /// <summary>A file name that is safe inside the ZIP: no folders, no control characters.</summary>
    internal static string SafeName(string name)
    {
        var clean = new string([.. Path.GetFileName(name.Replace('\\', '/')).Select(c => char.IsControl(c) || c is '/' or ':' ? '_' : c)]);
        return clean.Length is 0 or > 120 ? "file" : clean;
    }
}
