using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Channels;
using Zelvo.Channels.Messaging;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Tenancy;
using Zelvo.Notifications;
using Zelvo.Secrets;

namespace Zelvo.Api.Channels;

/// <summary>
/// Settings → Messaging (module 6, task E3; product owner decisions 2026-11-25): WhatsApp Business
/// through Meta's Cloud API, SMS through Twilio or a generic HTTP gateway, and Microsoft Teams through
/// the tenant's own Teams app. Credentials are encrypted and never returned; a new account, number,
/// gateway address or app needs them again. Every change is audited; the channel log masks numbers.
/// </summary>
internal static partial class MessagingEndpoints
{
    public static RouteGroupBuilder MapMessagingEndpoints(this RouteGroupBuilder group)
    {
        var channels = group.MapGroup("/messaging/channels").WithTags("Channels").RequireTenant();
        channels.MapGet("/", ListAsync)
            .WithName("ListMessagingChannels")
            .WithSummary("The tenant's WhatsApp numbers, SMS gateways and Teams apps with their webhook addresses. Credentials are never returned. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<IReadOnlyList<MessagingChannelResponse>>();
        channels.MapGet("/{id:guid}", GetAsync)
            .WithName("GetMessagingChannel")
            .WithSummary("One channel. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MessagingChannelResponse>();
        channels.MapPost("/", CreateAsync)
            .WithName("CreateMessagingChannel")
            .WithSummary("Adds a channel: WhatsApp (Meta Cloud API: phone number id, access token, app secret; WhatsApp templates per event), SMS (Twilio account SID, auth token and sender; or a generic HTTP gateway set up on screen) or Microsoft Teams (app id, client secret, Entra directory). Who may raise tickets without an account; category or group of new tickets. Credentials are stored encrypted. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MessagingChannelResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        channels.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateMessagingChannel")
            .WithSummary("Changes a channel; values left out stay. A new account, number, directory or gateway address needs the credential again. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MessagingChannelResponse>()
            .ProducesValidationProblem();
        channels.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteMessagingChannel")
            .WithSummary("Removes a channel with its contacts and log. Its tickets stay; their updates then go by email and in-app. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces(StatusCodes.Status204NoContent);
        channels.MapPost("/{id:guid}/test", TestAsync)
            .WithName("TestMessagingChannel")
            .WithSummary("Checks the account and credential with the provider without sending anything; with `to`, also queues a test message to that number (WhatsApp, SMS). Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MessagingTestResponse>()
            .ProducesValidationProblem();
        channels.MapGet("/{id:guid}/log", LogAsync)
            .WithName("ListMessagingChannelLog")
            .WithSummary("Messages in and out of the channel, newest first, with what became of each; numbers masked. Kept 30 days. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<PageResult<ChannelMessageResponse>>();
        channels.MapPost("/{id:guid}/log/{messageId:long}/resend", ResendAsync)
            .WithName("ResendChannelMessage")
            .WithSummary("Sends a message that failed again. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<ChannelMessageResponse>();
        channels.MapGet("/{id:guid}/teams-app", TeamsAppAsync)
            .WithName("DownloadTeamsApp")
            .WithSummary("The Teams app package (manifest in English and Arabic, icons) of a Teams channel, to upload in the Teams admin centre. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip");

        var mine = group.MapGroup("/me/notification-channels").WithTags("Notifications").RequireTenant().RequirePerson();
        mine.MapGet("/", MyChannelsAsync)
            .WithName("GetMyNotificationChannels")
            .WithSummary("Whether your notifications also go by WhatsApp and SMS (to the phone of your profile) and to Microsoft Teams, and which of these your organisation offers.")
            .Produces<MyNotificationChannelsResponse>();
        mine.MapPut("/", UpdateMyChannelsAsync)
            .WithName("UpdateMyNotificationChannels")
            .WithSummary("Turns WhatsApp, SMS and Teams notifications on or off for you. WhatsApp and SMS need a phone number on your profile. Audited.")
            .Produces<MyNotificationChannelsResponse>()
            .ProducesValidationProblem();
        return group;
    }

    /// <summary>The address providers send a channel's incoming messages to (through the reverse proxy's <c>/hooks/</c>).</summary>
    internal static Uri WebhookUrl(ChannelOptions options, string tenantSlug, MessagingChannel channel) =>
        new(options.PublicUrl, $"hooks/{tenantSlug}/{channel.Kind switch { MessagingKind.WhatsApp => "whatsapp", MessagingKind.Sms => "sms", _ => "teams" }}/{channel.Id}");

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, ISecretProtector secrets, ChannelOptions options, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var list = new List<MessagingChannelResponse>();
        foreach (var channel in await db.MessagingChannels.AsNoTracking().OrderBy(c => c.Kind).ThenBy(c => c.Name).ToListAsync(cancellationToken))
        {
            list.Add(await ResponseAsync(channel, secrets, options, tenant, cancellationToken));
        }

        return TypedResults.Ok(list);
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, ISecretProtector secrets, ChannelOptions options, ITenantContext tenant, CancellationToken cancellationToken) =>
        await db.MessagingChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is { } channel
            ? TypedResults.Ok(await ResponseAsync(channel, secrets, options, tenant, cancellationToken))
            : NotFound();

    internal static async Task<IResult> CreateAsync(
        MessagingChannelRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, ChannelHttp http, ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (request.Kind is not { } kind || request.Provider is not { } provider)
        {
            return UserEndpoints.Validation("kind", "Choose WhatsApp, SMS or Teams and its provider.");
        }

        var channel = new MessagingChannel { Name = string.Empty, Kind = kind, Provider = provider };
        if (await ApplyAsync(channel, request, db, http, creating: true, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (string.IsNullOrEmpty(request.Credential) && !(provider == MessagingProvider.GenericHttp && GatewayOf(channel)?.Auth == GatewayAuth.None))
        {
            return UserEndpoints.Validation("credential", CredentialLabel(provider) + " is needed.");
        }

        if (provider == MessagingProvider.WhatsAppCloud && string.IsNullOrEmpty(request.AppSecret))
        {
            return UserEndpoints.Validation("appSecret", "Enter the Meta app's secret, which signs incoming messages.");
        }

        if (!string.IsNullOrEmpty(request.Credential))
        {
            await SetCredentialAsync(channel, request.Credential, secrets, audit, cancellationToken);
        }

        // Webhook secrets: WhatsApp's app secret (from Meta) and a verify token, or the gateway's token (made by Zelvo).
        if (provider is MessagingProvider.WhatsAppCloud or MessagingProvider.GenericHttp)
        {
            await MessagingSecretStore.WriteWebhookAsync(secrets, channel, new WebhookSecrets(request.AppSecret, NewToken()), cancellationToken);
        }

        db.MessagingChannels.Add(channel);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/messaging/channels/{channel.Id}", await ResponseAsync(channel, secrets, http.Options, tenant, cancellationToken));
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, MessagingChannelRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, ChannelHttp http, MessagingClients clients,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (request.Kind is { } kind && kind != channel.Kind)
        {
            return UserEndpoints.Validation("kind", "A channel's kind cannot change; add a new channel instead.");
        }

        var before = Destination(channel);
        if (await ApplyAsync(channel, request, db, http, creating: false, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (!string.Equals(before, Destination(channel), StringComparison.Ordinal) && string.IsNullOrEmpty(request.Credential) && channel.CredentialCiphertext is not null)
        {
            return UserEndpoints.Validation("credential", "A new account, number, directory or gateway address needs the credential again.");
        }

        if (request.Credential is not null)
        {
            if (request.Credential.Length == 0)
            {
                channel.CredentialCiphertext = null;
                channel.CredentialSetAt = null;
                audit.Add(AuditCategory.Admin, "channels.messaging.credential-removed", "messaging_channel", channel.Id.ToString());
            }
            else
            {
                await SetCredentialAsync(channel, request.Credential, secrets, audit, cancellationToken);
            }

            clients.Forget(channel.Id);
        }

        if (request.AppSecret is { Length: > 0 } || request.NewWebhookToken == true)
        {
            var current = (await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken)).WebhookSecret;
            await MessagingSecretStore.WriteWebhookAsync(secrets, channel, new WebhookSecrets(
                request.AppSecret is { Length: > 0 } secret ? secret : current?.AppSecret,
                request.NewWebhookToken == true || current is null ? NewToken() : current.VerifyToken), cancellationToken);
            audit.Add(AuditCategory.Admin, "channels.messaging.webhook-secret-changed", "messaging_channel", channel.Id.ToString());
        }

        channel.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(channel, secrets, http.Options, tenant, cancellationToken));
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        // Tickets keep their history; their conversation link goes.
        var contacts = db.MessagingContacts.Where(c => c.ChannelId == id).Select(c => c.Id);
        await db.Tickets.Where(t => t.MessagingContactId != null && contacts.Contains(t.MessagingContactId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.MessagingContactId, (Guid?)null), cancellationToken);
        db.MessagingChannels.Remove(channel);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> TestAsync(
        Guid id, MessagingTestRequest? request, ZelvoDbContext db, ISecretProtector secrets, IMessagingClients clients, IAuditLog audit, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (request?.To is { Length: > 0 } to && (channel.Kind == MessagingKind.Teams || !Phone().IsMatch(to)))
        {
            return UserEndpoints.Validation("to", "Enter a phone number in international form, for example +971501234567.");
        }

        string? detail = null;
        string? error = null;
        try
        {
            detail = await clients.TestAsync(channel, await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken), cancellationToken);
        }
        catch (MessagingException ex)
        {
            error = ex.Summary;
        }
        catch (SecretDecryptionException)
        {
            error = "key-unreadable: The stored credential cannot be decrypted. Enter it again.";
        }

        channel.LastError = error is null ? channel.LastError : Cut(error, MessagingChannel.ErrorMaxLength);
        channel.LastErrorAt = error is null ? channel.LastErrorAt : DateTimeOffset.UtcNow;
        long? queued = null;
        if (error is null && request?.To is { Length: > 0 } number)
        {
            var language = request.Language == "ar" ? "ar" : "en";
            var text = NotificationTexts.For(language).Terms.GetValueOrDefault("messaging.test") ?? "Zelvo";
            var message = new ChannelMessage
            {
                TenantId = channel.TenantId,
                ChannelId = channel.Id,
                Direction = MessageDirection.Out,
                Kind = channel.Kind == MessagingKind.WhatsApp ? ChannelMessageKind.Template : ChannelMessageKind.Text,
                Address = number,
                Text = text,
                // WhatsApp's own "hello_world" template: a business may only start a conversation with a template.
                Payload = channel.Kind == MessagingKind.WhatsApp ? new JsonObject { ["name"] = "hello_world", ["language"] = "en_US" }.ToJsonString() : null,
                Language = language,
                Status = ChannelMessageStatus.Pending,
                NextAttemptAt = DateTimeOffset.UtcNow,
            };
            db.ChannelMessages.Add(message);
            await db.SaveChangesAsync(cancellationToken);
            queued = message.Id;
        }

        audit.Add(AuditCategory.Admin, "channels.messaging.tested", "messaging_channel", channel.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = error is null, ["testMessage"] = queued is not null });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new MessagingTestResponse(error is null, detail, error, queued));
    }

    internal static async Task<IResult> LogAsync(Guid id, [AsParameters] ChannelLogQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.MessagingChannels.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var messages = db.ChannelMessages.AsNoTracking().Where(m => m.ChannelId == id);
        if (query.Direction is { } direction)
        {
            messages = messages.Where(m => m.Direction == direction);
        }

        if (query.Status is { } status)
        {
            messages = messages.Where(m => m.Status == status);
        }

        var total = await messages.LongCountAsync(cancellationToken);
        var page = await messages.OrderByDescending(m => m.Id).Skip(query.Skip).Take(query.Size).ToListAsync(cancellationToken);
        var contactIds = page.Where(m => m.ContactId is not null).Select(m => m.ContactId!.Value).Distinct().ToList();
        var addresses = await db.MessagingContacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Address, cancellationToken);
        var ticketIds = page.Where(m => m.TicketId is not null).Select(m => m.TicketId!.Value).Distinct().ToList();
        var numbers = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        return TypedResults.Ok(new PageResult<ChannelMessageResponse>(
            [.. page.Select(m => ChannelMessageResponse.From(m, m.Address ?? (m.ContactId is { } c ? addresses.GetValueOrDefault(c) : null), m.TicketId is { } t ? numbers.GetValueOrDefault(t) : null))],
            query.Number, query.Size, total));
    }

    internal static async Task<IResult> ResendAsync(Guid id, long messageId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var message = await db.ChannelMessages.SingleOrDefaultAsync(m => m.Id == messageId && m.ChannelId == id, cancellationToken);
        if (message is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "message-not-found", "The message does not exist.");
        }

        if (message is not { Direction: MessageDirection.Out, Status: ChannelMessageStatus.Failed })
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "not-failed", "Only messages that failed can be sent again.");
        }

        message.Status = ChannelMessageStatus.Pending;
        message.Attempts = 0;
        message.NextAttemptAt = DateTimeOffset.UtcNow;
        message.Reason = null;
        audit.Add(AuditCategory.Admin, "channels.messaging.resent", "messaging_channel", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["messageId"] = messageId });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ChannelMessageResponse.From(message, message.Address, null));
    }

