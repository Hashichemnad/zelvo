using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Identity;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Privacy;

/// <summary>Why a person cannot be erased.</summary>
internal enum ErasureRefusal
{
    /// <summary>Nothing stops it.</summary>
    None,

    /// <summary>The person is already erased.</summary>
    AlreadyErased,

    /// <summary>Privacy officers cannot erase themselves.</summary>
    Self,

    /// <summary>The person is the tenant's last active administrator.</summary>
    LastAdministrator,

    /// <summary>The person still has tickets that are not resolved or closed.</summary>
    OpenTickets,
}

/// <summary>What an erasure or anonymisation removed, for the audit log (counts only).</summary>
internal sealed record ErasureCounts(int Tickets, int Comments, int Attachments, int ChatMessages, int Emails, int Notifications)
{
    public Dictionary<string, object?> ToMetadata() => new(StringComparer.Ordinal)
    {
        ["tickets"] = Tickets,
        ["comments"] = Comments,
        ["attachments"] = Attachments,
        ["chatMessages"] = ChatMessages,
        ["emails"] = Emails,
        ["notifications"] = Notifications,
    };
}

/// <summary>
/// Removes personal data (GOV-5, task F5; product owner decisions 2026-11-25). Two operations:
/// <list type="bullet">
/// <item><b>Anonymise tickets</b> (retention periods, and the tickets of an erased person): title,
/// description, form answers, attachments, AI caches, PDF exports, copies of emails and the
/// requester's own messages are removed; the ticket keeps its number, type, dates, states,
/// category, group and SLA clocks for reports. Staff notes and replies stay. Payments and receipts
/// stay (financial records); the receipt then names nobody.</item>
/// <item><b>Erase a person</b>: their tickets anonymised as above, their own messages anywhere,
/// chats, notifications and email copies removed, their account emptied (no name, email, phone,
/// Emirates ID, employee number, roles, groups or skills; disabled, shown as "Erased person") and
/// their Keycloak user deleted. The consent history and the audit log are kept as the record the
/// organisation must keep (decided 2026-11-25); the audit log shows them as "Erased person".</item>
/// </list>
/// Every change runs on the tenant's connection (row-level security applies) in one transaction,
/// as plain SQL so the field-level audit does not copy the removed values; one audit entry with
/// counts records each operation.
/// </summary>
internal sealed class PersonalDataEraser(
    ZelvoDbContext db, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity, IAuditLog audit, ISearchIndexer search)
{
    /// <summary>Whether <paramref name="person"/> can be erased by <paramref name="actorId"/>.</summary>
    public static async Task<ErasureRefusal> CheckAsync(ZelvoDbContext db, UserAccount person, Guid? actorId, CancellationToken cancellationToken)
    {
        if (person.ErasedAt is not null)
        {
            return ErasureRefusal.AlreadyErased;
        }

        if (person.Id == actorId)
        {
            return ErasureRefusal.Self;
        }

        var isAdmin = await db.UserRoles.AnyAsync(r => r.UserId == person.Id && r.Role!.Key == BuiltInRoles.TenantAdmin, cancellationToken);
        if (isAdmin && !await db.UserRoles.AnyAsync(
                r => r.UserId != person.Id && r.Role!.Key == BuiltInRoles.TenantAdmin && db.Users.Any(u => u.Id == r.UserId && u.Status == UserStatus.Active && u.ErasedAt == null),
                cancellationToken))
        {
            return ErasureRefusal.LastAdministrator;
        }

        return await db.Tickets.AnyAsync(t => t.RequesterId == person.Id && t.State != TicketState.Resolved && t.State != TicketState.Closed, cancellationToken) ? ErasureRefusal.OpenTickets : ErasureRefusal.None;
    }

    /// <summary>Erases <paramref name="person"/>; the caller checked <see cref="CheckAsync"/>. Saves and commits.</summary>
    public async Task<ErasureCounts> ErasePersonAsync(UserAccount person, string reason, Guid? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);
        var now = DateTimeOffset.UtcNow;
        var id = person.Id;
        var subject = person.Subject;
        var email = person.Email;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ticketIds = await db.Tickets.Where(t => t.RequesterId == id).Select(t => t.Id).ToListAsync(cancellationToken);
        var counts = await AnonymiseCoreAsync(ticketIds, now, staffTextToo: false, cancellationToken);

        // The person's own messages on other people's tickets (a requester's comment on a ticket they follow).
        var comments = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ticket_comments SET body = '', erased_at = {now} WHERE author_id = {id} AND NOT by_staff AND erased_at IS NULL", cancellationToken);
        var chat = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE chat_messages m SET body = '' FROM chat_sessions s
            WHERE m.session_id = s.id AND s.visitor_user_id = {id} AND m.sender IN ('Visitor', 'Assistant') AND m.body <> ''
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE chat_sessions SET visitor_name = '', visitor_email = '' WHERE visitor_user_id = {id}", cancellationToken);
        var emails = 0;
        if (!string.IsNullOrEmpty(email))
        {
            emails += await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE inbound_emails SET from_address = '', subject = '' WHERE lower(from_address) = lower({email})", cancellationToken);
            emails += await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE outbound_emails SET to_address = '', subject = '', html_body = '', text_body = '' WHERE lower(to_address) = lower({email})", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE chat_sessions SET visitor_name = '', visitor_email = '' WHERE lower(visitor_email) = lower({email})", cancellationToken);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE notification_deliveries SET address = '', subject = '', html_body = '', text_body = '' WHERE recipient_id = {id}", cancellationToken);
        var notifications = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notifications WHERE recipient_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_preferences WHERE user_id = {id}", cancellationToken);
        // Task E3: WhatsApp, SMS and Teams. Their messages lose their text and address; the
        // person's contacts (phone numbers, Teams ids) are removed, so the channels forget them.
        var messages = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE channel_messages SET text = NULL, address = NULL, payload = NULL
            WHERE contact_id IN (SELECT id FROM messaging_contacts WHERE user_id = {id}) AND (text IS NOT NULL OR address IS NOT NULL OR payload IS NOT NULL)
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM messaging_contacts WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE happiness_ratings SET comment = NULL WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE kb_feedback SET comment = NULL WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE proactive_runs SET answers = '{{}}', state = CASE WHEN state IN ('Scheduled', 'Offered') THEN 'Expired' ELSE state END, ended_at = COALESCE(ended_at, {now}) WHERE person_id = {id}",
            cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM document_exports WHERE requested_by_id = {id}", cancellationToken);
        // Form uploads not attached to a ticket yet.
        var uploads = await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ticket_attachments WHERE uploaded_by_id = {id} AND ticket_id IS NULL AND article_id IS NULL AND service_id IS NULL AND branding_slot IS NULL", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE data_subject_requests SET details = NULL WHERE subject_id = {id}", cancellationToken);

        // The account: kept (what refers to it keeps working) without anything that identifies them.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM user_roles WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM support_group_members WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM user_skills WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM customer_group_members WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM report_schedule_recipients WHERE user_id = {id}", cancellationToken);
        // Task J2: the person's devices for web push and their push messages.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM push_deliveries WHERE recipient_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM push_subscriptions WHERE user_id = {id}", cancellationToken);
        // Task K4: the person's wellbeing figures and alerts.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wellbeing_days WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wellbeing_alerts WHERE user_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET manager_id = NULL WHERE manager_id = {id}", cancellationToken);
        var erasedSubject = $"erased:{id}";
        var noLanguages = Array.Empty<string>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE users SET subject = {erasedSubject}, email = NULL, display_name = NULL, emirates_id = NULL, phone = NULL, employee_id = NULL,
                manager_id = NULL, department_id = NULL, location_id = NULL, preferred_language = NULL, languages = {noLanguages},
                available_for_assignment = false, notify_whats_app = false, notify_sms = false, notify_teams = false, status = 'Disabled', erased_at = {now}, updated_at = {now}
            WHERE id = {id}
            """, cancellationToken);

        var total = counts with { Comments = counts.Comments + comments, Attachments = counts.Attachments + uploads, ChatMessages = counts.ChatMessages + chat + messages, Emails = counts.Emails + emails, Notifications = notifications };
        var metadata = total.ToMetadata();
        metadata["reason"] = reason;
        metadata["requestId"] = requestId;
        audit.Add(AuditCategory.Delete, "privacy.person.erased", "user", id.ToString(), metadata);
        await db.SaveChangesAsync(cancellationToken);

        // Keycloak last: if it fails, the erasure is not committed and can be tried again.
        if (!subject.StartsWith("contact:", StringComparison.Ordinal) && !subject.StartsWith("erased:", StringComparison.Ordinal))
        {
            await keycloak.DeleteUserAsync(identity.Value.RealmFor(tenant.Slug!), subject, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return total;
    }

    /// <summary>
    /// Anonymises tickets whose retention period ended (by the privacy jobs): everything the
    /// person erasure removes from a ticket, and the staff's messages too, since the whole
    /// ticket's period is over. Saves and commits.
    /// </summary>
    public async Task<ErasureCounts> AnonymiseTicketsAsync(IReadOnlyList<Guid> ticketIds, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var counts = await AnonymiseCoreAsync(ticketIds, DateTimeOffset.UtcNow, staffTextToo: true, cancellationToken);
        var metadata = counts.ToMetadata();
        metadata["reason"] = reason;
        audit.Add(AuditCategory.Delete, "privacy.tickets.anonymised", "tickets", null, metadata);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return counts;
    }

    private async Task<ErasureCounts> AnonymiseCoreAsync(IReadOnlyList<Guid> ids, DateTimeOffset now, bool staffTextToo, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new ErasureCounts(0, 0, 0, 0, 0, 0);
        }

        var array = ids.ToArray();
        var tickets = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tickets SET title = '', description = NULL, anonymised_at = COALESCE(anonymised_at, {now}), updated_at = {now} WHERE id = ANY({array})", cancellationToken);
        if (staffTextToo)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tickets SET resolution_notes = NULL WHERE id = ANY({array})", cancellationToken);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ticket_form_responses SET answers = '{{}}'::jsonb, personal_answers = '{{}}'::jsonb WHERE ticket_id = ANY({array})", cancellationToken);
        var comments = staffTextToo
            ? await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ticket_comments SET body = '', erased_at = {now} WHERE ticket_id = ANY({array}) AND erased_at IS NULL", cancellationToken)
            : await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE ticket_comments c SET body = '', erased_at = {now} FROM tickets t
                WHERE c.ticket_id = t.id AND t.id = ANY({array}) AND c.author_id = t.requester_id AND NOT c.by_staff AND c.erased_at IS NULL
                """, cancellationToken);
        // Files of the ticket: every file for a retention period; the requester's own for an erasure.
        var attachments = staffTextToo
            ? await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ticket_attachments WHERE ticket_id = ANY({array})", cancellationToken)
            : await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM ticket_attachments a USING tickets t
                WHERE a.ticket_id = t.id AND t.id = ANY({array}) AND (a.uploaded_by_id = t.requester_id OR a.uploaded_by_id IS NULL OR a.form_field_key IS NOT NULL)
                """, cancellationToken);
        var chat = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE chat_messages m SET body = '' FROM chat_sessions s WHERE m.session_id = s.id AND s.ticket_id = ANY({array}) AND m.body <> ''", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE chat_sessions SET visitor_name = '', visitor_email = '' WHERE ticket_id = ANY({array})", cancellationToken);
        var emails = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE inbound_emails SET from_address = '', subject = '' WHERE ticket_id = ANY({array})", cancellationToken);
        emails += await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE outbound_emails SET to_address = '', subject = '', html_body = '', text_body = '' WHERE ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notifications WHERE ticket_id = ANY({array})", cancellationToken);
        // Task E3: WhatsApp, SMS and Teams messages of the tickets, and transcripts of voice notes.
        chat += await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE channel_messages SET text = NULL, address = NULL, payload = NULL WHERE ticket_id = ANY({array}) AND (text IS NOT NULL OR address IS NOT NULL OR payload IS NOT NULL)",
            cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM voice_notes WHERE ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ai_ticket_insights WHERE ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ai_jobs WHERE ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ai_translations WHERE source_id = ANY({array}) OR source_id IN (SELECT id FROM ticket_comments WHERE ticket_id = ANY({array}))", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ai_triage_suggestions SET reason = '' WHERE ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ai_duplicate_candidates SET reason = '' WHERE ticket_id = ANY({array}) OR candidate_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM document_exports WHERE entity_id = ANY({array}) AND kind IN ('Incident', 'ServiceRequest')", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE customer_voice_records SET confirmation_comment = NULL, review_feedback = NULL WHERE ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE happiness_ratings SET comment = NULL WHERE ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE proactive_runs SET answers = '{{}}' WHERE source_ticket_id = ANY({array}) OR ticket_id = ANY({array})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE approval_processes SET reason = NULL WHERE ticket_id = ANY({array})", cancellationToken);
        if (staffTextToo)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE approval_decisions SET comment = NULL WHERE ticket_id = ANY({array})", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE request_tasks SET notes = NULL WHERE ticket_id = ANY({array})", cancellationToken);
        }

        // Anonymised tickets leave the search index: there is nothing left to find them by.
        var types = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.Type }).ToListAsync(cancellationToken);
        foreach (var t in types)
        {
            search.Delete(t.Type switch
            {
                TicketType.Incident => IncidentSearch.Type,
                TicketType.ServiceRequest => RequestSearch.Type,
                TicketType.Problem => ProblemSearch.Type,
                TicketType.Custom => RecordSearch.Type,
                _ => VoiceSearch.Type,
            }, t.Id.ToString());
        }

        return new ErasureCounts(tickets, comments, attachments, chat, emails, 0);
    }
}
