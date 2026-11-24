namespace Zelvo.Notifications;

/// <summary>Who a notification event is for; decides which people see it under Account → Notifications.</summary>
public enum NotificationAudience
{
    /// <summary>The person a ticket is for (portal users and staff alike).</summary>
    Requester,

    /// <summary>Agents and team leads working on tickets.</summary>
    Staff,

    /// <summary>People who publish knowledge articles.</summary>
    Publisher,

    /// <summary>People asked to approve requests (task C3); anyone can be an approver.</summary>
    Approver,
}

/// <summary>One kind of notification.</summary>
/// <param name="Type">Stable key, for example <c>incident.assigned</c>.</param>
/// <param name="Audience">Who it is for.</param>
/// <param name="EmailOptional">
/// Whether a person may turn its email off (product owner decision 2026-11-24: SLA alerts cannot be turned off).
/// </param>
/// <param name="Placeholders">Placeholders its templates may use, besides <see cref="NotificationEvents.Common"/>.</param>
public sealed record NotificationEventDefinition(string Type, NotificationAudience Audience, bool EmailOptional, IReadOnlyList<string> Placeholders)
{
    /// <summary>Every placeholder its templates may use.</summary>
    public IReadOnlyList<string> AllPlaceholders { get; } = [.. NotificationEvents.Common, .. Placeholders];
}

/// <summary>
/// The notifications of task B5 (product owner decision 2026-11-24): updates to requesters about
/// their own incidents, assignment to staff, SLA alerts and the knowledge review queue. Each can be
/// switched off by administrators; their wording is a template they can change. The no-code rule
/// builder (task C3) adds more later.
/// </summary>
public static class NotificationEvents
{
    /// <summary>A requester's incident was logged.</summary>
    public const string IncidentReceived = "incident.received";

    /// <summary>An incident was put on hold waiting for its requester.</summary>
    public const string IncidentAwaitingRequester = "incident.awaiting-requester";

    /// <summary>An incident was resolved; the requester can reopen it until it closes.</summary>
    public const string IncidentResolved = "incident.resolved";

    /// <summary>An incident was closed.</summary>
    public const string IncidentClosed = "incident.closed";

    /// <summary>A major-incident update meant for requesters was posted.</summary>
    public const string IncidentMajorUpdate = "incident.major-update";

    /// <summary>An incident was assigned to someone (not by themselves).</summary>
    public const string IncidentAssigned = "incident.assigned";

    /// <summary>An incident arrived in a group's queue without an assignee.</summary>
    public const string IncidentQueued = "incident.queued";

    /// <summary>A staff member posted a public reply on a requester's incident (task B4).</summary>
    public const string IncidentReplied = "incident.replied";

    /// <summary>Someone was @mentioned in a comment on an incident (task B4).</summary>
    public const string IncidentMentioned = "incident.mentioned";

    /// <summary>
    /// The AI found the requester of an incident frustrated (task E5; product owner decision
    /// 2026-11-24): the group's team leads hear about it, or the assignee when the group has none.
    /// </summary>
    public const string IncidentRequesterFrustrated = "incident.requester-frustrated";

    /// <summary>A ticket reached an escalation threshold of an SLA target (task B3).</summary>
    public const string SlaThreshold = "sla.threshold";

    /// <summary>A ticket passed an SLA target.</summary>
    public const string SlaBreached = "sla.breached";

    /// <summary>A knowledge article version waits for review (four-eyes rule, task D1).</summary>
    public const string KnowledgeReviewRequested = "knowledge.review-requested";

    /// <summary>A requester's complaint, inquiry or suggestion was received (task C6).</summary>
    public const string VoiceReceived = "voice.received";

    /// <summary>Staff replied publicly on a complaint, inquiry or suggestion.</summary>
    public const string VoiceReplied = "voice.replied";

    /// <summary>A complaint was resolved; the requester confirms or rejects the resolution.</summary>
    public const string ComplaintResolved = "complaint.resolved";

    /// <summary>An inquiry was answered (resolved).</summary>
    public const string InquiryAnswered = "inquiry.answered";

    /// <summary>A suggestion's review changed, with feedback to the submitter.</summary>
    public const string SuggestionReviewed = "suggestion.reviewed";