    internal static async Task<IResult> TeamsAppAsync(
        Guid id, ZelvoDbContext db, IAuditLog audit, ChannelOptions options, NotificationComposer composer, CancellationToken cancellationToken)
    {
        var channel = await db.MessagingChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.Kind == MessagingKind.Teams, cancellationToken);
        if (channel?.AccountId is null)
        {
            return NotFound();
        }

        var letterhead = await composer.LetterheadAsync(cancellationToken);
        var package = TeamsAppPackage.Build(channel, letterhead.Name.En ?? letterhead.Fallback, letterhead.Name.Ar ?? letterhead.Name.En ?? letterhead.Fallback, options.WebUrl);
        audit.Add(AuditCategory.Admin, "channels.messaging.teams-app-downloaded", "messaging_channel", channel.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.File(package, "application/zip", "zelvo-teams-app.zip");
    }

    internal static async Task<IResult> MyChannelsAsync(CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Account!.Id, cancellationToken);
        return TypedResults.Ok(await MyChannelsResponseAsync(me, db, cancellationToken));
    }

    internal static async Task<IResult> UpdateMyChannelsAsync(UpdateMyNotificationChannelsRequest request, CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = await db.Users.SingleAsync(u => u.Id == user.Account!.Id, cancellationToken);
        if ((request.WhatsApp == true || request.Sms == true) && string.IsNullOrEmpty(me.Phone))
        {
            return UserEndpoints.Validation("phone", "Add your mobile number to your profile first.");
        }

        me.NotifyWhatsApp = request.WhatsApp ?? me.NotifyWhatsApp;
        me.NotifySms = request.Sms ?? me.NotifySms;
        me.NotifyTeams = request.Teams ?? me.NotifyTeams;
        me.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await MyChannelsResponseAsync(me, db, cancellationToken));
    }

    private static async Task<MyNotificationChannelsResponse> MyChannelsResponseAsync(Data.Identity.UserAccount me, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var kinds = await db.MessagingChannels.AsNoTracking().Where(c => c.Active).Select(c => c.Kind).Distinct().ToListAsync(cancellationToken);
        var teamsLinked = await db.MessagingContacts.AsNoTracking().AnyAsync(c => c.UserId == me.Id && c.TeamsConversationId != null, cancellationToken);
        return new MyNotificationChannelsResponse(
            me.NotifyWhatsApp, me.NotifySms, me.NotifyTeams, me.Phone is { Length: > 0 } phone ? MaskPhone(phone) : null,
            kinds.Contains(MessagingKind.WhatsApp), kinds.Contains(MessagingKind.Sms), kinds.Contains(MessagingKind.Teams), teamsLinked);
    }

    // Checks and applies the request; returns a problem or null.
    private static async Task<IResult?> ApplyAsync(MessagingChannel channel, MessagingChannelRequest request, ZelvoDbContext db, ChannelHttp http, bool creating, CancellationToken cancellationToken)
    {
        var provider = request.Provider ?? channel.Provider;
        var valid = (channel.Kind, provider) switch
        {
            (MessagingKind.WhatsApp, MessagingProvider.WhatsAppCloud) or (MessagingKind.Sms, MessagingProvider.Twilio or MessagingProvider.GenericHttp)
                or (MessagingKind.Teams, MessagingProvider.TeamsBot) => true,
            _ => false,
        };
        if (!valid)
        {
            return UserEndpoints.Validation("provider", "WhatsApp uses Meta's Cloud API, SMS Twilio or a generic gateway, and Teams the Teams app.");
        }

        channel.Provider = provider;
        if (request.Name is not null || creating)
        {
            var name = request.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > MessagingChannel.NameMaxLength)
            {
                return UserEndpoints.Validation("name", $"Give the channel a name of up to {MessagingChannel.NameMaxLength} characters.");
            }

            channel.Name = name;
        }

        channel.AccountId = Trimmed(request.AccountId) ?? channel.AccountId;
        channel.Sender = Trimmed(request.Sender) ?? channel.Sender;
        channel.DirectoryId = request.DirectoryId is null ? channel.DirectoryId : Trimmed(request.DirectoryId);
        switch (provider)
        {
            case MessagingProvider.WhatsAppCloud when channel.AccountId is not { } number || !Digits().IsMatch(number):
                return UserEndpoints.Validation("accountId", "Enter the phone number id from Meta's WhatsApp Manager (digits only).");
            case MessagingProvider.WhatsAppCloud when channel.Sender is { } shown && !Phone().IsMatch(shown):
                return UserEndpoints.Validation("sender", "Enter the WhatsApp number in international form, for example +97142345678.");
            case MessagingProvider.Twilio when channel.AccountId is not { } sid || !TwilioSid().IsMatch(sid):
                return UserEndpoints.Validation("accountId", "Enter the Twilio account SID (AC followed by 32 characters).");
            case MessagingProvider.Twilio when channel.Sender is not { } from || !(Phone().IsMatch(from) || TwilioService().IsMatch(from) || SenderName().IsMatch(from)):
                return UserEndpoints.Validation("sender", "Enter the sender: a number such as +97142345678, a messaging service SID (MG…) or a registered sender name of up to 11 letters.");
            case MessagingProvider.GenericHttp when channel.Sender is { } name && !(Phone().IsMatch(name) || SenderName().IsMatch(name)):
                return UserEndpoints.Validation("sender", "Enter the sender: a number such as +97142345678 or a registered sender name of up to 11 letters.");
            case MessagingProvider.TeamsBot when channel.AccountId is not { } app || !Guid.TryParse(app, out _):
                return UserEndpoints.Validation("accountId", "Enter the Teams app's Microsoft app (client) id.");
            case MessagingProvider.TeamsBot when channel.DirectoryId is not { } directory || !(Guid.TryParse(directory, out _) || Domain().IsMatch(directory)):
                return UserEndpoints.Validation("directoryId", "Enter the Entra directory (tenant) id or its domain.");
        }

        var settings = MessagingSettings.Read(channel.Settings);
        if (request.Gateway is { } gateway)
        {
            if (provider != MessagingProvider.GenericHttp)
            {
                return UserEndpoints.Validation("gateway", "Only generic gateways are set up on screen.");
            }

            var problems = gateway.Problems().ToList();
            if (problems.Count > 0)
            {
                return TypedResults.ValidationProblem(problems.GroupBy(p => p.Field).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray(), StringComparer.Ordinal));
            }

            var host = new Uri(gateway.Url).Host;
            if (new Uri(gateway.Url).Scheme == Uri.UriSchemeHttp && !http.Options.Network.AllowInsecureHttp
                || IPAddress.TryParse(host, out var literal) && !http.Policy.IsAllowed(literal)
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                return UserEndpoints.Validation("gateway.url", "Zelvo may not connect to this address. Gateways are reached over HTTPS; the installation's operator can allow internal networks (Channels:Network:AllowedPrivateNetworks).");
            }

            settings = settings with { Gateway = gateway };
        }
        else if (creating && provider == MessagingProvider.GenericHttp)
        {
            return UserEndpoints.Validation("gateway", "Set up the gateway's request.");
        }

        if (request.WhatsApp is { } whatsApp)
        {
            if (provider != MessagingProvider.WhatsAppCloud)
            {
                return UserEndpoints.Validation("whatsApp", "Templates belong to WhatsApp channels.");
            }

            var problems = whatsApp.Problems().ToList();
            if (whatsApp.Templates.Keys.FirstOrDefault(k => NotificationEvents.Find(k) is null) is { } unknown)
            {
                problems.Add(("whatsApp.templates", $"'{unknown}' is not a notification event."));
            }

            if (problems.Count > 0)
            {
                return TypedResults.ValidationProblem(problems.GroupBy(p => p.Field).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray(), StringComparer.Ordinal));
            }

            settings = settings with { WhatsApp = whatsApp };
        }

        channel.Settings = settings.Write();
        channel.NewSenders = request.NewSenders ?? channel.NewSenders;
        channel.Active = request.Active ?? channel.Active;
        if (request.CategoryId is not null || request.GroupId is not null || creating)
        {
            channel.CategoryId = request.CategoryId == Guid.Empty ? null : request.CategoryId ?? channel.CategoryId;
            channel.GroupId = request.GroupId == Guid.Empty ? null : request.GroupId ?? channel.GroupId;
            if (channel.CategoryId is { } category && !await db.Categories.AnyAsync(c => c.Id == category, cancellationToken))
            {
                return UserEndpoints.Validation("categoryId", "Choose one of the tenant's categories.");
            }

            if (channel.GroupId is { } group && !await db.SupportGroups.AnyAsync(g => g.Id == group, cancellationToken))
            {
                return UserEndpoints.Validation("groupId", "Choose one of the tenant's groups.");
            }
        }

        return null;
    }

    private static async Task SetCredentialAsync(MessagingChannel channel, string credential, ISecretProtector secrets, IAuditLog audit, CancellationToken cancellationToken)
    {
        channel.CredentialCiphertext = await secrets.ProtectAsync(MessagingSecretStore.CredentialPurpose(channel.Id), credential, cancellationToken);
        channel.CredentialSetAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "channels.messaging.credential-changed", "messaging_channel", channel.Id.ToString());
    }

    private static async Task<MessagingChannelResponse> ResponseAsync(MessagingChannel channel, ISecretProtector secrets, ChannelOptions options, ITenantContext tenant, CancellationToken cancellationToken)
    {
        string? token = null;
        var appSecretSet = false;
        if (channel.WebhookSecretCiphertext is not null)
        {
            try
            {
                var webhook = (await MessagingSecretStore.ReadAsync(secrets, channel, cancellationToken)).WebhookSecret;
                token = webhook?.VerifyToken;
                appSecretSet = !string.IsNullOrEmpty(webhook?.AppSecret);
            }
            catch (SecretDecryptionException)
            {
                token = null;
            }
        }

        var settings = MessagingSettings.Read(channel.Settings);
        var url = WebhookUrl(options, tenant.Slug!, channel);
        // The gateway's token is part of its address; Meta's verify token is entered next to the address.
        var webhookUrl = channel.Provider == MessagingProvider.GenericHttp && token is not null ? new Uri(url, $"?token={Uri.EscapeDataString(token)}") : url;
        return new MessagingChannelResponse(
            channel.Id, channel.Name, channel.Kind, channel.Provider, channel.AccountId, channel.Sender, channel.DirectoryId,
            channel.CredentialCiphertext is not null, channel.CredentialSetAt, appSecretSet, webhookUrl.AbsoluteUri,
            channel.Provider == MessagingProvider.WhatsAppCloud ? token : null, settings.Gateway, settings.WhatsApp,
            channel.NewSenders, channel.CategoryId, channel.GroupId, channel.Active, channel.LastInboundAt, channel.LastError, channel.LastErrorAt,
            channel.CreatedAt, channel.UpdatedAt);
    }

    private static GenericSmsSettings? GatewayOf(MessagingChannel channel) => MessagingSettings.Read(channel.Settings).Gateway;

    private static string Destination(MessagingChannel channel) =>
        $"{channel.Provider}|{channel.AccountId}|{channel.DirectoryId}|{(channel.Provider == MessagingProvider.GenericHttp ? GatewayOf(channel)?.Url : null)}";

    private static string CredentialLabel(MessagingProvider provider) => provider switch
    {
        MessagingProvider.WhatsAppCloud => "The access token",
        MessagingProvider.Twilio => "The auth token",
        MessagingProvider.GenericHttp => "The gateway's secret",
        _ => "The app's client secret",
    };

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>A phone number with the middle hidden: <c>+9715•••••678</c>.</summary>
    internal static string MaskPhone(string phone) =>
        phone.Length <= 7 ? new string('•', phone.Length) : $"{phone[..5]}{new string('•', phone.Length - 8)}{phone[^3..]}";

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "channel-not-found", "The channel does not exist.");

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    internal static partial Regex Phone();

    [GeneratedRegex(@"^\d{5,30}$")]
    private static partial Regex Digits();

    [GeneratedRegex("^AC[0-9a-fA-F]{32}$")]
    private static partial Regex TwilioSid();

    [GeneratedRegex("^MG[0-9a-fA-F]{32}$")]
    private static partial Regex TwilioService();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 .-]{0,10}$")]
    private static partial Regex SenderName();

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex Domain();
}

