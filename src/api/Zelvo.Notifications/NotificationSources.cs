using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Notifications;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Notifications;

/// <summary>Notifications to queue for some people, found in the changes of one save.</summary>
/// <param name="EventType">Event (<see cref="NotificationEvents"/>).</param>
/// <param name="Recipients">People to notify.</param>
/// <param name="Values">Placeholder values, captured now (the worker formats them per recipient).</param>
/// <param name="LinkPath">Path in the web app without the language.</param>
/// <param name="TicketId">Ticket it is about.</param>
/// <param name="ArticleId">Article it is about.</param>
/// <param name="SlaAlertId">SLA alert it delivers.</param>
public sealed record NotificationRequest(
    string EventType,
    IReadOnlyCollection<Guid> Recipients,
    IReadOnlyDictionary<string, PlaceholderValue> Values,
    string? LinkPath,
    Guid? TicketId = null,
    Guid? ArticleId = null,
    Guid? SlaAlertId = null,
    Guid? CommentId = null);

/// <summary>Finds notifications in the pending changes of a save. Each module registers one.</summary>
public interface INotificationSource
{
    /// <summary>Notifications described by the pending changes of <paramref name="db"/>; <paramref name="actorId"/> is the person saving, if any.</summary>
    Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken);
}

/// <summary>
/// Before each save: asks the registered <see cref="INotificationSource"/>s which notifications the
/// changes describe and queues one <see cref="Notification"/> per recipient in the same transaction,
/// so a notification exists if and only if its change was saved. The worker renders and delivers them.
/// </summary>
public sealed class NotificationInterceptor(IEnumerable<INotificationSource> sources, ITenantContext tenantContext, IAuditContext auditContext)
    : SaveChangesInterceptor, IZelvoSaveChangesInterceptor
{
    private bool _collecting;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        CollectAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await CollectAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task CollectAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (_collecting || context is not ZelvoDbContext db || tenantContext.TenantId is not { } tenantId)
        {
            return;
        }

        _collecting = true;
        try
        {
            db.ChangeTracker.DetectChanges();
            Guid? actor = auditContext.ActorType == AuditActorType.User && Guid.TryParse(auditContext.ActorId, out var id) ? id : null;
            var now = DateTimeOffset.UtcNow;
            var queued = new HashSet<(string, Guid, Guid?, Guid?, Guid?)>();
            foreach (var source in sources)
            {
                foreach (var request in await source.CollectAsync(db, actor, cancellationToken).ConfigureAwait(false))
                {
                    var data = NotificationData.Serialize(request.Values);
                    foreach (var recipient in request.Recipients.Distinct())
                    {
                        // The same person once per event and subject in one save.
                        if (!queued.Add((request.EventType, recipient, request.TicketId, request.ArticleId, request.SlaAlertId)))
                        {
                            continue;
                        }

                        db.Notifications.Add(new Notification
                        {
                            TenantId = tenantId,
                            EventType = request.EventType,
                            RecipientId = recipient,
                            Data = data,
                            LinkPath = request.LinkPath,
                            TicketId = request.TicketId,
                            ArticleId = request.ArticleId,
                            SlaAlertId = request.SlaAlertId,
                            CommentId = request.CommentId,
                            CreatedAt = now,
                        });
                    }
                }
            }
        }
        finally
        {
            _collecting = false;
        }
    }
}

/// <summary>Values and names that several sources need.</summary>
internal static class SourceLookups
{
    /// <summary>Path of a ticket for its requester.</summary>
    public static string RequesterPath(Ticket ticket) => $"/my-incidents/{ticket.Id}";

    /// <summary>Path of a ticket for staff.</summary>
    public static string StaffPath(Guid ticketId) => $"/incidents/{ticketId}";

    /// <summary>Path of a ticket in the agent workspace (task B4).</summary>
    public static string WorkspacePath(Guid ticketId) => $"/workspace/{ticketId}";

