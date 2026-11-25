using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Api.Workflows;
using Zelvo.Channels;
using Zelvo.Channels.Messaging;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Tenancy;
using Zelvo.Notifications;
using Zelvo.Secrets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Channels;

/// <summary>
/// Incoming webhooks of WhatsApp, SMS gateways and Microsoft Teams (module 6, task E3). They are
/// reached from the internet through the reverse proxy (<c>/hooks/{tenant}/…</c>), carry no Zelvo
/// sign-in and are trusted only after the provider's proof: Meta's signature with the app secret,
/// Twilio's signature with the auth token, the gateway's token, or a token Bot Framework signed.
/// </summary>
internal static class MessagingHooks
{
    public static RouteGroupBuilder MapMessagingHooks(this RouteGroupBuilder group)
    {
        var hooks = group.MapGroup("/hooks").WithTags("Channels").RequireTenant();
        hooks.MapGet("/whatsapp/{id:guid}", WhatsAppVerifyAsync)
            .WithName("VerifyWhatsAppWebhook")
            .WithSummary("Meta's check when the webhook is registered: echoes hub.challenge when hub.verify_token is the channel's verify token.")
            .AllowAnonymousAccess()
            .Produces<string>(contentType: "text/plain");
        hooks.MapPost("/whatsapp/{id:guid}", WhatsAppAsync)
            .WithName("ReceiveWhatsApp")
            .WithSummary("Messages and delivery reports from WhatsApp, signed by Meta (X-Hub-Signature-256 with the app secret); anything else is refused. Messages become tickets or replies; voice notes are kept and transcribed when that AI feature is on.")
            .AllowAnonymousAccess()
            .Produces(StatusCodes.Status200OK);
        hooks.MapPost("/sms/{id:guid}", SmsAsync)
            .WithName("ReceiveSms")
            .WithSummary("Text messages and delivery reports from Twilio (X-Twilio-Signature with the auth token) or a generic gateway (the channel's token in the address or X-Zelvo-Token).")
            .AllowAnonymousAccess()
            .Produces(StatusCodes.Status200OK);
        hooks.MapPost("/teams/{id:guid}", TeamsAsync)
            .WithName("ReceiveTeams")
            .WithSummary("Activities from Microsoft Teams with a token Bot Framework signed for the app: personal messages become tickets, Approve and Reject on a card decide the approval as the linked person (product owner decision 2026-11-25).")
            .AllowAnonymousAccess()
            .Produces(StatusCodes.Status200OK);
        return group;
    }