/// <summary>
/// The Teams app package an administrator uploads to Teams (manifest 1.17, English and Arabic,
/// icons). Task J4 (version 1.1.0): personal tabs (My tickets, the agent's list, approvals), the
/// bot's commands and the message action "Raise a ticket".
/// </summary>
internal static class TeamsAppPackage
{
    /// <summary>The app's version; Teams asks administrators to update when it grows.</summary>
    public const string Version = "1.1.0";

    /// <summary>The personal tabs: entity id, view of the Teams tab page, the term of its name.</summary>
    internal static readonly (string Id, string View, string Term)[] Tabs =
    [
        ("my-tickets", "my-tickets", "teams.tab.my-tickets"),
        ("work", "workspace", "teams.tab.work"),
        ("approvals", "approvals", "teams.tab.approvals"),
    ];

    /// <summary>The bot's commands: the words the person sends (see <see cref="TeamsApp.CommandOf"/>) and the term of their description.</summary>
    internal static readonly (string Title, string Term)[] Commands =
    [
        ("teams.command.raise", "teams.command.raise.help"),
        ("teams.command.status", "teams.command.status.help"),
        ("teams.command.help", "teams.command.help.help"),
    ];

    public static byte[] Build(MessagingChannel channel, string nameEn, string nameAr, Uri webUrl)
    {
        var en = NotificationTexts.For("en").Terms;
        var ar = NotificationTexts.For("ar").Terms;
        string Short(string value) => value.Length <= 30 ? value : value[..30];
        var manifest = new JsonObject
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/teams/v1.17/MicrosoftTeams.schema.json",
            ["manifestVersion"] = "1.17",
            ["version"] = Version,
            ["id"] = channel.AccountId,
            ["developer"] = new JsonObject
            {
                ["name"] = Short(nameEn),
                ["websiteUrl"] = webUrl.AbsoluteUri,
                ["privacyUrl"] = webUrl.AbsoluteUri,
                ["termsOfUseUrl"] = webUrl.AbsoluteUri,
            },
            ["name"] = new JsonObject { ["short"] = Short(nameEn), ["full"] = nameEn },
            ["description"] = new JsonObject { ["short"] = en.GetValueOrDefault("teams.app.short"), ["full"] = en.GetValueOrDefault("teams.app.full") },
            ["icons"] = new JsonObject { ["color"] = "color.png", ["outline"] = "outline.png" },
            ["accentColor"] = "#0B5CAD",
            ["bots"] = new JsonArray(new JsonObject
            {
                ["botId"] = channel.AccountId,
                ["scopes"] = new JsonArray("personal"),
                ["supportsFiles"] = false,
                ["isNotificationOnly"] = false,
                ["commandLists"] = new JsonArray(new JsonObject
                {
                    ["scopes"] = new JsonArray("personal"),
                    ["commands"] = new JsonArray([.. Commands.Select(c => (JsonNode)new JsonObject { ["title"] = en.GetValueOrDefault(c.Title), ["description"] = en.GetValueOrDefault(c.Term) })]),
                }),
            }),
            // Task J4: the Zelvo web app inside Teams; the tab page picks the language from Teams' {locale}.
            ["staticTabs"] = new JsonArray([.. Tabs.Select(t => (JsonNode)new JsonObject
            {
                ["entityId"] = t.Id,
                ["name"] = en.GetValueOrDefault(t.Term),
                ["contentUrl"] = $"{webUrl.GetLeftPart(UriPartial.Authority)}/auth/teams/tab?view={t.View}&locale={{locale}}",
                ["websiteUrl"] = new Uri(webUrl, $"en/teams/tab?view={t.View}").AbsoluteUri,
                ["scopes"] = new JsonArray("personal"),
            })]),
            // Task J4: "Raise a ticket" on any message, in a dialog.
            ["composeExtensions"] = new JsonArray(new JsonObject
            {
                ["botId"] = channel.AccountId,
                ["commands"] = new JsonArray(new JsonObject
                {
                    ["id"] = TeamsApp.MessageActionCommand,
                    ["type"] = "action",
                    ["context"] = new JsonArray("message"),
                    ["fetchTask"] = true,
                    ["title"] = en.GetValueOrDefault("teams.command.raise"),
                    ["description"] = en.GetValueOrDefault("teams.message-action.help"),
                }),
            }),
            ["permissions"] = new JsonArray("identity", "messageTeamMembers"),
            ["validDomains"] = new JsonArray(webUrl.Host),
            ["localizationInfo"] = new JsonObject
            {
                ["defaultLanguageTag"] = "en",
                ["additionalLanguages"] = new JsonArray(new JsonObject { ["languageTag"] = "ar", ["file"] = "ar.json" }),
            },
        };
        var arabic = new JsonObject
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/teams/v1.17/MicrosoftTeams.Localization.schema.json",
            ["name.short"] = Short(nameAr),
            ["name.full"] = nameAr,
            ["description.short"] = ar.GetValueOrDefault("teams.app.short"),
            ["description.full"] = ar.GetValueOrDefault("teams.app.full"),
            ["composeExtensions[0].commands[0].title"] = ar.GetValueOrDefault("teams.command.raise"),
            ["composeExtensions[0].commands[0].description"] = ar.GetValueOrDefault("teams.message-action.help"),
        };
        for (var i = 0; i < Tabs.Length; i++)
        {
            arabic[$"staticTabs[{i}].name"] = ar.GetValueOrDefault(Tabs[i].Term);
        }

        for (var i = 0; i < Commands.Length; i++)
        {
            arabic[$"bots[0].commandLists[0].commands[{i}].title"] = ar.GetValueOrDefault(Commands[i].Title);
            arabic[$"bots[0].commandLists[0].commands[{i}].description"] = ar.GetValueOrDefault(Commands[i].Term);
        }
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            Add(zip, "ar.json", Encoding.UTF8.GetBytes(arabic.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            Add(zip, "color.png", Png.Solid(192, 0x0B, 0x5C, 0xAD, 0xFF));
            Add(zip, "outline.png", Png.Solid(32, 0xFF, 0xFF, 0xFF, 0xFF));
        }

        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        entry.Write(content);
    }
}

