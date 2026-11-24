using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data.Channels;

/// <summary>How Zelvo reaches a mailbox.</summary>
public enum MailboxProvider
{
    /// <summary>IMAP to read and SMTP to send: Exchange on-premises and any other mail server.</summary>
    Imap,

    /// <summary>Microsoft 365 through Microsoft Graph.</summary>
    Microsoft365,

    /// <summary>Gmail (Google Workspace) through the Gmail API.</summary>
    Gmail,
}

/// <summary>How Zelvo proves it may use the mailbox.</summary>
public enum MailboxAuth
{
    /// <summary>IMAP and SMTP user name and password (or an app password).</summary>
    Password,

    /// <summary>Microsoft 365: an app registration in the customer's Entra ID with a client secret (application permissions).</summary>
    ClientSecret,

    /// <summary>Gmail: a Google Workspace service account with domain-wide delegation (its JSON key).</summary>
    ServiceAccount,

    /// <summary>Microsoft 365 or Gmail: an administrator signed in to the mailbox and granted access (delegated; refresh token kept).</summary>
    OAuthSignIn,
}

/// <summary>TLS for IMAP and SMTP.</summary>
public enum MailSecurity
{
    /// <summary>TLS on connect for ports 993 and 465, STARTTLS when offered otherwise.</summary>
    Auto,

    /// <summary>TLS from the first byte.</summary>
    SslOnConnect,

    /// <summary>STARTTLS required.</summary>
    StartTls,

    /// <summary>No TLS (only for a relay inside the same network).</summary>
    None,
}

/// <summary>
/// Who may raise tickets by email (product owner decision 2026-11-24: a setting per mailbox,
/// "anyone" by default). Rejected emails get no automatic reply and are listed in the mailbox's log.
/// </summary>
public enum NewSenderPolicy
{
    /// <summary>Anyone; senders without an account become requesters.</summary>
    Anyone,

    /// <summary>People with an account, and anyone whose address is in one of the allowed domains (who becomes a requester).</summary>
    AllowedDomains,

    /// <summary>Only people who already have an account.</summary>
    KnownUsers,
}

/// <summary>Whether Zelvo reads a mailbox.</summary>
public enum MailboxStatus
{
    /// <summary>Read every poll interval; replies go out from it.</summary>
    Active,

    /// <summary>Not read; replies of its tickets wait.</summary>
    Disabled,
}