    /// <summary>Names people see for <paramref name="ids"/>: the display name, else the email address.</summary>
    public static async Task<Dictionary<Guid, string>> NamesAsync(ZelvoDbContext db, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var users = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return users.ToDictionary(u => u.Id, u => u.DisplayName ?? u.Email ?? string.Empty);
    }

    /// <summary>The ticket with <paramref name="id"/>: the tracked one when the save has it, else from the database.</summary>
    public static async Task<Ticket?> TicketAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.ChangeTracker.Entries<Ticket>().FirstOrDefault(e => e.Entity.Id == id)?.Entity
        ?? await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);

    /// <summary>Priorities of <paramref name="tickets"/>, named from the tenant's matrix.</summary>
    public static async Task<Dictionary<Guid, PlaceholderValue>> PrioritiesAsync(ZelvoDbContext db, IReadOnlyCollection<Guid> tickets, CancellationToken cancellationToken)
    {
        if (tickets.Count == 0)
        {
            return [];
        }

        var tracked = db.ChangeTracker.Entries<Incident>().Where(e => tickets.Contains(e.Entity.TicketId))
            .ToDictionary(e => e.Entity.TicketId, e => e.Entity.Priority);
        var missing = tickets.Where(t => !tracked.ContainsKey(t)).ToList();
        if (missing.Count > 0)
        {
            foreach (var row in await db.Incidents.AsNoTracking().Where(i => missing.Contains(i.TicketId))
                .Select(i => new { i.TicketId, i.Priority }).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                tracked[row.TicketId] = row.Priority;
            }
        }

        var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken)
            .ConfigureAwait(false);
        return tracked.ToDictionary(p => p.Key, p => p.Value is >= 1 and <= PriorityMatrix.PriorityLevels
            ? PlaceholderValue.OfText(matrix.Priorities[p.Value - 1].ToText())
            : PlaceholderValue.OfText(string.Empty));
    }

    /// <summary>Names of groups.</summary>
    public static async Task<Dictionary<Guid, PlaceholderValue>> GroupNamesAsync(ZelvoDbContext db, IEnumerable<Guid?> groups, CancellationToken cancellationToken)
    {
        var wanted = groups.OfType<Guid>().Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var rows = await db.SupportGroups.AsNoTracking().Where(g => wanted.Contains(g.Id)).Select(g => new { g.Id, g.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(g => g.Id, g => PlaceholderValue.OfText(g.Name));
    }

    /// <summary>Whether <paramref name="property"/> changed in this save.</summary>
    public static bool Changed<TValue>(EntityEntry<Ticket> entry, System.Linq.Expressions.Expression<Func<Ticket, TValue>> property)
    {
        var p = entry.Property(property);
        return entry.State == EntityState.Modified && p.IsModified && !EqualityComparer<TValue>.Default.Equals(p.OriginalValue, p.CurrentValue);
    }
}

/// <summary>
/// Incident notifications (task B5): to the requester when their incident is received, waits for
/// them, is resolved or closed, and when a major-incident update is posted for requesters; to staff
/// when an incident is assigned to them or arrives in their group's queue. Nobody is notified of
/// their own action, except the confirmation that an incident was received.
/// </summary>
public sealed class IncidentNotificationSource : INotificationSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entries = db.ChangeTracker.Entries<Ticket>()
            .Where(e => e.Entity.Type == TicketType.Incident && e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        var updates = db.ChangeTracker.Entries<MajorIncidentUpdate>()
            .Where(e => e.State == EntityState.Added && e.Entity.Audience == UpdateAudience.Requesters)
            .Select(e => e.Entity)
            .ToList();
        if (entries.Count == 0 && updates.Count == 0)
        {
            return [];
        }

        var requests = new List<(string Event, Ticket Ticket, IReadOnlyCollection<Guid> To, string Path)>();
        var queuedGroups = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            var ticket = entry.Entity;
            var notActor = (Guid id) => id != actorId;
            if (entry.State == EntityState.Added)
            {
                requests.Add((NotificationEvents.IncidentReceived, ticket, [ticket.RequesterId], SourceLookups.RequesterPath(ticket)));
            }
            else
            {
                if (SourceLookups.Changed(entry, t => t.State) && notActor(ticket.RequesterId))
                {
                    var stateEvent = ticket.State switch
                    {
                        TicketState.OnHold when string.Equals(ticket.OnHoldReason, OnHoldReason.AwaitingRequester, StringComparison.Ordinal)
                            => NotificationEvents.IncidentAwaitingRequester,
                        TicketState.Resolved => NotificationEvents.IncidentResolved,
                        TicketState.Closed => NotificationEvents.IncidentClosed,
                        _ => null,
                    };
                    if (stateEvent is not null)
                    {
                        requests.Add((stateEvent, ticket, [ticket.RequesterId], SourceLookups.RequesterPath(ticket)));
                    }
                }
            }

            var assigned = entry.State == EntityState.Added ? ticket.AssigneeId is not null : SourceLookups.Changed(entry, t => t.AssigneeId);
            if (assigned && ticket.AssigneeId is { } assignee && notActor(assignee))
            {
                requests.Add((NotificationEvents.IncidentAssigned, ticket, [assignee], SourceLookups.StaffPath(ticket.Id)));
            }
            else if (ticket.AssigneeId is null && ticket.GroupId is { } group
                && (entry.State == EntityState.Added || SourceLookups.Changed(entry, t => t.GroupId)))
            {
                queuedGroups.Add(group);
                requests.Add((NotificationEvents.IncidentQueued, ticket, [], SourceLookups.StaffPath(ticket.Id)));
            }
        }

        var memberRows = queuedGroups.Count == 0
            ? []
            : await db.SupportGroupMembers.AsNoTracking().Where(m => queuedGroups.Contains(m.GroupId))
                .Select(m => new { m.GroupId, m.UserId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var members = memberRows.ToLookup(m => m.GroupId, m => m.UserId);

        // Major-incident updates go to the requester of the incident and of each child incident, linked to their own incident.
        var updateRequests = new List<(MajorIncidentUpdate Update, Ticket Major, Ticket Own)>();
        foreach (var update in updates)
        {
            if (await SourceLookups.TicketAsync(db, update.TicketId, cancellationToken).ConfigureAwait(false) is not { } major)
            {
                continue;
            }

            updateRequests.Add((update, major, major));
            var children = await db.Tickets.AsNoTracking().Where(t => t.ParentId == major.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            updateRequests.AddRange(children.Select(child => (update, major, child)));
        }

        var ticketIds = requests.Select(r => r.Ticket.Id).Concat(updateRequests.Select(u => u.Major.Id)).Distinct().ToList();
        var priorities = await SourceLookups.PrioritiesAsync(db, ticketIds, cancellationToken).ConfigureAwait(false);
        var groups = await SourceLookups.GroupNamesAsync(db, requests.Select(r => r.Ticket.GroupId), cancellationToken).ConfigureAwait(false);
        var names = await SourceLookups.NamesAsync(db, requests.Select(r => (Guid?)r.Ticket.RequesterId), cancellationToken).ConfigureAwait(false);

        Dictionary<string, PlaceholderValue> Values(Ticket ticket) => new(StringComparer.Ordinal)
        {
            [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
            [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
            [Placeholders.TicketCreatedAt] = PlaceholderValue.OfDate(ticket.CreatedAt),
            [Placeholders.TicketResolutionNotes] = PlaceholderValue.OfText(ticket.ResolutionNotes),
            [Placeholders.TicketPriority] = priorities.GetValueOrDefault(ticket.Id) ?? PlaceholderValue.OfText(string.Empty),
            [Placeholders.TicketGroup] = ticket.GroupId is { } g && groups.TryGetValue(g, out var name) ? name : PlaceholderValue.OfText(string.Empty),
            [Placeholders.RequesterName] = PlaceholderValue.OfText(names.GetValueOrDefault(ticket.RequesterId)),
        };

        var result = new List<NotificationRequest>();
        foreach (var (eventType, ticket, to, path) in requests)
        {
            var recipients = eventType == NotificationEvents.IncidentQueued
                ? members[ticket.GroupId!.Value].Where(m => m != actorId).ToList()
                : to;
            if (recipients.Count > 0)
            {
                result.Add(new(eventType, recipients, Values(ticket), path, TicketId: ticket.Id));
            }
        }

        foreach (var (update, major, own) in updateRequests)
        {
            if (own.RequesterId == actorId || own.RequesterId == update.PostedById)
            {
                continue;
            }

            var values = Values(major);
            values[Placeholders.UpdateText] = PlaceholderValue.OfText(update.Body);
            values[Placeholders.UpdatePostedAt] = PlaceholderValue.OfDate(update.PostedAt);
            result.Add(new(NotificationEvents.IncidentMajorUpdate, [own.RequesterId], values, SourceLookups.RequesterPath(own), TicketId: own.Id));
        }

        return result;
    }
}

/// <summary>
/// SLA alerts (task B3) delivered as notifications: each new <see cref="SlaAlert"/> notifies the
/// recipients it recorded (the assignee, the group's team leads, another group).
/// </summary>
public sealed class SlaNotificationSource : INotificationSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var alerts = db.ChangeTracker.Entries<SlaAlert>().Where(e => e.State == EntityState.Added).Select(e => e.Entity)
            .Where(a => a.RecipientIds.Count > 0).ToList();
        if (alerts.Count == 0)
        {
            return [];
        }

        var priorities = await SourceLookups.PrioritiesAsync(db, [.. alerts.Select(a => a.TicketId).Distinct()], cancellationToken).ConfigureAwait(false);
        var result = new List<NotificationRequest>();
        foreach (var alert in alerts)
        {
            if (await SourceLookups.TicketAsync(db, alert.TicketId, cancellationToken).ConfigureAwait(false) is not { } ticket)
            {
                continue;
            }

            var clock = db.ChangeTracker.Entries<TicketSla>().FirstOrDefault(e => e.Entity.Id == alert.TicketSlaId)?.Entity
                ?? await db.TicketSlas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == alert.TicketSlaId, cancellationToken).ConfigureAwait(false);
            var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
            {
                [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
                [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
                [Placeholders.TicketPriority] = priorities.GetValueOrDefault(ticket.Id) ?? PlaceholderValue.OfText(string.Empty),
                [Placeholders.SlaTarget] = PlaceholderValue.OfTerm(alert.Metric == SlaMetric.Response ? "sla.target.response" : "sla.target.resolution"),
                [Placeholders.SlaPercent] = PlaceholderValue.OfPercent(alert.Percent),
                [Placeholders.SlaDueAt] = PlaceholderValue.OfDate(clock?.DueAt ?? clock?.BreachedAt ?? alert.ReachedAt),
            };
            var eventType = alert.Percent == 100 ? NotificationEvents.SlaBreached : NotificationEvents.SlaThreshold;
            result.Add(new(eventType, alert.RecipientIds, values, SourceLookups.StaffPath(ticket.Id), TicketId: ticket.Id, SlaAlertId: alert.Id));
        }

        return result;
    }
}

/// <summary>
/// The knowledge review queue (task D1): when a version is submitted for review, people who may
/// publish are told, except its creator and last editor (four-eyes rule) and whoever submitted it.
/// </summary>
public sealed class KnowledgeNotificationSource : INotificationSource
{
    /// <summary>The permission to publish articles (<c>Permissions.KnowledgePublish</c> in the API).</summary>
    public const string PublishPermission = "knowledge.publish";

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var versions = db.ChangeTracker.Entries<KbArticleVersion>()
            .Where(e => e.Entity.State == ArticleVersionState.InReview
                && (e.State == EntityState.Added
                    || (e.State == EntityState.Modified && e.Property(v => v.State).OriginalValue != ArticleVersionState.InReview)))
            .Select(e => e.Entity)
            .ToList();
        if (versions.Count == 0)
        {
            return [];
        }

        var publishers = await db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active
                && u.Roles.Any(r => db.RoleGrants.Any(g => g.RoleId == r.RoleId && g.Permission == PublishPermission)))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await SourceLookups.NamesAsync(db, versions.Select(v => v.SubmittedById ?? v.LastEditedById ?? v.CreatedById), cancellationToken)
            .ConfigureAwait(false);
        var result = new List<NotificationRequest>();
        foreach (var version in versions)
        {
            var article = db.ChangeTracker.Entries<KbArticle>().FirstOrDefault(e => e.Entity.Id == version.ArticleId)?.Entity
                ?? await db.KbArticles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == version.ArticleId, cancellationToken).ConfigureAwait(false);
            if (article is null)
            {
                continue;
            }

            Guid?[] excluded = [version.CreatedById, version.LastEditedById, version.SubmittedById, actorId];
            var recipients = publishers.Where(p => !excluded.Contains(p)).ToList();
            if (recipients.Count == 0)
            {
                continue;
            }

            var author = version.SubmittedById ?? version.LastEditedById ?? version.CreatedById;
            var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
            {
                [Placeholders.ArticleNumber] = PlaceholderValue.OfText(article.Number),
                [Placeholders.ArticleTitle] = PlaceholderValue.OfText(version.Title),
                [Placeholders.AuthorName] = PlaceholderValue.OfText(author is { } a ? names.GetValueOrDefault(a) : null),
            };
            result.Add(new(NotificationEvents.KnowledgeReviewRequested, recipients, values, $"/knowledge/{article.Id}", ArticleId: article.Id));
        }

        return result;
    }
}