    /// <summary>A complaint, inquiry or suggestion was assigned to someone (not by themselves).</summary>
    public const string VoiceAssigned = "voice.assigned";

    /// <summary>A complaint, inquiry or suggestion arrived in a group's queue without an assignee.</summary>
    public const string VoiceQueued = "voice.queued";
    /// <summary>An approver is asked to decide on a request (task C3); the email has Approve and Reject buttons.</summary>
    public const string ApprovalRequested = "approval.requested";

    /// <summary>A request was approved and goes to the service desk (task C3).</summary>
    public const string RequestApproved = "request.approved";

    /// <summary>A request was rejected before assignment and closed, with the reason (task C3).</summary>
    public const string RequestRejected = "request.rejected";

    /// <summary>The sign-off of a fulfilled request was rejected; the work goes back to the assignee (task C3).</summary>
    public const string RequestSignOffRejected = "request.signoff-rejected";

    /// <summary>
    /// A notify action of a workflow rule (task C3). Its wording is the rule's own, carried in the
    /// notification's values; it is not listed in Settings → Notifications or Account → Notifications.
    /// </summary>
    public const string WorkflowRule = "workflow.rule";

    /// <summary>Event type of test emails sent from Settings → Notifications.</summary>
    public const string TestEmail = "notifications.test";

    /// <summary>Placeholders every template may use.</summary>
    public static IReadOnlyList<string> Common { get; } = [Placeholders.RecipientName, Placeholders.TenantName];

    /// <summary>Every event, in the order Settings shows them.</summary>
    public static IReadOnlyList<NotificationEventDefinition> All { get; } =
    [
        new(IncidentReceived, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketCreatedAt]),
        new(IncidentAwaitingRequester, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle]),
        new(IncidentResolved, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketResolutionNotes]),
        new(IncidentClosed, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle]),
        new(IncidentMajorUpdate, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.UpdateText, Placeholders.UpdatePostedAt]),
        new(IncidentAssigned, NotificationAudience.Staff, true,
            [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketPriority, Placeholders.TicketGroup, Placeholders.RequesterName]),
        new(IncidentQueued, NotificationAudience.Staff, true,
            [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketPriority, Placeholders.TicketGroup, Placeholders.RequesterName]),
        new(IncidentReplied, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.CommentText, Placeholders.CommentAuthor]),
        new(IncidentMentioned, NotificationAudience.Staff, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.CommentText, Placeholders.CommentAuthor]),
        new(IncidentRequesterFrustrated, NotificationAudience.Staff, true,
            [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketPriority, Placeholders.TicketGroup, Placeholders.RequesterName]),
        new(SlaThreshold, NotificationAudience.Staff, false,
            [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketPriority, Placeholders.SlaTarget, Placeholders.SlaPercent, Placeholders.SlaDueAt]),
        new(SlaBreached, NotificationAudience.Staff, false,
            [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketPriority, Placeholders.SlaTarget, Placeholders.SlaDueAt]),
        new(KnowledgeReviewRequested, NotificationAudience.Publisher, true, [Placeholders.ArticleNumber, Placeholders.ArticleTitle, Placeholders.AuthorName]),
        new(VoiceReceived, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketType, Placeholders.TicketCreatedAt]),
        new(VoiceReplied, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketType, Placeholders.CommentText, Placeholders.CommentAuthor]),
        new(ComplaintResolved, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketResolutionNotes]),
        new(InquiryAnswered, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketResolutionNotes]),
        new(SuggestionReviewed, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.ReviewState, Placeholders.ReviewFeedback]),
        new(VoiceAssigned, NotificationAudience.Staff, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketType, Placeholders.TicketGroup, Placeholders.RequesterName]),
        new(VoiceQueued, NotificationAudience.Staff, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.TicketType, Placeholders.TicketGroup, Placeholders.RequesterName]),
        new(ApprovalRequested, NotificationAudience.Approver, true,
            [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.RequesterName, Placeholders.ServiceName, Placeholders.ApprovalLevel]),
        new(RequestApproved, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.ServiceName]),
        new(RequestRejected, NotificationAudience.Requester, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.ServiceName, Placeholders.ApprovalReason]),
        new(RequestSignOffRejected, NotificationAudience.Staff, true, [Placeholders.TicketNumber, Placeholders.TicketTitle, Placeholders.ApprovalReason]),
    ];

    /// <summary>Events that are not set up in Settings (their wording comes from elsewhere).</summary>
    public static IReadOnlyList<NotificationEventDefinition> Internal { get; } =
    [
        new(WorkflowRule, NotificationAudience.Staff, false, [Placeholders.RuleSubject, Placeholders.RuleBody]),
    ];

    /// <summary>The event with key <paramref name="type"/>, or <see langword="null"/>.</summary>
    public static NotificationEventDefinition? Find(string? type) =>
        All.FirstOrDefault(e => string.Equals(e.Type, type, StringComparison.Ordinal))
        ?? Internal.FirstOrDefault(e => string.Equals(e.Type, type, StringComparison.Ordinal));
}