/// <summary>
/// A mailbox that turns emails into tickets (module 6, task E2). Several per tenant. Credentials
/// are encrypted with the tenant's data key (<c>Zelvo.Secrets</c>) and never returned. The worker
/// reads it every poll interval (no push: many installations are offline).
/// </summary>
public sealed class Mailbox : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of an address.</summary>
    public const int AddressMaxLength = 254;

    /// <summary>Maximum length of a host name.</summary>
    public const int HostMaxLength = 253;

    /// <summary>Maximum length of <see cref="Username"/>, <see cref="ClientId"/> and <see cref="DirectoryId"/>.</summary>
    public const int IdentifierMaxLength = 254;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators, for example "IT support".</summary>
    public required string Name { get; set; }

    /// <summary>The mailbox's address, for example <c>support@entity.gov.ae</c>; replies come from it.</summary>
    public required string Address { get; set; }

    /// <summary>How Zelvo reaches it.</summary>
    public MailboxProvider Provider { get; set; }

    /// <summary>How Zelvo proves access.</summary>
    public MailboxAuth Auth { get; set; }

    /// <summary>IMAP host.</summary>
    public string? ImapHost { get; set; }

    /// <summary>IMAP port.</summary>
    public int? ImapPort { get; set; }

    /// <summary>IMAP TLS.</summary>
    public MailSecurity ImapSecurity { get; set; }

    /// <summary>SMTP host.</summary>
    public string? SmtpHost { get; set; }

    /// <summary>SMTP port.</summary>
    public int? SmtpPort { get; set; }

    /// <summary>SMTP TLS.</summary>
    public MailSecurity SmtpSecurity { get; set; }

    /// <summary>IMAP and SMTP user name.</summary>
    public string? Username { get; set; }

    /// <summary>Microsoft: the app's client id; Google: the OAuth client id (sign-in) — for service accounts the key holds it.</summary>
    public string? ClientId { get; set; }

    /// <summary>Microsoft: the Entra directory (tenant) id or domain.</summary>
    public string? DirectoryId { get; set; }

    /// <summary>
    /// The credential, encrypted (<c>zv1.…</c>): password, client secret, service account key, or
    /// for <see cref="MailboxAuth.OAuthSignIn"/> a JSON object with the client secret and the refresh token.
    /// </summary>
    [SensitiveData]
    public string? CredentialCiphertext { get; set; }

    /// <summary>When the credential was last set.</summary>
    public DateTimeOffset? CredentialSetAt { get; set; }

    /// <summary>
    /// With <see cref="MailboxAuth.OAuthSignIn"/>: the sign-in an administrator started and has not
    /// finished (JSON with the state's hash, the PKCE verifier and the expiry), encrypted.
    /// </summary>
    [SensitiveData]
    public string? OAuthPending { get; set; }

    /// <summary>With <see cref="MailboxAuth.OAuthSignIn"/>: the account that granted access.</summary>
    public string? SignedInAs { get; set; }

    /// <summary>Who may raise tickets by email.</summary>
    public NewSenderPolicy NewSenders { get; set; } = NewSenderPolicy.Anyone;

    /// <summary>With <see cref="NewSenderPolicy.AllowedDomains"/>: domains such as <c>entity.gov.ae</c>.</summary>
    public string[] AllowedDomains { get; set; } = [];

    /// <summary>Category of new tickets (the assignment rules then route them).</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Group of new tickets; when set, the assignment rules are not used.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Whether Zelvo reads it.</summary>
    public MailboxStatus Status { get; set; } = MailboxStatus.Active;

    /// <summary>Where reading got to (JSON, provider specific: IMAP UIDVALIDITY and last UID). Not audited.</summary>
    [AuditLengthOnly]
    public string? SyncState { get; set; }

    /// <summary>A worker is reading it until this time.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>When it is read next.</summary>
    public DateTimeOffset NextCheckAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last read.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Whether the last read worked.</summary>
    public bool? LastCheckSucceeded { get; set; }

    /// <summary>Why the last read failed.</summary>
    public string? LastError { get; set; }

    /// <summary>Reads that failed in a row (the interval grows with them).</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When it was set up.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>What Zelvo did with an email.</summary>
public enum InboundEmailOutcome
{
    /// <summary>A new ticket.</summary>
    Created,

    /// <summary>Added to the conversation of an open ticket.</summary>
    Added,

    /// <summary>A reply to a closed ticket: a new follow-up ticket.</summary>
    FollowUp,

    /// <summary>Refused (for example the sender may not raise tickets); no reply sent.</summary>
    Rejected,

    /// <summary>Left alone on purpose (automatic replies, bulk mail, mail Zelvo sent itself).</summary>
    Ignored,

    /// <summary>Zelvo could not process it; it is tried again with the next read.</summary>
    Failed,
}

/// <summary>
/// The mailbox log: one row per email read (task E2), so administrators see what became of each
/// one. Written by Zelvo, not audited (the ticket and message it created are). Kept 90 days.
/// </summary>
public sealed class InboundEmail : ITenantOwned
{
    /// <summary>Maximum length of <see cref="ProviderMessageId"/>.</summary>
    public const int ProviderIdMaxLength = 500;

    /// <summary>Maximum length of <see cref="Subject"/>.</summary>
    public const int SubjectMaxLength = 500;

    /// <summary>Maximum length of <see cref="Reason"/>.</summary>
    public const int ReasonMaxLength = 64;

    /// <summary>Position in the log.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The mailbox.</summary>
    public Guid MailboxId { get; init; }

    /// <summary>The provider's id of the email (IMAP UIDVALIDITY:UID, Graph or Gmail id); read once.</summary>
    public required string ProviderMessageId { get; init; }

    /// <summary>The email's <c>Message-ID</c>.</summary>
    public string? MessageId { get; set; }