    internal static async Task<IResult> WhatsAppVerifyAsync(Guid id, HttpRequest request, ZelvoDbContext db, ISecretProtector secrets, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.Kind == MessagingKind.WhatsApp, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var webhook = (await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken)).WebhookSecret;
        return request.Query["hub.mode"] == "subscribe" && webhook is not null && WebhookSignatures.SameSecret(request.Query["hub.verify_token"], webhook.VerifyToken)
            ? TypedResults.Text(request.Query["hub.challenge"].ToString(), "text/plain")
            : ApiProblem.Create(StatusCodes.Status403Forbidden, "verify-token-wrong", "The verify token is not this channel's.");
    }

    internal static async Task<IResult> WhatsAppAsync(Guid id, HttpRequest request, ZelvoDbContext db, ISecretProtector secrets, MessagingIntake intake,
        IMessagingClients clients, ChannelOptions options, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.SingleOrDefaultAsync(c => c.Id == id && c.Kind == MessagingKind.WhatsApp, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var body = await ReadBodyAsync(request, cancellationToken);
        var keys = await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken);
        if (keys.WebhookSecret?.AppSecret is not { } appSecret || !WebhookSignatures.WhatsAppValid(body, request.Headers["X-Hub-Signature-256"], appSecret))
        {
            return Refused();
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return TypedResults.Ok();
        }

        foreach (var change in (json?["entry"] as JsonArray ?? []).SelectMany(e => e?["changes"] as JsonArray ?? []))
        {
            var value = change?["value"];
            // Each channel is one number: others' messages are not ours.
            if (value?["metadata"]?["phone_number_id"]?.GetValue<string>() is { } number && number != channel.AccountId)
            {
                continue;
            }

            var names = (value?["contacts"] as JsonArray ?? []).Where(c => c?["wa_id"] is not null)
                .ToDictionary(c => c!["wa_id"]!.GetValue<string>(), c => c!["profile"]?["name"]?.GetValue<string>(), StringComparer.Ordinal);
            foreach (var message in value?["messages"] as JsonArray ?? [])
            {
                if (message?["from"]?.GetValue<string>() is not { } from || message["id"]?.GetValue<string>() is not { } providerId)
                {
                    continue;
                }

                var inbound = await WhatsAppMessageAsync(channel, keys, clients, options, message, "+" + from, names.GetValueOrDefault(from), providerId, cancellationToken);
                await intake.ProcessAsync(channel, keys, inbound, cancellationToken);
            }

            foreach (var status in value?["statuses"] as JsonArray ?? [])
            {
                if (status?["id"]?.GetValue<string>() is { } providerId)
                {
                    var error = (status["errors"] as JsonArray)?.FirstOrDefault()?["code"]?.ToString();
                    await intake.ReportAsync(channel, providerId, status["status"]?.GetValue<string>() switch
                    {
                        "sent" => ChannelMessageStatus.Sent,
                        "delivered" => ChannelMessageStatus.Delivered,
                        "read" => ChannelMessageStatus.Read,
                        "failed" => ChannelMessageStatus.Failed,
                        _ => ChannelMessageStatus.Sent,
                    }, error, cancellationToken);
                }
            }
        }

        return TypedResults.Ok();
    }

    private static async Task<InboundMessage> WhatsAppMessageAsync(
        MessagingChannel channel, MessagingSecrets keys, IMessagingClients clients, ChannelOptions options, JsonNode message, string from, string? name, string providerId,
        CancellationToken cancellationToken)
    {
        var type = message["type"]?.GetValue<string>();
        string? Text(string path) => JsonPath.Text(message, path);
        switch (type)
        {
            case "text":
                return new InboundMessage(providerId, from, name, ChannelMessageKind.Text, Text("text.body"));
            case "button":
                return new InboundMessage(providerId, from, name, ChannelMessageKind.Text, Text("button.text"));
            case "interactive":
                return new InboundMessage(providerId, from, name, ChannelMessageKind.Text, Text("interactive.button_reply.title") ?? Text("interactive.list_reply.title"));
            case "audio" or "image" or "document" or "video":
                MessagingMedia? media = null;
                if (Text($"{type}.id") is { } mediaId)
                {
                    try
                    {
                        media = await clients.DownloadAsync(channel, keys, mediaId, options.MaxMediaBytes, cancellationToken);
                    }
                    catch (MessagingException)
                    {
                        // The message still counts; the file is left out (its log entry says so).
                        media = null;
                    }
                }

                var kind = type == "audio" ? ChannelMessageKind.Voice : ChannelMessageKind.Media;
                return new InboundMessage(providerId, from, name, kind, Text($"{type}.caption"), media, Text($"{type}.filename"));
            default:
                return new InboundMessage(providerId, from, name, ChannelMessageKind.Event, null);
        }
    }

    internal static async Task<IResult> SmsAsync(Guid id, HttpRequest request, ZelvoDbContext db, ISecretProtector secrets, MessagingIntake intake, ChannelOptions options,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.SingleOrDefaultAsync(c => c.Id == id && c.Kind == MessagingKind.Sms, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var keys = await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken);
        if (channel.Provider == MessagingProvider.Twilio)
        {
            if (!request.HasFormContentType)
            {
                return Refused();
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var fields = form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value.ToString())).ToList();
            var url = MessagingEndpoints.WebhookUrl(options, tenant.Slug!, channel);
            if (keys.Credential is not { } token || !WebhookSignatures.TwilioValid(url, fields, request.Headers["X-Twilio-Signature"], token))
            {
                return Refused();
            }

            var sid = form["MessageSid"].ToString();
            if (form.ContainsKey("MessageStatus") && !form.ContainsKey("Body"))
            {
                await intake.ReportAsync(channel, sid, form["MessageStatus"].ToString() switch
                {
                    "delivered" => ChannelMessageStatus.Delivered,
                    "read" => ChannelMessageStatus.Read,
                    "failed" or "undelivered" => ChannelMessageStatus.Failed,
                    _ => ChannelMessageStatus.Sent,
                }, form["ErrorCode"].ToString() is { Length: > 0 } code ? code : null, cancellationToken);
            }
            else if (sid.Length > 0 && Normalise(form["From"].ToString()) is { } from)
            {
                await intake.ProcessAsync(channel, keys, new InboundMessage(sid, from, null, ChannelMessageKind.Text, form["Body"].ToString()), cancellationToken);
            }

            // Twilio expects TwiML; an empty response sends no automatic answer.
            return TypedResults.Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response/>", "text/xml");
        }

        var gateway = MessagingSettings.Read(channel.Settings).Gateway;
        var given = request.Query["token"].ToString() is { Length: > 0 } query ? query : request.Headers["X-Zelvo-Token"].ToString();
        if (gateway is null || keys.WebhookSecret is not { } webhook || !WebhookSignatures.SameSecret(given, webhook.VerifyToken))
        {
            return Refused();
        }

        string? Field(string? name, JsonNode? json, IFormCollection? fields) =>
            name is null ? null : json is not null ? JsonPath.Text(json, name) : fields?[name].ToString() is { Length: > 0 } v ? v : null;
        JsonNode? body = null;
        IFormCollection? formFields = null;
        if (request.HasFormContentType)
        {
            formFields = await request.ReadFormAsync(cancellationToken);
        }
        else
        {
            try
            {
                body = JsonNode.Parse(await ReadBodyAsync(request, cancellationToken));
            }
            catch (JsonException)
            {
                return ApiProblem.Create(StatusCodes.Status400BadRequest, "bad-request", "Send JSON or form fields.");
            }
        }

        var sender = Normalise(Field(gateway.InboundFrom, body, formFields));
        var text = Field(gateway.InboundText, body, formFields);
        if (sender is null || string.IsNullOrWhiteSpace(text))
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, "bad-request", $"The message needs the fields {gateway.InboundFrom} and {gateway.InboundText}.");
        }

        var providerId = Field(gateway.InboundId, body, formFields)
            ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{sender}\n{text}\n{DateTimeOffset.UtcNow:yyyyMMddHHmm}")));
        await intake.ProcessAsync(channel, keys, new InboundMessage(providerId, sender, null, ChannelMessageKind.Text, text), cancellationToken);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> TeamsAsync(
        Guid id, HttpRequest request, ZelvoDbContext db, ISecretProtector secrets, MessagingIntake intake, TeamsTokenValidator validator, MessagingClients http,
        ApprovalService approvals, AuditContext auditContext, Zelvo.Tickets.Sla.SlaTracker sla, NotificationComposer composer, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.SingleOrDefaultAsync(c => c.Id == id && c.Kind == MessagingKind.Teams, cancellationToken);
        if (channel?.AccountId is null)
        {
            return NotFound();
        }

        JsonNode? activity;
        try
        {
            activity = JsonNode.Parse(await ReadBodyAsync(request, cancellationToken));
        }
        catch (JsonException)
        {
            return Refused();
        }

        var serviceUrl = JsonPath.Text(activity, "serviceUrl");
        if (!await validator.ValidAsync(request.Headers.Authorization.ToString(), channel.AccountId, serviceUrl, cancellationToken))
        {
            return Refused();
        }

        // Only the customer's own directory, and only personal chats with the app.
        var directory = JsonPath.Text(activity, "channelData.tenant.id") ?? JsonPath.Text(activity, "conversation.tenantId");
        if (channel.DirectoryId is { } own && Guid.TryParse(own, out _) && !string.Equals(directory, own, StringComparison.OrdinalIgnoreCase)
            || JsonPath.Text(activity, "conversation.conversationType") is { } type && type != "personal")
        {
            return TypedResults.Ok();
        }

        try
        {
            http.TeamsServiceUrl(serviceUrl!);
        }
        catch (MessagingException)
        {
            return Refused();
        }

        var objectId = JsonPath.Text(activity, "from.aadObjectId");
        var conversationId = JsonPath.Text(activity, "conversation.id");
        var userId = JsonPath.Text(activity, "from.id");
        if (objectId is null || conversationId is null || userId is null)
        {
            return TypedResults.Ok();
        }

        var keys = await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken);
        var teams = new TeamsDetails(serviceUrl!, conversationId, userId);
        var providerId = JsonPath.Text(activity, "id") ?? Guid.NewGuid().ToString("N");
        var locale = JsonPath.Text(activity, "locale");
        switch (JsonPath.Text(activity, "type"))
        {
            case "conversationUpdate" when (activity!["membersAdded"] as JsonArray ?? []).Any(m => JsonPath.Text(m, "id") == JsonPath.Text(activity, "recipient.id")):
            {
                // The app was added: remember the conversation, link the person and welcome them.
                var entry = await intake.ProcessAsync(channel, keys, new InboundMessage(providerId, objectId, JsonPath.Text(activity, "from.name"), ChannelMessageKind.Event, null, Teams: teams),
                    cancellationToken);
                var contact = await db.MessagingContacts.SingleAsync(c => c.Id == entry.ContactId, cancellationToken);
                await LinkAsync(db, channel, keys, intake, contact, teams, cancellationToken);
                var language = await LanguageAsync(db, composer, contact.UserId, locale, cancellationToken);
                var example = await db.Tickets.AsNoTracking().Select(t => t.Number).FirstOrDefaultAsync(cancellationToken) ?? "INC-000123";
                Reply(db, channel, contact, Term(language, "teams.welcome").Replace("{number}", example, StringComparison.Ordinal), language);
                await db.SaveChangesAsync(cancellationToken);
                return TypedResults.Ok();
            }

            case "message" when activity!["value"] is JsonObject value && JsonPath.Text(value, "zelvo") == "approval":
                return await DecideAsync(db, channel, keys, intake, approvals, auditContext, sla, composer, value, objectId, teams, locale, cancellationToken);

            case "message":
                await intake.ProcessAsync(channel, keys, new InboundMessage(providerId, objectId, JsonPath.Text(activity, "from.name"), ChannelMessageKind.Text,
                    StripMentions(JsonPath.Text(activity, "text")), Teams: teams), cancellationToken);
                return TypedResults.Ok();

            default:
                return TypedResults.Ok();
        }
    }

    // Approve or Reject on a card: decided as the person Microsoft signed the activity for, linked to their Zelvo account.
    private static async Task<IResult> DecideAsync(
        ZelvoDbContext db, MessagingChannel channel, MessagingSecrets keys, MessagingIntake intake, ApprovalService approvals, AuditContext auditContext,
        Zelvo.Tickets.Sla.SlaTracker sla, NotificationComposer composer, JsonObject value, string objectId, TeamsDetails teams, string? locale, CancellationToken cancellationToken)
    {
        var contact = await db.MessagingContacts.FirstOrDefaultAsync(c => c.ChannelId == channel.Id && c.Address == objectId, cancellationToken);
        if (contact is null)
        {
            contact = new MessagingContact { TenantId = channel.TenantId, ChannelId = channel.Id, Address = objectId };
            db.MessagingContacts.Add(contact);
        }

        contact.TeamsServiceUrl = teams.ServiceUrl;
        contact.TeamsConversationId = teams.ConversationId;
        contact.TeamsUserId = teams.UserId;
        await LinkAsync(db, channel, keys, intake, contact, teams, cancellationToken);
        var language = await LanguageAsync(db, composer, contact.UserId, locale, cancellationToken);
        var user = contact.UserId is { } userId ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.Status == Data.Identity.UserStatus.Active, cancellationToken) : null;
        if (user is null)
        {
            var tenantName = (await composer.LetterheadAsync(cancellationToken)).Name.Get(language) ?? string.Empty;
            Reply(db, channel, contact, Term(language, "teams.not-linked").Replace("{tenant}", tenantName, StringComparison.Ordinal), language);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok();
        }

        var decision = Guid.TryParse(JsonPath.Text(value, "decisionId"), out var decisionId)
            ? await db.ApprovalDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == decisionId, cancellationToken)
            : null;
        if (decision is null || decision.ApproverId != user.Id)
        {
            Reply(db, channel, contact, Term(language, "teams.not-allowed"), language);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok();
        }

        var approve = JsonPath.Text(value, "decision") == "approve";
        var reason = JsonPath.Text(value, "reason")?.Trim();
        if (reason is { Length: > Data.Workflows.ApprovalDecision.CommentMaxLength })
        {
            reason = reason[..Data.Workflows.ApprovalDecision.CommentMaxLength];
        }

        // The decision is the person's own: audited as them, through Teams.
        auditContext.SetActor(AuditActorType.User, user.Id.ToString(), user.DisplayName ?? user.Email);
        auditContext.SetMetadata("via", "teams");
        var number = await db.Tickets.AsNoTracking().Where(t => t.Id == decision.TicketId).Select(t => t.Number).SingleAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var error = await approvals.DecideAsync(decisionId, approve, string.IsNullOrEmpty(reason) ? null : reason, "teams", cancellationToken);
        db.ChangeTracker.DetectChanges();
        var tickets = db.ChangeTracker.Entries<Data.Tickets.Ticket>().Where(e => e.State == EntityState.Modified).Select(e => e.Entity).ToList();
        if (tickets.Count > 0)
        {
            await sla.UpdateAsync(tickets, DateTimeOffset.UtcNow, cancellationToken);
        }

        var answer = error switch
        {
            DecisionError.ReasonRequired => Term(language, "teams.reason-required"),
            DecisionError.AlreadyDecided => Term(language, "teams.already-decided"),
            _ => Term(language, approve ? "teams.approved" : "teams.rejected").Replace("{number}", number, StringComparison.Ordinal),
        };
        Reply(db, channel, contact, answer, language);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok();
    }

    // Links the Teams account to the Zelvo account with its email, once; nobody is created here.
    private static async Task LinkAsync(ZelvoDbContext db, MessagingChannel channel, MessagingSecrets keys, MessagingIntake intake, MessagingContact contact, TeamsDetails teams,
        CancellationToken cancellationToken)
    {
        if (contact.UserId is not null)
        {
            return;
        }

        var user = await intake.FindTeamsUserAsync(channel, keys, teams, cancellationToken);
        contact.UserId = user?.Id;
    }

    private static void Reply(ZelvoDbContext db, MessagingChannel channel, MessagingContact contact, string text, string language) =>
        db.ChannelMessages.Add(new ChannelMessage
        {
            TenantId = channel.TenantId,
            ChannelId = channel.Id,
            ContactId = contact.Id,
            Direction = MessageDirection.Out,
            Kind = ChannelMessageKind.Text,
            Address = contact.Address,
            Text = text,
            Language = language,
            Status = ChannelMessageStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow,
        });

    private static async Task<string> LanguageAsync(ZelvoDbContext db, NotificationComposer composer, Guid? userId, string? locale, CancellationToken cancellationToken)
    {
        var user = userId is { } id ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken) : null;
        if (user is not null)
        {
            return (await composer.FormatAsync(user, cancellationToken)).Language;
        }

        return locale?.StartsWith("ar", StringComparison.OrdinalIgnoreCase) == true ? "ar" : "en";
    }

    private static string Term(string language, string key) => NotificationTexts.For(language).Terms.GetValueOrDefault(key) ?? key;

    // Teams puts <at>App</at> in front of messages that mention the app.
    private static string? StripMentions(string? text) =>
        text is null ? null : System.Text.RegularExpressions.Regex.Replace(text, "<at>[^<]*</at>", string.Empty).Trim();

    private static string? Normalise(string? number) =>
        string.IsNullOrWhiteSpace(number) ? null : FieldFormats.Phone(number) ?? (number.Trim().All(char.IsDigit) ? FieldFormats.Phone("+" + number.Trim()) : null);

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static IResult Refused() => ApiProblem.Create(StatusCodes.Status401Unauthorized, "signature-invalid", "The request is not signed by the channel's provider.");

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "channel-not-found", "The channel does not exist.");
}