/// <summary>Writes small solid-colour PNG images (Teams app icons).</summary>
internal static class Png
{
    public static byte[] Solid(int size, byte r, byte g, byte b, byte a)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < size; x++)
            {
                raw.Write([r, g, b, a]);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
    }
}

/// <summary>Adds or changes a messaging channel. Values left out stay when changing.</summary>
public sealed class MessagingChannelRequest
{
    /// <summary>Name shown to administrators.</summary>
    public string? Name { get; init; }

    /// <summary>WhatsApp, Sms or Teams (fixed once added).</summary>
    public MessagingKind? Kind { get; init; }

    /// <summary>WhatsAppCloud, Twilio, GenericHttp or TeamsBot.</summary>
    public MessagingProvider? Provider { get; init; }

    /// <summary>WhatsApp phone number id, Twilio account SID or the Teams app id.</summary>
    public string? AccountId { get; init; }

    /// <summary>The WhatsApp number shown to people, or the SMS sender (number, messaging service SID or sender name).</summary>
    public string? Sender { get; init; }

    /// <summary>WhatsApp business account id, or the Teams app's Entra directory.</summary>
    public string? DirectoryId { get; init; }

    /// <summary>Access token, auth token, gateway secret or client secret; empty removes it. Never returned.</summary>
    public string? Credential { get; init; }

