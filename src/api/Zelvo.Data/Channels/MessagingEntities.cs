using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Channels;

/// <summary>A messaging channel's kind (module 6, task E3).</summary>
public enum MessagingKind
{
    /// <summary>WhatsApp Business.</summary>
    WhatsApp,

    /// <summary>Text messages through an SMS gateway.</summary>
    Sms,

    /// <summary>Microsoft Teams, through the tenant's own Teams app (a bot).</summary>
    Teams,
}

/// <summary>How Zelvo talks to a messaging channel (product owner decisions 2026-11-25).</summary>
public enum MessagingProvider
{
    /// <summary>Meta's WhatsApp Cloud API (Graph API); incoming messages by a signed webhook.</summary>
    WhatsAppCloud,

    /// <summary>Twilio's Programmable Messaging (SMS).</summary>
    Twilio,

    /// <summary>Any SMS gateway with an HTTP API, set up on screen (Etisalat, du, Unifonic, Infobip and others).</summary>
    GenericHttp,

    /// <summary>A Microsoft Teams bot (Bot Framework), registered in the customer's Entra directory.</summary>
    TeamsBot,
}

/// <summary>Who may raise tickets through a channel without an account.</summary>
public enum MessagingSenders
{
    /// <summary>Anyone: a requester account is made for the phone number.</summary>
    Anyone,

    /// <summary>Only people whose phone number (or Teams account) belongs to a Zelvo user.</summary>
    KnownUsers,
}

/// <summary>
/// A WhatsApp number, SMS gateway or Teams app of a tenant (module 6, task E3). Credentials are
/// encrypted with the tenant's data key (<c>Zelvo.Secrets</c>) and never returned; a change of
/// account, number, gateway address or app needs them again.
/// </summary>
public sealed class MessagingChannel : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="AccountId"/>, <see cref="Sender"/> and <see cref="DirectoryId"/>.</summary>
    public const int IdentifierMaxLength = 200;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Unique id (UUID version 7); part of the channel's webhook address.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators, for example "Customer care WhatsApp".</summary>
    public required string Name { get; set; }

    /// <summary>WhatsApp, SMS or Teams.</summary>
    public MessagingKind Kind { get; set; }

    /// <summary>The provider.</summary>
    public MessagingProvider Provider { get; set; }

    /// <summary>
    /// The account: the WhatsApp phone number id, the Twilio account SID, or the Teams app's
    /// Microsoft app (client) id.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// Who messages come from: the WhatsApp number as shown to people, the SMS number, sender
    /// name or Twilio messaging service SID.
    /// </summary>
    public string? Sender { get; set; }

    /// <summary>The WhatsApp business account id, or the Entra directory (tenant) id of the Teams app.</summary>
    public string? DirectoryId { get; set; }

    /// <summary>
    /// Provider settings that are not secret, as JSON: the generic SMS gateway's request and answer
    /// mapping, and WhatsApp's message templates per notification event and language.
    /// </summary>
    public string Settings { get; set; } = "{}";

    /// <summary>
    /// The credential, encrypted: WhatsApp access token, Twilio auth token, the gateway's secret
    /// or the Teams app's client secret.
    /// </summary>
    [SensitiveData]
    public string? CredentialCiphertext { get; set; }

    /// <summary>When the credential was last set.</summary>
    public DateTimeOffset? CredentialSetAt { get; set; }

    /// <summary>
    /// The secret incoming webhooks are checked with, encrypted: WhatsApp's app secret (signatures)
    /// and verify token, or the generic gateway's token. Twilio uses the auth token; Teams Microsoft's signed tokens.
    /// </summary>
    [SensitiveData]
    public string? WebhookSecretCiphertext { get; set; }

    /// <summary>Who may raise tickets without an account.</summary>
    public MessagingSenders NewSenders { get; set; } = MessagingSenders.Anyone;

    /// <summary>Category of new tickets (then the assignment rules route them).</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Group of new tickets (instead of the assignment rules).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Whether messages are taken in and sent.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When the last message came in.</summary>
    public DateTimeOffset? LastInboundAt { get; set; }

    /// <summary>The last error (sending, a refused webhook), for administrators.</summary>
    public string? LastError { get; set; }

    /// <summary>When <see cref="LastError"/> happened.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// One person on one channel: a WhatsApp or SMS phone number, or a Teams account. Holds the
/// person's Zelvo account, the ticket their messages go to, when they last wrote (WhatsApp's
/// 24-hour window) and, for Teams, where to reach them.
/// </summary>
public sealed class MessagingContact : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Address"/>.</summary>
    public const int AddressMaxLength = 200;

    /// <summary>Maximum length of the Teams values.</summary>
    public const int TeamsValueMaxLength = 500;

    /// <summary>Unique id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The channel.</summary>
    public Guid ChannelId { get; init; }

    /// <summary>The phone number in E.164 (<c>+971501234567</c>), or the Teams account's Entra object id.</summary>
    [SensitiveData]
    public required string Address { get; init; }

    /// <summary>The person's Zelvo account, once known.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The ticket the person's messages are added to while it is open.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>When the person last wrote (WhatsApp lets businesses answer freely for 24 hours after it).</summary>
    public DateTimeOffset? LastInboundAt { get; set; }

    /// <summary>Teams: the service address of the conversation.</summary>
    public string? TeamsServiceUrl { get; set; }

    /// <summary>Teams: the personal conversation's id.</summary>
    public string? TeamsConversationId { get; set; }

    /// <summary>Teams: the person's id in the bot's conversations (<c>29:…</c>).</summary>
    public string? TeamsUserId { get; set; }

    /// <summary>When it was first seen.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Whether a channel message came in or goes out.</summary>