    /// <summary>The sender's address (personal data: shown masked in the log).</summary>
    public string? FromAddress { get; set; }

    /// <summary>The subject.</summary>
    public string? Subject { get; set; }

    /// <summary>What Zelvo did with it.</summary>
    public InboundEmailOutcome Outcome { get; set; }

    /// <summary>Why, as a code: <c>sender-not-allowed</c>, <c>auto-reply</c>, <c>dmarc-failed</c>, …</summary>
    public string? Reason { get; set; }

    /// <summary>Attempts that failed.</summary>
    public int Attempts { get; set; }

    /// <summary>The ticket it created or was added to.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>The comment it became on the ticket's conversation.</summary>
    public Guid? CommentId { get; set; }

    /// <summary>When Zelvo read it.</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When Zelvo last processed it.</summary>
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Where an outgoing reply stands.</summary>
public enum OutboundEmailStatus
{
    /// <summary>Waiting to be sent.</summary>
    Pending,

    /// <summary>Sent.</summary>
    Sent,

    /// <summary>Given up.</summary>
    Failed,
}

/// <summary>
/// An email to the requester of a ticket that came in by email, going out from the ticket's mailbox
/// in the same thread (task E2): staff replies and the requester's notifications (received,
/// resolved, …), rendered with the B5 wording in the requester's language. Sent by the worker with
/// retries. Written by Zelvo, not audited (the message is). Kept 30 days.
/// </summary>
public sealed class OutboundEmail : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Subject"/>.</summary>
    public const int SubjectMaxLength = 500;

    /// <summary>Maximum length of <see cref="References"/>.</summary>
    public const int ReferencesMaxLength = 4000;

    /// <summary>Position in the queue.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The mailbox it goes out from.</summary>
    public Guid MailboxId { get; init; }

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The staff reply (comment) it sends; empty for other requester notifications (received, resolved, …).</summary>
    public Guid? CommentId { get; init; }

    /// <summary>The notification it delivers (task B5 wording, in the requester's language).</summary>
    public Guid? NotificationId { get; init; }

    /// <summary>The recipient (the requester).</summary>
    public required string ToAddress { get; init; }

    /// <summary>Subject, with the ticket number in brackets.</summary>
    public required string Subject { get; init; }

    /// <summary>Its <c>Message-ID</c>, without angle brackets.</summary>
    public required string MessageId { get; init; }

    /// <summary>The email it answers (<c>In-Reply-To</c>).</summary>
    public string? InReplyTo { get; init; }

    /// <summary>Earlier emails of the thread (<c>References</c>, space separated).</summary>
    public string? References { get; init; }

    /// <summary>The language it is written in (layout direction).</summary>
    public required string Language { get; init; }

    /// <summary>HTML part.</summary>
    public required string HtmlBody { get; init; }

    /// <summary>Plain-text part.</summary>
    public required string TextBody { get; init; }

    /// <summary>Pending, sent or failed.</summary>
    public OutboundEmailStatus Status { get; set; } = OutboundEmailStatus.Pending;