/// <summary>
/// Conversation notifications (task B4): the requester hears of staff public replies on their
/// incident; staff hear when they are @mentioned. Nobody is notified of their own comment.
/// </summary>
public sealed class CommentNotificationSource : INotificationSource
{
    /// <summary>Longest comment text put into a notification; the full text is on the ticket.</summary>
    internal const int TextLimit = 1500;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        // Task E2: a chat's transcript added to a ticket raises nothing; both sides saw it live.
        var comments = db.ChangeTracker.Entries<TicketComment>().Where(e => e.State == EntityState.Added && e.Entity.Source != CommentSource.Chat)
            .Select(e => e.Entity).ToList();
        if (comments.Count == 0)
        {
            return [];
        }

        var mentions = db.ChangeTracker.Entries<TicketCommentMention>().Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity).ToLookup(m => m.CommentId, m => m.UserId);
        var authors = await SourceLookups.NamesAsync(db, comments.Select(c => (Guid?)c.AuthorId), cancellationToken).ConfigureAwait(false);
        var result = new List<NotificationRequest>();
        foreach (var comment in comments)
        {
            if (await SourceLookups.TicketAsync(db, comment.TicketId, cancellationToken).ConfigureAwait(false) is not { Type: TicketType.Incident } ticket)
            {
                continue;
            }

            var text = comment.Body.Length <= TextLimit ? comment.Body : string.Concat(comment.Body.AsSpan(0, TextLimit - 1), "…");
            var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
            {
                [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
                [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
                [Placeholders.CommentText] = PlaceholderValue.OfText(text),
                [Placeholders.CommentAuthor] = PlaceholderValue.OfText(authors.GetValueOrDefault(comment.AuthorId)),
            };

            if (comment.Visibility == CommentVisibility.Public && comment.ByStaff && ticket.RequesterId != comment.AuthorId && ticket.RequesterId != actorId)
            {
                result.Add(new(NotificationEvents.IncidentReplied, [ticket.RequesterId], values, SourceLookups.RequesterPath(ticket), TicketId: ticket.Id,
                    CommentId: comment.Id));
            }

            var mentioned = mentions[comment.Id].Where(u => u != comment.AuthorId && u != actorId).Distinct().ToList();
            if (mentioned.Count > 0)
            {
                result.Add(new(NotificationEvents.IncidentMentioned, mentioned, values, SourceLookups.WorkspacePath(ticket.Id), TicketId: ticket.Id));
            }
        }

        return result;
    }
}