public enum MessageDirection
{
    /// <summary>From the person to Zelvo.</summary>
    In,

    /// <summary>From Zelvo to the person.</summary>
    Out,
}

/// <summary>What a channel message holds.</summary>
public enum ChannelMessageKind
{
    /// <summary>Text.</summary>
    Text,

    /// <summary>A voice note.</summary>
    Voice,

    /// <summary>An image, document or other file.</summary>
    Media,

    /// <summary>WhatsApp: an approved message template (outside the 24-hour window).</summary>
    Template,

    /// <summary>Teams: an Adaptive Card (approvals).</summary>
    Card,

    /// <summary>Teams: a button pressed on a card.</summary>
    Action,

    /// <summary>A delivery or read receipt, or another event without text.</summary>
    Event,
}

/// <summary>What became of a channel message.</summary>
public enum ChannelMessageStatus
{
    /// <summary>Came in and was taken in.</summary>
    Received,

    /// <summary>Came in and was left alone (a receipt, a duplicate, a kind Zelvo does not take).</summary>
    Ignored,

    /// <summary>Came in from someone the channel does not accept.</summary>
    Rejected,

    /// <summary>Waits to be sent (or to be tried again).</summary>
    Pending,

    /// <summary>The provider accepted it.</summary>
    Sent,

    /// <summary>The provider reported it delivered.</summary>
    Delivered,

    /// <summary>The provider reported it read.</summary>
    Read,

    /// <summary>Could not be sent.</summary>
    Failed,
}

/// <summary>
/// The channel log and outgoing queue: every message that came in through a channel or goes out
/// through it, with what became of it. Not audited (the tickets and comments it makes are). Kept 30 days.
/// </summary>
public sealed class ChannelMessage : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Text"/>.</summary>
    public const int TextMaxLength = 4096;

    /// <summary>Maximum length of <see cref="ProviderMessageId"/>.</summary>
    public const int ProviderIdMaxLength = 300;

    /// <summary>Maximum length of <see cref="Reason"/>.</summary>
    public const int ReasonMaxLength = 200;

    /// <summary>Sequential id.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The channel.</summary>
    public Guid ChannelId { get; init; }

    /// <summary>The person.</summary>
    public Guid? ContactId { get; set; }

    /// <summary>In or out.</summary>
    public MessageDirection Direction { get; init; }

    /// <summary>What it holds.</summary>
    public ChannelMessageKind Kind { get; set; }

    /// <summary>The provider's id of the message (incoming messages are taken once).</summary>
    public string? ProviderMessageId { get; set; }

    /// <summary>Where it goes (outgoing): phone number in E.164 or the Teams contact's address.</summary>
    [SensitiveData]
    public string? Address { get; set; }

    /// <summary>The text (outgoing: what is sent; incoming: what came).</summary>
    public string? Text { get; set; }

    /// <summary>WhatsApp template or Teams card, as JSON, for outgoing messages that are not plain text.</summary>
    public string? Payload { get; set; }

    /// <summary>The language it is in (<c>en</c> or <c>ar</c>).</summary>
    public string? Language { get; set; }

    /// <summary>What became of it.</summary>
    public ChannelMessageStatus Status { get; set; }

    /// <summary>Why (a rejection, an ignored kind, a provider's error code).</summary>
    public string? Reason { get; set; }

    /// <summary>Sending attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When it is sent (again).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>The worker holding it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>The ticket it belongs to.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>The comment it made or carries.</summary>
    public Guid? CommentId { get; set; }

    /// <summary>The notification it carries.</summary>
    public Guid? NotificationId { get; init; }

    /// <summary>The approval decision it asks for (Teams cards, WhatsApp links).</summary>
    public Guid? ApprovalDecisionId { get; init; }

    /// <summary>When it came in or was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the provider accepted it.</summary>
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Where a voice note's transcript stands.</summary>
public enum TranscriptStatus
{
    /// <summary>Waits for the AI.</summary>
    Pending,

    /// <summary>Transcribed.</summary>
    Done,

    /// <summary>The AI could not transcribe it.</summary>
    Failed,

    /// <summary>Transcription was off when it came in; the recording is the message.</summary>
    Off,
}

/// <summary>
/// A voice note sent through WhatsApp (task E3): the comment it is, its recording (an
/// attachment) and, once transcription is on, the text the AI heard. Comments never change, so
/// the transcript is kept here and shown with the comment.
/// </summary>
public sealed class VoiceNote : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Transcript"/>.</summary>
    public const int TranscriptMaxLength = 20_000;

    /// <summary>The comment (one voice note each).</summary>
    public Guid CommentId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The recording.</summary>
    public Guid AttachmentId { get; init; }

    /// <summary>Where the transcript stands.</summary>
    public TranscriptStatus Status { get; set; }

    /// <summary>What the AI heard, in the language spoken.</summary>
    public string? Transcript { get; set; }

    /// <summary>The language the AI heard (<c>en</c> or <c>ar</c>), when it said.</summary>
    public string? Language { get; set; }

    /// <summary>The AI call that made it (AI log).</summary>
    public long? AiCallId { get; set; }

    /// <summary>When it came in.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was transcribed.</summary>
    public DateTimeOffset? TranscribedAt { get; set; }
}