    /// <summary>WhatsApp: the Meta app's secret, which signs incoming messages. Never returned.</summary>
    public string? AppSecret { get; init; }

    /// <summary>Makes a new verify token (WhatsApp) or gateway token.</summary>
    public bool? NewWebhookToken { get; init; }

    /// <summary>The generic gateway's request and answer mapping.</summary>
    public GenericSmsSettings? Gateway { get; init; }

    /// <summary>WhatsApp's templates per notification event.</summary>
    public WhatsAppSettings? WhatsApp { get; init; }

    /// <summary>Who may raise tickets without an account.</summary>
    public MessagingSenders? NewSenders { get; init; }

    /// <summary>Category of new tickets (empty id: none).</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Group of new tickets (empty id: the assignment rules).</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Whether it takes and sends messages.</summary>
    public bool? Active { get; init; }
}

/// <summary>A test of a channel.</summary>
public sealed class MessagingTestRequest
{
    /// <summary>Also send a test message to this number (WhatsApp: Meta's <c>hello_world</c> template).</summary>
    [StringLength(16)]
    public string? To { get; init; }

    /// <summary>Language of the SMS test text.</summary>
    public string? Language { get; init; }
}

/// <summary>Filters of the channel log.</summary>
internal sealed class ChannelLogQuery
{
    /// <summary>In or Out.</summary>
    public MessageDirection? Direction { get; init; }

