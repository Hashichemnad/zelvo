using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Channels;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Ai.Features;

/// <summary>
/// Transcribes a WhatsApp voice note (task E3) once its recording is scanned: the transcript is
/// kept with the comment (comments never change) and shown to staff and the requester. Runs in
/// the worker's AI jobs; nothing happens while the feature is off.
/// </summary>
public sealed class VoiceTranscriber(ZelvoDbContext db, IAiGateway gateway, IAttachmentStore attachments, TimeProvider time)
{
    /// <summary>The last call, for the job's retry decision.</summary>
    public AiResult? LastCall { get; private set; }

    /// <summary>Transcribes the voice note of <paramref name="commentId"/>.</summary>
    public async Task TranscribeAsync(Guid? commentId, CancellationToken cancellationToken)
    {
        var note = commentId is { } id ? await db.VoiceNotes.FirstOrDefaultAsync(v => v.CommentId == id, cancellationToken).ConfigureAwait(false) : null;
        if (note is not { Status: TranscriptStatus.Pending })
        {
            return;
        }

        var attachment = await db.TicketAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == note.AttachmentId, cancellationToken).ConfigureAwait(false);
        if (attachment is null || attachment.ScanStatus == AttachmentScanStatus.Infected)
        {
            note.Status = TranscriptStatus.Failed;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // The recording is sent only once ClamAV found it clean (or scanning is off).
        if (attachment.ScanStatus == AttachmentScanStatus.Pending)
        {
            throw new AiJobRetryException("The voice note is not scanned yet.");
        }

        var audio = await attachments.ReadAsync(attachment.Id, cancellationToken).ConfigureAwait(false) ?? [];
        LastCall = await gateway.TranscribeAsync(audio, attachment.ContentType, "ticket_comment", note.CommentId.ToString(), cancellationToken).ConfigureAwait(false);
        if (LastCall is { Succeeded: true, Text: { } text })
        {
            note.Transcript = AiText.Cut(text, VoiceNote.TranscriptMaxLength);
            note.Language = Arabic(text) ? "ar" : "en";
            note.Status = TranscriptStatus.Done;
            note.TranscribedAt = time.GetUtcNow();
            note.AiCallId = LastCall.CallId;
        }
        else if (LastCall is { ErrorCode: "ai-off" or "limit-reached" or "not-supported" or "empty" })
        {
            note.Status = LastCall.ErrorCode == "ai-off" ? TranscriptStatus.Off : TranscriptStatus.Failed;
            note.AiCallId = LastCall.CallId;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // Mostly Arabic letters.
    private static bool Arabic(string text)
    {
        var letters = text.Count(char.IsLetter);
        return letters > 0 && text.Count(c => c is >= '\u0600' and <= '\u06FF') * 2 >= letters;
    }
}