    /// <summary>Attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When it is tried next.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A worker is sending it until this time.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Why the last attempt failed.</summary>
    public string? LastError { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was sent.</summary>
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>
/// A live chat widget (module 6, task E2): shown on the portal and/or embedded in other websites
/// with one script tag. Chats go to one support group, whose available members with
/// <c>chat.handle</c> accept them (product owner decision 2026-11-24).
/// </summary>
public sealed class ChatWidget : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of each language of <see cref="Greeting"/>.</summary>
    public const int GreetingMaxLength = 500;

    /// <summary>Maximum length of one allowed origin.</summary>
    public const int OriginMaxLength = 200;

    /// <summary>Most allowed origins per widget.</summary>
    public const int MaxOrigins = 20;

    /// <summary>Unique id (UUID version 7); part of the embed code.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Title of the chat window in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>First message visitors see, in English and Arabic (either may be empty).</summary>
    public LocalizedText Greeting { get; set; } = new(null, null);

    /// <summary>The group whose members answer.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Category of tickets made from its chats.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Websites allowed to embed it (<c>https://www.entity.gov.ae</c>); the portal never needs listing.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Shown on Zelvo's own portal pages.</summary>
    public bool OnPortal { get; set; }

    /// <summary>Inactive widgets accept no chats.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Where a chat stands.</summary>
public enum ChatState
{
    /// <summary>Waiting in the group's queue.</summary>
    Waiting,

    /// <summary>An agent accepted it.</summary>
    Active,

    /// <summary>Over.</summary>
    Ended,
}

/// <summary>Who ended a chat.</summary>
public enum ChatEndedBy
{
    /// <summary>The visitor.</summary>
    Visitor,

    /// <summary>The agent.</summary>
    Agent,

    /// <summary>Zelvo, after the visitor left it idle.</summary>
    System,
}

/// <summary>
/// One live chat (task E2). Visitors who are not signed in give their name and email first
/// (product owner decision 2026-11-24) and prove the chat is theirs with a random token, of which
/// only a hash is stored. The agent decides whether it becomes a ticket.
/// </summary>
public sealed class ChatSession : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="VisitorName"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of <see cref="Origin"/>.</summary>
    public const int OriginMaxLength = 200;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The widget it started from.</summary>
    public Guid WidgetId { get; init; }

    /// <summary>The group whose queue it is in (changes when transferred).</summary>
    public Guid GroupId { get; set; }

    /// <summary>The signed-in visitor, or the requester account once a ticket is made.</summary>
    public Guid? VisitorUserId { get; set; }

    /// <summary>The visitor's name.</summary>
    public required string VisitorName { get; init; }

    /// <summary>The visitor's email address.</summary>
    [SensitiveData]
    public required string VisitorEmail { get; init; }

    /// <summary>SHA-256 of the visitor's chat token (hex); empty for signed-in visitors.</summary>
    [SensitiveData]
    public string? VisitorTokenHash { get; init; }

    /// <summary>The visitor's language (<c>en</c> or <c>ar</c>).</summary>
    public required string Language { get; init; }

    /// <summary>The website it started on (origin only), or null on the portal.</summary>
    public string? Origin { get; init; }

    /// <summary>Waiting, active or ended.</summary>
    public ChatState State { get; set; } = ChatState.Waiting;

    /// <summary>The agent answering.</summary>
    public Guid? AgentId { get; set; }

    /// <summary>The agent (navigation).</summary>
    public Identity.UserAccount? Agent { get; init; }

    /// <summary>The ticket made from it, or linked to it.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When an agent first accepted it.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>When it ended.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Who ended it.</summary>
    public ChatEndedBy? EndedBy { get; set; }

    /// <summary>When the visitor last wrote or looked (idle chats end on their own).</summary>
    public DateTimeOffset LastVisitorActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>), so two agents cannot both accept it.</summary>
    public uint Version { get; init; }
}

/// <summary>Who wrote a chat message.</summary>
public enum ChatSender
{
    /// <summary>The visitor.</summary>
    Visitor,

    /// <summary>An agent.</summary>
    Agent,

    /// <summary>Zelvo: what happened in the chat (accepted, transferred, ended, ticket made).</summary>
    System,
}

/// <summary>A message of a live chat. Audited with the text's length only.</summary>
public sealed class ChatMessage : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 4000;

    /// <summary>Maximum length of <see cref="Event"/>.</summary>
    public const int EventMaxLength = 40;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Position across all chats; clients ask for messages after the last one they have.</summary>
    public long Sequence { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The chat.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Visitor, agent or Zelvo.</summary>
    public ChatSender Sender { get; init; }

    /// <summary>The agent who wrote it, or the agent a system event names.</summary>
    public Guid? AuthorId { get; init; }

    /// <summary>The text; for system messages empty (the event says what happened). Only the length is audited.</summary>
    [AuditLengthOnly]
    public required string Body { get; init; }

    /// <summary>System messages: <c>accepted</c>, <c>transferred</c>, <c>ended</c>, <c>ticket</c>.</summary>
    public string? Event { get; init; }

    /// <summary>When it was written.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