    /// <summary>Status.</summary>
    public ChannelMessageStatus? Status { get; init; }

    /// <summary>Page, from 1.</summary>
    [Range(1, 100_000)]
    public int? Page { get; init; }

    /// <summary>Page size.</summary>
    [Range(1, PageQuery.MaxPageSize)]
    public int? PageSize { get; init; }

    internal int Number => Page ?? 1;

    internal int Size => PageSize ?? PageQuery.DefaultPageSize;

    internal int Skip => (Number - 1) * Size;
}

/// <summary>A messaging channel as administrators see it (never its credentials).</summary>
internal sealed record MessagingChannelResponse(
    Guid Id, string Name, MessagingKind Kind, MessagingProvider Provider, string? AccountId, string? Sender, string? DirectoryId,
    bool CredentialSet, DateTimeOffset? CredentialSetAt, bool AppSecretSet, string WebhookUrl, string? VerifyToken,
    GenericSmsSettings? Gateway, WhatsAppSettings? WhatsApp, MessagingSenders NewSenders, Guid? CategoryId, Guid? GroupId, bool Active,
    DateTimeOffset? LastInboundAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What a test found.</summary>
/// <param name="Ok">The provider accepted the account and credential.</param>
/// <param name="Detail">What the provider said about the account.</param>
/// <param name="Error">Why not.</param>
/// <param name="TestMessageId">The queued test message, if one was asked for.</param>
internal sealed record MessagingTestResponse(bool Ok, string? Detail, string? Error, long? TestMessageId);

/// <summary>One message of the channel log (number masked; text only for messages without a ticket's conversation).</summary>
internal sealed record ChannelMessageResponse(
    long Id, MessageDirection Direction, ChannelMessageKind Kind, ChannelMessageStatus Status, string? Reason, string? Address,
    int TextLength, Guid? TicketId, string? TicketNumber, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? SentAt)
{
    public static ChannelMessageResponse From(ChannelMessage m, string? address, string? number) =>
        new(m.Id, m.Direction, m.Kind, m.Status, m.Reason,
            address is null ? null : address.StartsWith('+') ? MessagingEndpoints.MaskPhone(address) : "•••",
            m.Text?.Length ?? 0, m.TicketId, number, m.Attempts, m.CreatedAt, m.SentAt);
}

/// <summary>Your WhatsApp, SMS and Teams choices.</summary>
/// <param name="WhatsApp">Notifications also by WhatsApp.</param>
/// <param name="Sms">Notifications also by SMS.</param>
/// <param name="Teams">Notifications also in Teams.</param>
/// <param name="Phone">Your profile's number, masked.</param>
/// <param name="WhatsAppOffered">Your organisation has WhatsApp.</param>
/// <param name="SmsOffered">Your organisation has SMS.</param>
/// <param name="TeamsOffered">Your organisation has a Teams app.</param>
/// <param name="TeamsLinked">You have used the Teams app, so it can reach you.</param>
internal sealed record MyNotificationChannelsResponse(
    bool WhatsApp, bool Sms, bool Teams, string? Phone, bool WhatsAppOffered, bool SmsOffered, bool TeamsOffered, bool TeamsLinked);

/// <summary>Turns your WhatsApp, SMS and Teams notifications on or off; values left out stay.</summary>
public sealed class UpdateMyNotificationChannelsRequest
{
    /// <summary>WhatsApp.</summary>
    public bool? WhatsApp { get; init; }

    /// <summary>SMS.</summary>
    public bool? Sms { get; init; }

    /// <summary>Teams.</summary>
    public bool? Teams { get; init; }
}
