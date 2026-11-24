using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workspace;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task B4: the agent workspace's sample data. Canned responses for everyone, for the service desk
/// and one of the agent's own; a view the team lead shares with the service desk and one of the
/// agent's; and conversations on the sample incidents (the requester's message, a public reply and
/// an internal note that mentions the team lead), in the organisation's main language.
/// </summary>
internal sealed class DemoWorkspaceSeeder : IDemoSeeder
{
    public string Key => "b4-workspace";

    // After directory (110), SLA (120), notifications (130) and forms (140): the comments' notifications are
    // raised by the save like any others.
    public int Order => 150;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        if (await db.CannedResponses.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var arabic = context.Organisation.DefaultLanguage == LocalizedText.Arabic;
        var users = (await db.Users.Where(u => u.Email != null).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        UserAccount Persona(string persona) => users[context.Organisation.UsernameOf(context.Organisation.User(persona)!)];
        var agent = Persona("agent");
        var lead = Persona("lead");
        var admin = Persona("admin");
        var desk = await db.SupportGroups.AsNoTracking().OrderBy(g => g.CreatedAt).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        db.CannedResponses.Add(new CannedResponse
        {
            Scope = WorkspaceScope.Tenant,
            CreatedById = admin.Id,
            Title = new LocalizedText("Acknowledge", "إشعار بالاستلام"),
            Body = new LocalizedText(
                "Dear {{requester.name}},\n\nThank you for contacting us about {{ticket.number}}. We are looking into it and will update you shortly.\n\nKind regards,\n{{agent.name}}\n{{tenant.name}}",
                "تحية طيبة، {{requester.name}}\n\nنشكركم على تواصلكم بخصوص البلاغ رقم {{ticket.number}}، ونفيدكم بأننا نعمل على دراسته وسنوافيكم بالمستجدات قريباً.\n\nمع خالص التحية،\n{{agent.name}}\n{{tenant.name}}"),
        });
        db.CannedResponses.Add(new CannedResponse
        {
            Scope = WorkspaceScope.Tenant,
            CreatedById = admin.Id,
            Title = new LocalizedText("More information needed", "طلب معلومات إضافية"),
            Body = new LocalizedText(
                "Dear {{requester.name}},\n\nTo continue with {{ticket.number}}, could you tell us when the problem started and send a screenshot of any error message?\n\nKind regards,\n{{agent.name}}",
                "تحية طيبة، {{requester.name}}\n\nلمتابعة معالجة البلاغ رقم {{ticket.number}}، نرجو إفادتنا بموعد بدء المشكلة وإرفاق صورة لأي رسالة خطأ تظهر لكم.\n\nمع خالص التحية،\n{{agent.name}}"),
        });
        if (desk is { } deskId)
        {
            db.CannedResponses.Add(new CannedResponse
            {
                Scope = WorkspaceScope.Group,
                GroupId = deskId,
                CreatedById = lead.Id,
                Title = new LocalizedText("Restart and try again", "إعادة التشغيل والمحاولة مجدداً"),
                Body = new LocalizedText(
                    "Please restart your device and try again. If the problem continues, reply to this message and we will call you.",
                    "نرجو إعادة تشغيل الجهاز والمحاولة مرة أخرى، وفي حال استمرار المشكلة يُرجى الرد على هذه الرسالة وسنتواصل معكم هاتفياً."),
            });
            db.SavedViews.Add(new SavedView
            {
                Scope = WorkspaceScope.Group,
                GroupId = deskId,
                OwnerId = lead.Id,
                Name = new LocalizedText("Service desk queue", "قائمة مكتب الخدمة"),
                Filters = Filters(new { open = true, group = deskId.ToString(), assignee = "none", sort = "due" }),
            });
        }

        db.CannedResponses.Add(new CannedResponse
        {
            Scope = WorkspaceScope.Personal,
            OwnerId = agent.Id,
            CreatedById = agent.Id,
            Title = arabic ? new LocalizedText(null, "شكراً على المتابعة") : new LocalizedText("Thanks for the update", null),
            Body = arabic ? new LocalizedText(null, "نشكركم على المتابعة وسنوافيكم بالمستجدات.") : new LocalizedText("Thanks for the update; we will keep you posted.", null),
        });
        db.SavedViews.Add(new SavedView
        {
            Scope = WorkspaceScope.Personal,
            OwnerId = agent.Id,
            Name = new LocalizedText("High priority", "الأولوية العالية"),
            Filters = Filters(new { open = true, priority = 1, sort = "due" }),
        });

        // Conversations on the open sample incidents.
        var incidents = await db.Tickets.AsNoTracking()
            .Where(t => t.Type == TicketType.Incident && t.State != TicketState.Closed && t.State != TicketState.Resolved && t.ParentId == null)
            .OrderBy(t => t.Sequence).Take(3)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var language = context.Organisation.DefaultLanguage;
        var at = DateTimeOffset.UtcNow.AddHours(-3);
        foreach (var ticket in incidents)
        {
            db.TicketComments.Add(Comment(ticket, ticket.RequesterId, false, CommentVisibility.Public, language, at,
                arabic ? "ما زالت المشكلة قائمة هذا الصباح، هل من مستجدات؟" : "The problem is still there this morning. Is there any news?"));
            if (ticket.RespondedAt is not null)
            {
                db.TicketComments.Add(Comment(ticket, agent.Id, true, CommentVisibility.Public, language, at.AddMinutes(20),
                    arabic ? "نعمل على المعالجة حالياً، وسنوافيكم بالمستجدات خلال ساعة." : "We are working on it now and will update you within the hour."));
            }

            var note = Comment(ticket, agent.Id, true, CommentVisibility.Internal, language, at.AddMinutes(30),
                arabic ? $"@{lead.DisplayName} هل يمكن الموافقة على استبدال الجهاز؟" : $"@{lead.DisplayName} can we approve a replacement device?");
            db.TicketComments.Add(note);
            db.TicketCommentMentions.Add(new TicketCommentMention { CommentId = note.Id, TicketId = ticket.Id, UserId = lead.Id });
            at = at.AddMinutes(45);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TicketComment Comment(Ticket ticket, Guid author, bool byStaff, CommentVisibility visibility, string language, DateTimeOffset at, string body) =>
        new()
        {
            TicketId = ticket.Id, AuthorId = author, ByStaff = byStaff, Visibility = visibility, Language = language, Body = body, CreatedAt = at,
        };

    private static string Filters(object filters) => JsonSerializer.Serialize(filters);
}