/// <summary>Placeholder names (<c>{{ticket.number}}</c> in templates).</summary>
public static class Placeholders
{
    /// <summary>The recipient's name.</summary>
    public const string RecipientName = "recipient.name";

    /// <summary>The organisation's name in the recipient's language.</summary>
    public const string TenantName = "tenant.name";

    /// <summary>Ticket number, for example INC-000123.</summary>
    public const string TicketNumber = "ticket.number";

    /// <summary>Ticket title as the requester wrote it.</summary>
    public const string TicketTitle = "ticket.title";

    /// <summary>When the ticket was logged.</summary>
    public const string TicketCreatedAt = "ticket.createdAt";

    /// <summary>How the ticket was resolved.</summary>
    public const string TicketResolutionNotes = "ticket.resolutionNotes";

    /// <summary>Priority name from the tenant's matrix.</summary>
    public const string TicketPriority = "ticket.priority";

    /// <summary>The group's name.</summary>
    public const string TicketGroup = "ticket.group";

    /// <summary>The requester's name.</summary>
    public const string RequesterName = "requester.name";

    /// <summary>Text of a major-incident update.</summary>
    public const string UpdateText = "update.text";

    /// <summary>When the update was posted.</summary>
    public const string UpdatePostedAt = "update.postedAt";

    /// <summary>Text of a reply or note (task B4).</summary>
    public const string CommentText = "comment.text";

    /// <summary>Who wrote the reply or note.</summary>
    public const string CommentAuthor = "comment.author";

    /// <summary>"response" or "resolution".</summary>
    public const string SlaTarget = "sla.target";

    /// <summary>Share of the target reached.</summary>
    public const string SlaPercent = "sla.percent";

    /// <summary>When the target is (or was) due.</summary>
    public const string SlaDueAt = "sla.dueAt";

    /// <summary>Article number, for example KB-000123.</summary>
    public const string ArticleNumber = "article.number";

    /// <summary>Article title.</summary>
    public const string ArticleTitle = "article.title";

    /// <summary>Who submitted the version for review.</summary>
    public const string AuthorName = "author.name";

    /// <summary>Record type in words: complaint, inquiry or suggestion (task C6).</summary>
    public const string TicketType = "ticket.type";

    /// <summary>A suggestion's review state in words.</summary>
    public const string ReviewState = "review.state";

    /// <summary>Feedback to a suggestion's submitter.</summary>
    public const string ReviewFeedback = "review.feedback";
    /// <summary>The requested service's name (task C3).</summary>
    public const string ServiceName = "service.name";

    /// <summary>The approval level's name.</summary>
    public const string ApprovalLevel = "approval.level";

    /// <summary>Why a request was rejected.</summary>
    public const string ApprovalReason = "approval.reason";

    /// <summary>The ticket's state, named in the recipient's language.</summary>
    public const string TicketState = "ticket.state";

    /// <summary>A workflow rule's subject, in both languages (its own placeholders are filled in when sent).</summary>
    public const string RuleSubject = "rule.subject";

    /// <summary>A workflow rule's message.</summary>
    public const string RuleBody = "rule.body";

    /// <summary>Whether a workflow rule's notification also goes by email ("true").</summary>
    public const string RuleEmail = "rule.email";
}
