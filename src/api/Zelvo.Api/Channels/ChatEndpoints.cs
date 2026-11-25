using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Ai.Features;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Security.RateLimiting;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Channels;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Channels;

/// <summary>
/// Live chat (module 6, task E2; product owner decisions 2026-11-24): widgets for the portal and
/// other websites; visitors give their name and email (signed-in people need not); chats wait in
/// the widget's group, whose available members with <c>chat.handle</c> accept them up to the group's
/// limit, transfer and end them; the agent decides whether a chat becomes a ticket. With nobody
/// online, visitors leave a message, which becomes a ticket. Every change is audited.
/// </summary>
internal static partial class ChatEndpoints
{
    /// <summary>Header carrying a visitor's chat token.</summary>
    public const string TokenHeader = "X-Chat-Token";

    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        var widgets = group.MapGroup("/chat/widgets").WithTags("Channels").RequireTenant();
        widgets.MapGet("/", ListWidgetsAsync)
            .WithName("ListChatWidgets")
            .WithSummary("The tenant's chat widgets. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<IReadOnlyList<ChatWidgetResponse>>();
        widgets.MapPost("/", CreateWidgetAsync)
            .WithName("CreateChatWidget")
            .WithSummary("Adds a chat widget: bilingual name and greeting, the group that answers, category of tickets, whether it shows on the portal, and the websites (origins such as https://www.entity.gov.ae) that may embed it. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<ChatWidgetResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        widgets.MapPatch("/{id:guid}", UpdateWidgetAsync)
            .WithName("UpdateChatWidget")
            .WithSummary("Changes a chat widget; values left out stay. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<ChatWidgetResponse>()
            .ProducesValidationProblem();
        widgets.MapDelete("/{id:guid}", DeleteWidgetAsync)
            .WithName("DeleteChatWidget")
            .WithSummary("Removes a widget that has no chats; otherwise make it inactive. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces(StatusCodes.Status204NoContent);
        widgets.MapGet("/{id:guid}/public", PublicWidgetAsync)
            .WithName("GetPublicChatWidget")
            .WithSummary("What a visitor's chat window shows: name, greeting and whether anyone is online. Anyone; with origin, whether that website may embed the widget.")
            .AllowAnonymousAccess()
            .Produces<PublicChatWidgetResponse>();
        widgets.MapPost("/{id:guid}/sessions", StartAsync)
            .WithName("StartChat")
            .WithSummary("Starts a chat. Visitors give their name and email and get a chat token (send it as X-Chat-Token); signed-in people need neither. When the widget's virtual agent is on (task E5), it answers first, also when nobody is online; otherwise refused with chat-offline when nobody is online. A few per address and 10 minutes; audited.")
            .AllowAnonymousAccess()
            .Produces<StartChatResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        widgets.MapPost("/{id:guid}/offline-messages", LeaveMessageAsync)
            .WithName("LeaveChatMessage")
            .WithSummary("With nobody online: leaves a message, which becomes an incident (channel Chat) for the email given. A few per address and hour; audited.")
            .AllowAnonymousAccess()
            .Produces<LeftMessageResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        group.MapGet("/chat/portal-widget", PortalWidgetAsync)
            .WithTags("Channels").RequireTenant()
            .WithName("GetPortalChatWidget")
            .WithSummary("The widget shown on the portal, if one is active. Anyone.")
            .AllowAnonymousAccess()
            .Produces<PublicChatWidgetResponse>();

        var sessions = group.MapGroup("/chat/sessions").WithTags("Channels").RequireTenant();
        sessions.MapGet("/{id:guid}", VisitorGetAsync)
            .WithName("GetChat")
            .WithSummary("The visitor's chat with its messages after the given sequence number. The chat's token (X-Chat-Token) or the signed-in visitor.")
            .AllowAnonymousAccess()
            .Produces<VisitorChatResponse>();
        sessions.MapPost("/{id:guid}/messages", VisitorWriteAsync)
            .WithName("WriteChatMessage")
            .WithSummary("The visitor writes in the chat. While the virtual agent has the chat, its answer from the knowledge base is added before this returns, and it hands the chat to the group's queue when the visitor asks for a person or it cannot help. The chat's token or the signed-in visitor; at most 20 a minute; audited.")
            .AllowAnonymousAccess()
            .Produces<ChatMessageResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        sessions.MapPost("/{id:guid}/handover", HandoverAsync)
            .WithName("HandOverChat")
            .WithSummary("The visitor asks for a person while chatting with the virtual agent (task E5): the chat joins the group's queue with its whole conversation. With nobody online it is refused with chat-offline, unless leaveMessage is true: the conversation then becomes an incident and the chat ends. The chat's token or the signed-in visitor; audited.")
            .AllowAnonymousAccess()
            .Produces<VisitorChatResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);
        sessions.MapPost("/{id:guid}/end", VisitorEndAsync)
            .WithName("EndChat")
            .WithSummary("The visitor ends the chat. The chat's token or the signed-in visitor; audited.")
            .AllowAnonymousAccess()
            .Produces<VisitorChatResponse>();

        var agent = group.MapGroup("/chat").WithTags("Channels").RequireTenant();
        agent.MapGet("/queue", QueueAsync)
            .WithName("GetChatQueue")
            .WithSummary("Chats waiting in the caller's groups, the caller's own chats, and their limit. Needs chat.handle.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<ChatQueueResponse>();
        agent.MapGet("/sessions/{id:guid}/agent", AgentGetAsync)
            .WithName("GetChatForAgent")
            .WithSummary("A chat of the caller's groups with the visitor's details and messages after the given sequence number. Needs chat.handle; the visitor's email is audited as a view of personal data.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<AgentChatResponse>();
        agent.MapPost("/sessions/{id:guid}/accept", AcceptAsync)
            .WithName("AcceptChat")
            .WithSummary("Accepts a waiting chat of one of the caller's groups, while the caller is available and under the group's limit. Only one agent wins. Needs chat.handle; audited.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<AgentChatResponse>();
        agent.MapPost("/sessions/{id:guid}/agent-messages", AgentWriteAsync)
            .WithName("AnswerChat")
            .WithSummary("The agent of an active chat writes to the visitor. Needs chat.handle; audited.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<ChatMessageResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        agent.MapPost("/sessions/{id:guid}/transfer", TransferAsync)
            .WithName("TransferChat")
            .WithSummary("Hands the chat to a colleague who handles chats in the chat's group (it stays active), or to another group's queue (it waits again). Needs chat.handle; audited.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<AgentChatResponse>()
            .ProducesValidationProblem();
        agent.MapPost("/sessions/{id:guid}/close", AgentEndAsync)
            .WithName("CloseChat")
            .WithSummary("The agent ends the chat. Needs chat.handle; audited.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<AgentChatResponse>();
        agent.MapPost("/sessions/{id:guid}/ticket", CreateTicketAsync)
            .WithName("CreateTicketFromChat")
            .RequirePerson()
            .WithSummary("Makes an incident (channel Chat) from the chat for the visitor (an account is made for their email when there is none), assigned to the agent in the chat's group, with the transcript as its conversation. Needs chat.handle and incidents.create; audited.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<AgentChatResponse>()
            .ProducesValidationProblem();
        agent.MapPost("/sessions/{id:guid}/messages/{messageId:guid}/translation", TranslateMessageAsync)
            .WithName("TranslateChatMessage")
            .WithSummary("One-click translation of a chat message into en or ar (LANG-7; Modern Standard Arabic and Gulf dialect are understood). Cached per language. Needs chat.handle and the AI feature translation.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<Ai.AiTranslationResponse>()
            .ProducesValidationProblem();
        agent.MapPost("/sessions/{id:guid}/link", LinkTicketAsync)
            .WithName("LinkChatToTicket")
            .WithSummary("Adds the chat's transcript to an open incident of the same requester. Needs chat.handle and incidents.edit; audited.")
            .RequirePermission(Permissions.ChatHandle)
            .Produces<AgentChatResponse>()
            .ProducesValidationProblem();
        return group;
    }

    // --- Widgets (administrators) ---

    internal static async Task<IResult> ListWidgetsAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.ChatWidgets.AsNoTracking().OrderBy(w => w.CreatedAt).ToListAsync(cancellationToken)).Select(ChatWidgetResponse.From).ToList());

    internal static async Task<IResult> CreateWidgetAsync(ChatWidgetRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.GroupId is null)
        {
            return UserEndpoints.Validation("groupId", "Choose the group that answers the chats.");
        }

        var widget = new ChatWidget { Name = new LocalizedText(null, null) };
        if (await ApplyAsync(widget, request, db, cancellationToken) is { } problem)
        {
            return problem;
        }

        db.ChatWidgets.Add(widget);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/chat/widgets/{widget.Id}", ChatWidgetResponse.From(widget));
    }

    internal static async Task<IResult> UpdateWidgetAsync(Guid id, ChatWidgetRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var widget = await db.ChatWidgets.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (widget is null)
        {
            return WidgetNotFound();
        }

        if (await ApplyAsync(widget, request, db, cancellationToken) is { } problem)
        {
            return problem;
        }

        widget.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ChatWidgetResponse.From(widget));
    }

    internal static async Task<IResult> DeleteWidgetAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var widget = await db.ChatWidgets.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (widget is null)
        {
            return WidgetNotFound();
        }

        if (await db.ChatSessions.AnyAsync(c => c.WidgetId == id, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "widget-in-use", "The widget has chats; make it inactive instead.");
        }

        db.ChatWidgets.Remove(widget);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult?> ApplyAsync(ChatWidget widget, ChatWidgetRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.Name is { } name)
        {
            var text = new LocalizedText(name.En, name.Ar);
            if (!text.HasAnyValue)
            {
                return UserEndpoints.Validation("name", "Name the widget in English or Arabic.");
            }

            widget.Name = text;
        }
        else if (!widget.Name.HasAnyValue)
        {
            return UserEndpoints.Validation("name", "Name the widget in English or Arabic.");
        }

        if (request.Greeting is { } greeting)
        {
            widget.Greeting = new LocalizedText(greeting.En, greeting.Ar);
        }

        if (request.GroupId is { } groupId)
        {
            if (groupId != widget.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
            {
                return UserEndpoints.Validation("groupId", "Choose an active group.");
            }

            widget.GroupId = groupId;
        }

        if (request.ClearCategory == true)
        {
            widget.CategoryId = null;
        }

        if (request.CategoryId is { } categoryId)
        {
            if (categoryId != widget.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, categoryId, cancellationToken))
            {
                return UserEndpoints.Validation("categoryId", "Choose an active category.");
            }

            widget.CategoryId = categoryId;
        }

        if (request.AllowedOrigins is { } origins)
        {
            var cleaned = new List<string>();
            foreach (var origin in origins.Where(o => !string.IsNullOrWhiteSpace(o)))
            {
                if (NormalizeOrigin(origin) is not { } normalized)
                {
                    return UserEndpoints.Validation("allowedOrigins", $"'{origin}' is not a website address such as https://www.entity.gov.ae.");
                }

                cleaned.Add(normalized);
            }

            if (cleaned.Count > ChatWidget.MaxOrigins)
            {
                return UserEndpoints.Validation("allowedOrigins", $"List at most {ChatWidget.MaxOrigins} websites.");
            }

            widget.AllowedOrigins = [.. cleaned.Distinct(StringComparer.Ordinal)];
        }

        if (request.OnPortal is { } onPortal)
        {
            widget.OnPortal = onPortal;
        }

        if (request.Active is { } active)
        {
            widget.Active = active;
        }

        if (request.VirtualAgent is { } virtualAgent)
        {
            widget.VirtualAgent = virtualAgent;
        }

        return null;
    }

    /// <summary>An origin (<c>https://host[:port]</c>), lower case, or null when it is not one. Plain http only for localhost.</summary>
    internal static string? NormalizeOrigin(string? value)
    {
        if (value is null || value.Length > ChatWidget.OriginMaxLength || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.AbsolutePath is not ("/" or "") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    // --- Visitors ---

    internal static async Task<IResult> PortalWidgetAsync(HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var widget = await db.ChatWidgets.AsNoTracking().Where(w => w.Active && w.OnPortal).OrderBy(w => w.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return widget is null ? WidgetNotFound() : TypedResults.Ok(await PublicAsync(http, db, widget, null, cancellationToken));
    }

    internal static async Task<IResult> PublicWidgetAsync(Guid id, string? origin, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var widget = await db.ChatWidgets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id && w.Active, cancellationToken);
        return widget is null ? WidgetNotFound() : TypedResults.Ok(await PublicAsync(http, db, widget, origin, cancellationToken));
    }

    private static async Task<PublicChatWidgetResponse> PublicAsync(HttpContext http, ZelvoDbContext db, ChatWidget widget, string? origin, CancellationToken cancellationToken) =>
        new(widget.Id, LocalizedTextDto.From(widget.Name), LocalizedTextDto.From(widget.Greeting),
            await OnlineAsync(db, widget.GroupId, cancellationToken), origin is null ? widget.OnPortal : IsAllowedOrigin(widget, origin), widget.AllowedOrigins,
            await VirtualAgentOnAsync(http.RequestServices, widget, cancellationToken));

    /// <summary>
    /// Whether the widget's virtual agent answers now: on for the widget, and the AI feature
    /// available. The AI gateway is asked only for widgets that use it.
    /// </summary>
    internal static async Task<bool> VirtualAgentOnAsync(IServiceProvider services, ChatWidget widget, CancellationToken cancellationToken) =>
        widget.VirtualAgent && (await services.GetRequiredService<IAiGateway>().AvailabilityAsync(cancellationToken))
            .Any(a => a.Feature == AiFeatures.VirtualAgent && a.Available);

    internal static async Task<IResult> StartAsync(
        Guid id, StartChatRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IRateLimitStore limits,
        CancellationToken cancellationToken)
    {
        var widget = await db.ChatWidgets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id && w.Active, cancellationToken);
        if (widget is null)
        {
            return WidgetNotFound();
        }

        if (OriginProblem(widget, request.Origin) is { } originProblem)
        {
            return originProblem;
        }

        if (await LimitedAsync(limits, http, "chat-start", http.Connection.RemoteIpAddress?.ToString(), 5, TimeSpan.FromMinutes(10), cancellationToken) is { } limited)
        {
            return limited;
        }

        var person = user.Account;
        var name = person?.DisplayName ?? person?.Email ?? request.Name?.Trim();
        var email = person?.Email ?? request.Email?.Trim();
        if (person is null && string.IsNullOrWhiteSpace(name))
        {
            return UserEndpoints.Validation("name", "Enter your name.");
        }

        if (person is null && !MailboxEndpoints.IsAddress(email))
        {
            return UserEndpoints.Validation("email", "Enter your email address.");
        }

        // Task F5: visitors see the privacy notice (when one is published) before they chat.
        var (noticeProblem, notice) = person is null ? await Privacy.PrivacyConsents.VisitorCheckAsync(db, request.PrivacyNoticeAccepted, cancellationToken) : (null, null);
        if (noticeProblem is not null)
        {
            return noticeProblem;
        }

        var withAssistant = await VirtualAgentOnAsync(http.RequestServices, widget, cancellationToken);
        if (!withAssistant && !await OnlineAsync(db, widget.GroupId, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "chat-offline", "Nobody can chat right now. Leave a message instead.");
        }

        var token = person is null ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) : null;
        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession
        {
            WidgetId = widget.Id,
            GroupId = widget.GroupId,
            VisitorUserId = person?.Id,
            VisitorName = IncidentIntake.Clean(name ?? email ?? "-", ChatSession.NameMaxLength),
            VisitorEmail = (email ?? string.Empty).ToLowerInvariant(),
            VisitorTokenHash = token is null ? null : Hash(token),
            Language = request.Language is LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English,
            Origin = NormalizeOrigin(request.Origin),
            State = withAssistant ? ChatState.Assistant : ChatState.Waiting,
            StartedAt = now,
            LastVisitorActivityAt = now,
        };
        db.ChatSessions.Add(session);
        Privacy.PrivacyConsents.RecordVisitor(db, notice, $"chat:{session.Id}", Data.Privacy.ConsentChannel.Chat);
        var asked = false;
        if (request.Message is { } first && IncidentIntake.Clean(first, ChatMessage.BodyMaxLength) is { Length: > 0 } body)
        {
            db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.Visitor, Body = body, CreatedAt = now });
            asked = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (withAssistant && asked)
        {
            await AssistantAnswerAsync(db, user, http.RequestServices.GetRequiredService<VirtualAgent>(), session, cancellationToken);
        }

        return TypedResults.Created($"/api/v1/chat/sessions/{session.Id}", new StartChatResponse(session.Id, token));
    }

    internal static async Task<IResult> LeaveMessageAsync(
        Guid id, LeaveChatMessageRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IRateLimitStore limits, IncidentIntake intake,
        ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        var widget = await db.ChatWidgets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id && w.Active, cancellationToken);
        if (widget is null)
        {
            return WidgetNotFound();
        }

        if (OriginProblem(widget, request.Origin) is { } originProblem)
        {
            return originProblem;
        }

        if (await LimitedAsync(limits, http, "chat-offline", http.Connection.RemoteIpAddress?.ToString(), 5, TimeSpan.FromHours(1), cancellationToken) is { } limited)
        {
            return limited;
        }

        var text = IncidentIntake.Clean(request.Message ?? string.Empty, Ticket.DescriptionMaxLength);
        if (text.Length < 3)
        {
            return UserEndpoints.Validation("message", "Write your message.");
        }

        var person = user.Account;
        if (person is null && (string.IsNullOrWhiteSpace(request.Name) || !MailboxEndpoints.IsAddress(request.Email)))
        {
            return UserEndpoints.Validation(string.IsNullOrWhiteSpace(request.Name) ? "name" : "email", "Enter your name and email address.");
        }

        var (noticeProblem, notice) = person is null ? await Privacy.PrivacyConsents.VisitorCheckAsync(db, request.PrivacyNoticeAccepted, cancellationToken) : (null, null);
        if (noticeProblem is not null)
        {
            return noticeProblem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var requester = person ?? await RequesterAsync(db, audit, request.Email!, request.Name, request.Language, cancellationToken);
        if (requester.Status != UserStatus.Active)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "requester-disabled", "This account is disabled.");
        }

        var firstLine = text.Split('\n')[0].Trim();
        var ticket = await intake.CreateAsync(tenant.TenantId!.Value, new IncidentIntakeRequest(
            firstLine.Length <= 120 ? firstLine : $"{firstLine[..119]}…", text, TicketChannel.Chat, requester.Id, CategoryId: widget.CategoryId, GroupId: widget.GroupId),
            DateTimeOffset.UtcNow, cancellationToken);
        Privacy.PrivacyConsents.RecordVisitor(db, notice, $"ticket:{ticket.Id}", Data.Privacy.ConsentChannel.Chat);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/incidents/{ticket.Id}", new LeftMessageResponse(ticket.Number));
    }

    internal static async Task<IResult> VisitorGetAsync(Guid id, long? after, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var session = await VisitorSessionAsync(db, http, user, id, cancellationToken);
        if (session is null)
        {
            return ChatNotFound();
        }

        // Activity keeps the chat open; written without the change tracker (not audited).
        var now = DateTimeOffset.UtcNow;
        if (session.State != ChatState.Ended && now - session.LastVisitorActivityAt > TimeSpan.FromSeconds(30))
        {
            await db.ChatSessions.Where(c => c.Id == id).ExecuteUpdateAsync(set => set.SetProperty(c => c.LastVisitorActivityAt, now), cancellationToken);
        }

        return TypedResults.Ok(await VisitorViewAsync(db, session, after, cancellationToken));
    }

    internal static async Task<IResult> VisitorWriteAsync(
        Guid id, ChatMessageRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IRateLimitStore limits,
        CancellationToken cancellationToken)
    {
        var session = await VisitorSessionAsync(db, http, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.State == ChatState.Ended)
        {
            return Ended();
        }

        if (await LimitedAsync(limits, http, "chat-message", session.Id.ToString(), 20, TimeSpan.FromMinutes(1), cancellationToken) is { } limited)
        {
            return limited;
        }

        var body = IncidentIntake.Clean(request.Body!, ChatMessage.BodyMaxLength);
        if (body.Length == 0)
        {
            return UserEndpoints.Validation("body", "Write a message.");
        }

        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage { SessionId = session.Id, Sender = ChatSender.Visitor, Body = body, CreatedAt = now };
        db.ChatMessages.Add(message);
        session.LastVisitorActivityAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (session.State == ChatState.Assistant)
        {
            await AssistantAnswerAsync(db, user, http.RequestServices.GetRequiredService<VirtualAgent>(), session, cancellationToken);
        }

        return TypedResults.Created($"/api/v1/chat/sessions/{id}", ChatMessageResponse.From(message, null));
    }

    internal static async Task<IResult> HandoverAsync(
        Guid id, HandoverRequest? request, HttpContext http, ZelvoDbContext db, CurrentUser user, IncidentIntake intake, ITenantContext tenant, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var session = await VisitorSessionAsync(db, http, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.State == ChatState.Ended)
        {
            return Ended();
        }

        if (session.State == ChatState.Assistant)
        {
            if (await OnlineAsync(db, session.GroupId, cancellationToken))
            {
                HandOver(db, session);
            }
            else if (request?.LeaveMessage == true)
            {
                // Routing locks the group until the commit (task B2).
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await LeaveConversationAsync(db, user, intake, tenant, audit, session, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                return ApiProblem.Create(StatusCodes.Status409Conflict, "chat-offline", "Nobody can chat right now. Leave your conversation as a message instead.");
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await VisitorViewAsync(db, session, null, cancellationToken));
    }

    internal static async Task<IResult> TranslateMessageAsync(
        Guid id, Guid messageId, ChatTranslationRequest request, ZelvoDbContext db, CurrentUser user, AgentAssist assist, CancellationToken cancellationToken)
    {
        if (!LocalizationSettings.IsLanguage(request.Language))
        {
            return UserEndpoints.Validation("language", "Translate into en or ar.");
        }

        var session = await AgentSessionAsync(db, user, id, cancellationToken);
        var message = session is null ? null : await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId && m.SessionId == id && m.Body != "", cancellationToken);
        if (message is null)
        {
            return ChatNotFound();
        }

        var outcome = await assist.TranslateAsync(TranslationSource.ChatMessage, message.Id, message.Body, request.Language!, [session!.VisitorName, session.VisitorEmail], cancellationToken);
        return outcome.Value is { } translation
            ? TypedResults.Ok(new Ai.AiTranslationResponse(translation.Text, translation.Language, translation.Cached))
            : Ai.AiFeatureEndpoints.AiProblem(outcome.ErrorCode, outcome.Error);
    }

    /// <summary>
    /// The virtual agent answers the visitor's newest message (task E5). It hands the chat over when
    /// it says so and someone is online; when the AI cannot answer, the chat goes to the queue, or
    /// stays with a short note offering a message when nobody is online.
    /// </summary>
    private static async Task AssistantAnswerAsync(ZelvoDbContext db, CurrentUser user, VirtualAgent assistant, ChatSession session, CancellationToken cancellationToken)
    {
        var history = await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == session.Id && (m.Sender == ChatSender.Visitor || m.Sender == ChatSender.Assistant))
            .OrderByDescending(m => m.Sequence).Take(VirtualAgent.HistoryTurns).ToListAsync(cancellationToken);
        history.Reverse();
        var readers = (await CatalogScope.OfAsync(db, user, cancellationToken)).Readers;
        var (answer, _) = await assistant.AnswerAsync(
            [.. history.Select(m => new VirtualAgentTurn(m.Sender == ChatSender.Visitor, m.Body))], session.Language, readers, user.Account is not null,
            [session.VisitorName, session.VisitorEmail], session.Id.ToString(), cancellationToken);
        var tracked = await db.ChatSessions.SingleAsync(c => c.Id == session.Id, cancellationToken);
        if (answer is not null)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                SessionId = tracked.Id,
                Sender = ChatSender.Assistant,
                Body = IncidentIntake.Clean(answer.Text, ChatMessage.BodyMaxLength),
                Citations = answer.Articles.Count == 0 ? null : [.. answer.Articles.Select(a => a.Id)],
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        if ((answer is null || answer.Handover) && await OnlineAsync(db, tracked.GroupId, cancellationToken))
        {
            HandOver(db, tracked);
        }
        else if (answer is null)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                SessionId = tracked.Id,
                Sender = ChatSender.Assistant,
                Body = tracked.Language == LocalizedText.Arabic
                    ? "عذرًا، لا أستطيع الإجابة الآن، ولا يوجد موظف متاح حاليًا. يمكنكم ترك رسالة وسنعاود التواصل معكم."
                    : "Sorry, I cannot answer right now, and nobody is available at the moment. You can leave a message and we will get back to you.",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // The chat joins the group's queue, with the whole conversation, for the agent who accepts it.
    private static void HandOver(ZelvoDbContext db, ChatSession session)
    {
        session.State = ChatState.Waiting;
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, Body = string.Empty, Event = ChatEvents.Handover, CreatedAt = DateTimeOffset.UtcNow });
    }

    // Nobody online: the conversation with the virtual agent becomes an incident (E2: a message left while nobody is online always does).
    private static async Task LeaveConversationAsync(
        ZelvoDbContext db, CurrentUser user, IncidentIntake intake, ITenantContext tenant, IAuditLog audit, ChatSession session, CancellationToken cancellationToken)
    {
        var widget = await db.ChatWidgets.AsNoTracking().SingleAsync(w => w.Id == session.WidgetId, cancellationToken);
        var requester = session.VisitorUserId is { } known
            ? await db.Users.SingleAsync(u => u.Id == known, cancellationToken)
            : await RequesterAsync(db, audit, session.VisitorEmail, session.VisitorName, session.Language, cancellationToken);
        var transcript = await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == session.Id).OrderBy(m => m.Sequence).ToListAsync(cancellationToken);
        var first = transcript.FirstOrDefault(m => m.Sender == ChatSender.Visitor)?.Body.Split('\n')[0].Trim();
        var title = first is { Length: >= 3 } line ? line : session.VisitorName;
        var description = string.Join("\n\n", transcript.Where(m => m.Sender is ChatSender.Visitor or ChatSender.Assistant && m.Body.Length > 0)
            .Select(m => (m.Sender == ChatSender.Visitor ? session.VisitorName : session.Language == LocalizedText.Arabic ? "المساعد الافتراضي" : "Virtual agent") + ": " + m.Body));
        var now = DateTimeOffset.UtcNow;
        var ticket = await intake.CreateAsync(tenant.TenantId!.Value, new IncidentIntakeRequest(
            title.Length <= 120 ? title : $"{title[..119]}…", IncidentIntake.Clean(description, Ticket.DescriptionMaxLength), TicketChannel.Chat, requester.Id,
            CreatedById: user.Account?.Id, CategoryId: widget.CategoryId, GroupId: session.GroupId), now, cancellationToken);
        session.TicketId = ticket.Id;
        session.VisitorUserId ??= requester.Id;
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, Body = ticket.Number, Event = ChatEvents.Ticket, CreatedAt = now });
        End(db, session, ChatEndedBy.Visitor, null);
    }

    internal static async Task<IResult> VisitorEndAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var session = await VisitorSessionAsync(db, http, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.State != ChatState.Ended)
        {
            End(db, session, ChatEndedBy.Visitor, null);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await VisitorViewAsync(db, session, null, cancellationToken));
    }

    // --- Agents ---

    internal static async Task<IResult> QueueAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = MeOrNull(user);
        if (me is null)
        {
            return OnlyPeople();
        }

        var groups = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == me).Select(m => m.GroupId).ToListAsync(cancellationToken);
        var waiting = await db.ChatSessions.AsNoTracking().Where(c => c.State == ChatState.Waiting && groups.Contains(c.GroupId))
            .OrderBy(c => c.StartedAt).Take(100).ToListAsync(cancellationToken);
        var mine = await db.ChatSessions.AsNoTracking().Where(c => c.State == ChatState.Active && c.AgentId == me)
            .OrderBy(c => c.AcceptedAt).ToListAsync(cancellationToken);
        var recent = await db.ChatSessions.AsNoTracking().Where(c => c.State == ChatState.Ended && c.AgentId == me)
            .OrderByDescending(c => c.EndedAt).Take(10).ToListAsync(cancellationToken);
        var limit = groups.Count == 0 ? SupportGroupDefaults.ChatLimit
            : await db.SupportGroups.AsNoTracking().Where(g => groups.Contains(g.Id)).MaxAsync(g => g.ChatLimit, cancellationToken);
        var available = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.AvailableForAssignment).SingleAsync(cancellationToken);
        var all = waiting.Concat(mine).Concat(recent).ToList();
        var names = await GroupNamesAsync(db, all.Select(c => c.GroupId), cancellationToken);
        var lastMessages = await LastMessagesAsync(db, [.. all.Select(c => c.Id)], cancellationToken);
        return TypedResults.Ok(new ChatQueueResponse(
            [.. waiting.Select(c => ChatSummaryResponse.From(c, names, lastMessages))],
            [.. mine.Select(c => ChatSummaryResponse.From(c, names, lastMessages))],
            [.. recent.Select(c => ChatSummaryResponse.From(c, names, lastMessages))],
            limit, available));
    }

    internal static async Task<IResult> AgentGetAsync(Guid id, long? after, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var session = await AgentSessionAsync(db, user, id, cancellationToken);
        if (session is null)
        {
            return ChatNotFound();
        }

        // The full transcript is a view of personal data; reloading new messages is not.
        if (after is null)
        {
            audit.Add(AuditCategory.View, "channels.chat.viewed", "chat_session", session.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await AgentViewAsync(db, session, after, cancellationToken));
    }

    internal static async Task<IResult> AcceptAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var session = await AgentSessionAsync(db, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        var me = user.Account!.Id;
        if (session.State != ChatState.Waiting)
        {
            return session.AgentId == me && session.State == ChatState.Active
                ? TypedResults.Ok(await AgentViewAsync(db, session, null, cancellationToken))
                : ApiProblem.Create(StatusCodes.Status409Conflict, "chat-taken", "Another agent accepted the chat, or it ended.");
        }

        if (!await db.Users.AnyAsync(u => u.Id == me && u.AvailableForAssignment, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "agent-unavailable", "Set yourself as available to accept chats.");
        }

        var limit = await db.SupportGroups.Where(g => g.Id == session.GroupId).Select(g => g.ChatLimit).SingleAsync(cancellationToken);
        if (await db.ChatSessions.CountAsync(c => c.State == ChatState.Active && c.AgentId == me, cancellationToken) >= limit)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "chat-limit-reached", $"You are answering {limit} chats, the group's limit. End one first.");
        }

        var now = DateTimeOffset.UtcNow;
        session.State = ChatState.Active;
        session.AgentId = me;
        session.AcceptedAt ??= now;
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, AuthorId = me, Body = string.Empty, Event = ChatEvents.Accepted, CreatedAt = now });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "chat-taken", "Another agent accepted the chat, or it ended.");
        }
        return TypedResults.Ok(await AgentViewAsync(db, session, null, cancellationToken));
    }

    internal static async Task<IResult> AgentWriteAsync(
        Guid id, ChatMessageRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var session = await AgentSessionAsync(db, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.State != ChatState.Active || session.AgentId != user.Account!.Id)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "chat-not-yours", "Accept the chat before answering it.");
        }

        var body = IncidentIntake.Clean(request.Body!, ChatMessage.BodyMaxLength);
        if (body.Length == 0)
        {
            return UserEndpoints.Validation("body", "Write a message.");
        }

        var message = new ChatMessage { SessionId = session.Id, Sender = ChatSender.Agent, AuthorId = user.Account.Id, Body = body, CreatedAt = DateTimeOffset.UtcNow };
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/chat/sessions/{id}", ChatMessageResponse.From(message, user.Account.DisplayName));
    }

    internal static async Task<IResult> TransferAsync(
        Guid id, TransferChatRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var session = await AgentSessionAsync(db, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.State == ChatState.Ended)
        {
            return Ended();
        }

        if ((request.UserId is null) == (request.GroupId is null))
        {
            return UserEndpoints.Validation("userId", "Choose a colleague or a group.");
        }

        var now = DateTimeOffset.UtcNow;
        if (request.UserId is { } colleague)
        {
            if (!await ChatAgents(db, session.GroupId).AnyAsync(u => u.Id == colleague, cancellationToken))
            {
                return UserEndpoints.Validation("userId", "Choose a colleague who handles chats in this group.");
            }

            session.AgentId = colleague;
            session.State = ChatState.Active;
            session.AcceptedAt ??= now;
        }
        else
        {
            if (!await db.SupportGroups.AnyAsync(g => g.Id == request.GroupId && g.Active, cancellationToken))
            {
                return UserEndpoints.Validation("groupId", "Choose an active group.");
            }

            session.GroupId = request.GroupId!.Value;
            session.AgentId = null;
            session.State = ChatState.Waiting;
        }

        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, AuthorId = session.AgentId, Body = string.Empty, Event = ChatEvents.Transferred, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await AgentViewAsync(db, session, null, cancellationToken));
    }

    internal static async Task<IResult> AgentEndAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var session = await AgentSessionAsync(db, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.State != ChatState.Ended)
        {
            End(db, session, ChatEndedBy.Agent, user.Account!.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await AgentViewAsync(db, session, null, cancellationToken));
    }

    internal static async Task<IResult> CreateTicketAsync(
        Guid id, ChatTicketRequest request, ZelvoDbContext db, CurrentUser user, IncidentIntake intake, ITenantContext tenant, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.IncidentsCreate))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You may not log incidents.");
        }

        var session = await AgentSessionAsync(db, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.TicketId is not null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "chat-has-ticket", "The chat already has a ticket.");
        }

        var widget = await db.ChatWidgets.AsNoTracking().SingleAsync(w => w.Id == session.WidgetId, cancellationToken);
        var me = user.Account!.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var requester = session.VisitorUserId is { } known
            ? await db.Users.SingleAsync(u => u.Id == known, cancellationToken)
            : await RequesterAsync(db, audit, session.VisitorEmail, session.VisitorName, session.Language, cancellationToken);
        if (requester.Status != UserStatus.Active)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "requester-disabled", "The visitor's account is disabled.");
        }

        var transcript = await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == session.Id).OrderBy(m => m.Sequence).ToListAsync(cancellationToken);
        var firstVisitorLine = transcript.FirstOrDefault(m => m.Sender == ChatSender.Visitor)?.Body.Split('\n')[0].Trim();
        var title = request.Title?.Trim() is { Length: >= 3 } given ? given : firstVisitorLine is { Length: >= 3 } line ? line : session.VisitorName;
        var assignee = await db.SupportGroupMembers.AnyAsync(m => m.GroupId == session.GroupId && m.UserId == me, cancellationToken)
            && user.Has(Permissions.IncidentsEdit) ? me : (Guid?)null;
        var now = DateTimeOffset.UtcNow;
        var ticket = await intake.CreateAsync(tenant.TenantId!.Value, new IncidentIntakeRequest(
            title.Length <= 120 ? title : $"{title[..119]}…", request.Description, TicketChannel.Chat, requester.Id, CreatedById: me,
            CategoryId: widget.CategoryId, GroupId: session.GroupId, AssigneeId: assignee), now, cancellationToken);
        // The new ticket's clocks exist once saved; the transcript's response then moves them.
        await db.SaveChangesAsync(cancellationToken);
        CopyTranscript(db, ticket, session, requester.Id, transcript);
        await intake.UpdateClocksAsync([ticket], now, cancellationToken);
        session.TicketId = ticket.Id;
        session.VisitorUserId ??= requester.Id;
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, AuthorId = me, Body = ticket.Number, Event = ChatEvents.Ticket, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await AgentViewAsync(db, session, null, cancellationToken));
    }

    internal static async Task<IResult> LinkTicketAsync(
        Guid id, LinkChatRequest request, ZelvoDbContext db, CurrentUser user, IncidentIntake intake, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.IncidentsEdit))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You may not change incidents.");
        }

        var session = await AgentSessionAsync(db, user, id, cancellationToken, track: true);
        if (session is null)
        {
            return ChatNotFound();
        }

        if (session.TicketId is not null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "chat-has-ticket", "The chat already has a ticket.");
        }

        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == request.TicketId && t.Type == TicketType.Incident, cancellationToken);
        if (ticket is null || ticket.State == TicketState.Closed)
        {
            return UserEndpoints.Validation("ticketId", "Choose an incident that is not closed.");
        }

        var requester = session.VisitorUserId
            ?? (await ContactAccounts.FindByEmailAsync(db, session.VisitorEmail, cancellationToken))?.Id;
        if (requester != ticket.RequesterId)
        {
            return UserEndpoints.Validation("ticketId", "Choose an incident of the person in the chat.");
        }

        var transcript = await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == session.Id).OrderBy(m => m.Sequence).ToListAsync(cancellationToken);
        CopyTranscript(db, ticket, session, ticket.RequesterId, transcript);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        await intake.UpdateClocksAsync([ticket], ticket.UpdatedAt, cancellationToken);
        session.TicketId = ticket.Id;
        session.VisitorUserId ??= ticket.RequesterId;
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, AuthorId = user.Account!.Id, Body = ticket.Number, Event = ChatEvents.Ticket, CreatedAt = DateTimeOffset.UtcNow });
        audit.Add(AuditCategory.Change, "channels.chat.linked", "ticket", ticket.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["details"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["chatSessionId"] = session.Id.ToString() } });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await AgentViewAsync(db, session, null, cancellationToken));
    }

    // --- Helpers ---

    /// <summary>Members of a group who may answer its chats now or later: active, with chat.handle.</summary>
    internal static IQueryable<UserAccount> ChatAgents(ZelvoDbContext db, Guid groupId) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && db.SupportGroupMembers.Any(m => m.GroupId == groupId && m.UserId == u.Id)
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.ChatHandle)));

    /// <summary>Whether a member of the group is available and under the group's chat limit.</summary>
    internal static async Task<bool> OnlineAsync(ZelvoDbContext db, Guid groupId, CancellationToken cancellationToken)
    {
        var limit = await db.SupportGroups.AsNoTracking().Where(g => g.Id == groupId && g.Active).Select(g => (int?)g.ChatLimit).SingleOrDefaultAsync(cancellationToken);
        return limit is { } max && await ChatAgents(db, groupId).AnyAsync(u => u.AvailableForAssignment
            && db.ChatSessions.Count(c => c.State == ChatState.Active && c.AgentId == u.Id) < max, cancellationToken);
    }

    private static bool IsAllowedOrigin(ChatWidget widget, string origin) =>
        NormalizeOrigin(origin) is { } normalized && widget.AllowedOrigins.Contains(normalized, StringComparer.Ordinal);

    private static IResult? OriginProblem(ChatWidget widget, string? origin)
    {
        if (origin is null)
        {
            return widget.OnPortal ? null : ApiProblem.Create(StatusCodes.Status403Forbidden, "origin-not-allowed", "This widget is not shown on the portal.");
        }

        return IsAllowedOrigin(widget, origin) ? null : ApiProblem.Create(StatusCodes.Status403Forbidden, "origin-not-allowed", "This website may not use the chat widget.");
    }

    internal static async Task<IResult?> LimitedAsync(IRateLimitStore limits, HttpContext http, string kind, string? partition, int permits, TimeSpan window, CancellationToken cancellationToken)
    {
        var key = $"zelvo:rl:{kind}:{Hash($"{http.Request.Headers["X-Zelvo-Tenant"]}|{partition ?? "unknown"}")[..32]}";
        if (await limits.HitAsync(key, window, cancellationToken) is { } count && count.Count > permits)
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(count.ResetsIn.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ApiProblem.Create(StatusCodes.Status429TooManyRequests, "rate-limited", "Too many requests. Try again after the time in Retry-After.");
        }

        return null;
    }

    /// <summary>The visitor's chat: by its token, or for chats of signed-in people by the caller's account. Null hides chats from everyone else.</summary>
    private static async Task<ChatSession?> VisitorSessionAsync(
        ZelvoDbContext db, HttpContext http, CurrentUser user, Guid id, CancellationToken cancellationToken, bool track = false, string? token = null)
    {
        var query = track ? db.ChatSessions : db.ChatSessions.AsNoTracking();
        var session = await query.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        if (session.VisitorTokenHash is { } hash)
        {
            var given = token ?? http.Request.Headers[TokenHeader].ToString();
            return given.Length is > 0 and <= 128 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(given)), Encoding.ASCII.GetBytes(hash)) ? session : null;
        }

        return user.Account is { } account && session.VisitorUserId == account.Id ? session : null;
    }

    /// <summary>A chat the agent may work on: one of their groups' chats, or one they answer. Null otherwise (404).</summary>
    private static async Task<ChatSession?> AgentSessionAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken, bool track = false)
    {
        if (user.Account is not { } account)
        {
            return null;
        }

        var query = track ? db.ChatSessions : db.ChatSessions.AsNoTracking();
        return await query.SingleOrDefaultAsync(c => c.Id == id
            && (c.AgentId == account.Id || db.SupportGroupMembers.Any(m => m.GroupId == c.GroupId && m.UserId == account.Id)), cancellationToken);
    }

    private static void End(ZelvoDbContext db, ChatSession session, ChatEndedBy by, Guid? agentId)
    {
        var now = DateTimeOffset.UtcNow;
        session.State = ChatState.Ended;
        session.EndedAt = now;
        session.EndedBy = by;
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, Sender = ChatSender.System, AuthorId = agentId, Body = string.Empty, Event = ChatEvents.Ended, CreatedAt = now });
    }

    /// <summary>
    /// Adds the chat's messages to the ticket's conversation as public comments (task B4's thread)
    /// with the chat's language. They raise no reply notifications: both sides saw them live. A
    /// chat answered by an agent counts as the ticket's response.
    /// </summary>
    private static void CopyTranscript(ZelvoDbContext db, Ticket ticket, ChatSession session, Guid requesterId, List<ChatMessage> transcript)
    {
        foreach (var message in transcript.Where(m => m.Sender != ChatSender.System && m.Body.Length > 0))
        {
            var byAgent = message.Sender == ChatSender.Agent && message.AuthorId is not null;
            db.TicketComments.Add(new TicketComment
            {
                TicketId = ticket.Id,
                Visibility = CommentVisibility.Public,
                Body = IncidentIntake.Clean(message.Body, TicketComment.BodyMaxLength),
                Language = session.Language,
                AuthorId = byAgent ? message.AuthorId!.Value : requesterId,
                ByStaff = byAgent,
                Source = CommentSource.Chat,
                ChatSessionId = session.Id,
                CreatedAt = message.CreatedAt,
            });
        }

        if (transcript.FirstOrDefault(m => m.Sender == ChatSender.Agent) is { } answered && ticket.RespondedAt is null)
        {
            ticket.RespondedAt = answered.CreatedAt < ticket.CreatedAt ? ticket.CreatedAt : answered.CreatedAt;
        }
    }

    private static async Task<UserAccount> RequesterAsync(ZelvoDbContext db, IAuditLog audit, string email, string? name, string? language, CancellationToken cancellationToken)
    {
        if (await ContactAccounts.FindByEmailAsync(db, email, cancellationToken) is { } existing)
        {
            return existing;
        }

        var created = await ContactAccounts.CreateAsync(db, email, name, language, cancellationToken);
        audit.Add(AuditCategory.Create, "channels.chat.requester-created", "user", created.Id.ToString());
        return created;
    }

    private static async Task<VisitorChatResponse> VisitorViewAsync(ZelvoDbContext db, ChatSession session, long? after, CancellationToken cancellationToken)
    {
        var (messages, names) = await MessagesAsync(db, session.Id, after, cancellationToken);
        var numbers = await CitationsAsync(db, messages, cancellationToken);
        var agentName = session.AgentId is { } agent ? names.GetValueOrDefault(agent) ?? await db.Users.AsNoTracking().Where(u => u.Id == agent).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken) : null;
        return new VisitorChatResponse(session.Id, session.State, agentName, session.EndedBy, [.. messages.Select(m => ChatMessageResponse.From(m, m.AuthorId is { } a ? names.GetValueOrDefault(a) : null, numbers))]);
    }

    private static async Task<AgentChatResponse> AgentViewAsync(ZelvoDbContext db, ChatSession session, long? after, CancellationToken cancellationToken)
    {
        var (messages, names) = await MessagesAsync(db, session.Id, after, cancellationToken);
        var numbers = await CitationsAsync(db, messages, cancellationToken);
        var groups = await GroupNamesAsync(db, [session.GroupId], cancellationToken);
        var number = session.TicketId is { } ticketId ? await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.Number).FirstOrDefaultAsync(cancellationToken) : null;
        var agentName = session.AgentId is { } agent ? await db.Users.AsNoTracking().Where(u => u.Id == agent).Select(u => u.DisplayName ?? u.Email).FirstOrDefaultAsync(cancellationToken) : null;
        return new AgentChatResponse(session.Id, session.State, session.VisitorName, session.VisitorEmail, session.VisitorUserId, session.Language, session.Origin,
            new NamedRef(session.GroupId, groups.GetValueOrDefault(session.GroupId) ?? LocalizedTextDto.From(new LocalizedText(null, null))),
            session.AgentId is { } agentId ? new PersonResponse(agentId, agentName) : null,
            session.TicketId, number, session.StartedAt, session.AcceptedAt, session.EndedAt, session.EndedBy,
            [.. messages.Select(m => ChatMessageResponse.From(m, m.AuthorId is { } a ? names.GetValueOrDefault(a) : null, numbers))]);
    }

    private static async Task<(List<ChatMessage> Messages, Dictionary<Guid, string?> Names)> MessagesAsync(ZelvoDbContext db, Guid sessionId, long? after, CancellationToken cancellationToken)
    {
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sessionId && (after == null || m.Sequence > after))
            .OrderBy(m => m.Sequence).Take(500).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, messages.Where(m => m.AuthorId is not null).Select(m => m.AuthorId!.Value), cancellationToken);
        return (messages, names);
    }

    // Numbers of the articles virtual agent answers cite, while they are still published.
    private static async Task<Dictionary<Guid, string>> CitationsAsync(ZelvoDbContext db, List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var ids = messages.SelectMany(m => m.Citations ?? []).Distinct().ToList();
        return ids.Count == 0 ? [] : await db.KbArticles.AsNoTracking().Where(a => ids.Contains(a.Id) && a.State == Data.Knowledge.ArticleState.Published)
            .ToDictionaryAsync(a => a.Id, a => a.Number, cancellationToken);
    }

    private static async Task<Dictionary<Guid, LocalizedTextDto>> GroupNamesAsync(ZelvoDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return (await db.SupportGroups.AsNoTracking().Where(g => list.Contains(g.Id)).Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken))
            .ToDictionary(g => g.Id, g => LocalizedTextDto.From(g.Name));
    }

    private static async Task<Dictionary<Guid, string>> LastMessagesAsync(ZelvoDbContext db, List<Guid> ids, CancellationToken cancellationToken) =>
        (await db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.SessionId) && m.Sender != ChatSender.System)
            .GroupBy(m => m.SessionId).Select(g => g.OrderByDescending(m => m.Sequence).Select(m => new { m.SessionId, m.Body }).First())
            .ToListAsync(cancellationToken))
        .ToDictionary(m => m.SessionId, m => m.Body.Length <= 120 ? m.Body : $"{m.Body[..119]}…");

    private static Guid? MeOrNull(CurrentUser user) => user.Account?.Id;

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static IResult OnlyPeople() => ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only people answer chats.");

    private static IResult Ended() => ApiProblem.Create(StatusCodes.Status409Conflict, "chat-ended", "The chat has ended.");

    private static IResult ChatNotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "chat-not-found", "There is no such chat.");

    private static IResult WidgetNotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "widget-not-found", "There is no such chat widget.");
}

/// <summary>Defaults of support groups used by chat.</summary>
internal static class SupportGroupDefaults
{
    /// <summary>Chats one member answers at a time when the group does not say.</summary>
    public const int ChatLimit = Data.Directory.SupportGroup.DefaultChatLimit;
}

/// <summary>Bilingual text in a request.</summary>
public sealed class LocalizedTextRequest
{
    /// <summary>English.</summary>
    [StringLength(ChatWidget.GreetingMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(ChatWidget.GreetingMaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A new chat widget, or changes to one (values left out stay as they are).</summary>
public sealed class ChatWidgetRequest
{
    /// <summary>Title of the chat window (at most 100 characters per language).</summary>
    public LocalizedTextRequest? Name { get; init; }

    /// <summary>First message visitors see.</summary>
    public LocalizedTextRequest? Greeting { get; init; }

    /// <summary>The group that answers.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Category of tickets made from its chats.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>True removes the category.</summary>
    public bool? ClearCategory { get; init; }

    /// <summary>Websites that may embed it, such as https://www.entity.gov.ae.</summary>
    [MaxLength(ChatWidget.MaxOrigins + 10)]
    public IReadOnlyList<string>? AllowedOrigins { get; init; }

    /// <summary>Shown on the portal.</summary>
    public bool? OnPortal { get; init; }

    /// <summary>Accepts chats.</summary>
    public bool? Active { get; init; }

    /// <summary>The virtual agent answers first from the knowledge base (task E5; needs the AI feature virtualAgent).</summary>
    public bool? VirtualAgent { get; init; }
}

/// <summary>A visitor starting a chat.</summary>
public sealed class StartChatRequest
{
    /// <summary>Visitors: they read the privacy notice (needed when the organisation published one; task F5).</summary>
    public bool? PrivacyNoticeAccepted { get; init; }

    /// <summary>Visitors: their name.</summary>
    [StringLength(ChatSession.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>Visitors: their email address.</summary>
    [StringLength(Mailbox.AddressMaxLength)]
    public string? Email { get; init; }

    /// <summary>en or ar.</summary>
    [StringLength(5)]
    public string? Language { get; init; }

    /// <summary>The first message.</summary>
    [StringLength(ChatMessage.BodyMaxLength)]
    public string? Message { get; init; }

    /// <summary>The website the widget is embedded in (its origin); none on the portal.</summary>
    [StringLength(ChatWidget.OriginMaxLength)]
    public string? Origin { get; init; }
}

/// <summary>A message left while nobody is online.</summary>
public sealed class LeaveChatMessageRequest
{
    /// <summary>Visitors: they read the privacy notice (needed when the organisation published one; task F5).</summary>
    public bool? PrivacyNoticeAccepted { get; init; }

    /// <summary>Visitors: their name.</summary>
    [StringLength(ChatSession.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>Visitors: their email address.</summary>
    [StringLength(Mailbox.AddressMaxLength)]
    public string? Email { get; init; }

    /// <summary>en or ar.</summary>
    [StringLength(5)]
    public string? Language { get; init; }

    /// <summary>The message.</summary>
    [Required]
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Message { get; init; }

    /// <summary>The website the widget is embedded in; none on the portal.</summary>
    [StringLength(ChatWidget.OriginMaxLength)]
    public string? Origin { get; init; }
}

/// <summary>A chat message.</summary>
public sealed class ChatMessageRequest
{
    /// <summary>The text.</summary>
    [Required]
    [StringLength(ChatMessage.BodyMaxLength, MinimumLength = 1)]
    public string? Body { get; init; }
}

/// <summary>Where a chat goes: a colleague or a group.</summary>
public sealed class TransferChatRequest
{
    /// <summary>A colleague who handles chats in the chat's group.</summary>
    public Guid? UserId { get; init; }

    /// <summary>Another group's queue.</summary>
    public Guid? GroupId { get; init; }
}

/// <summary>A ticket made from a chat.</summary>
public sealed class ChatTicketRequest
{
    /// <summary>Title; default the visitor's first message.</summary>
    [StringLength(Ticket.TitleMaxLength)]
    public string? Title { get; init; }

    /// <summary>What happened (the transcript becomes the conversation).</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }
}

/// <summary>The incident a chat's transcript is added to.</summary>
public sealed class LinkChatRequest
{
    /// <summary>An open incident of the person in the chat.</summary>
    [Required]
    public Guid? TicketId { get; init; }
}

/// <summary>A chat widget as administrators see it.</summary>
internal sealed record ChatWidgetResponse(
    Guid Id, LocalizedTextDto Name, LocalizedTextDto Greeting, Guid GroupId, Guid? CategoryId, IReadOnlyList<string> AllowedOrigins, bool OnPortal, bool Active,
    bool VirtualAgent, DateTimeOffset UpdatedAt)
{
    public static ChatWidgetResponse From(ChatWidget w) => new(
        w.Id, LocalizedTextDto.From(w.Name), LocalizedTextDto.From(w.Greeting), w.GroupId, w.CategoryId, w.AllowedOrigins, w.OnPortal, w.Active, w.VirtualAgent, w.UpdatedAt);
}

/// <summary>What a visitor's chat window shows.</summary>
/// <param name="Allowed">Whether the website (or the portal) may show the widget.</param>
/// <param name="AllowedOrigins">Websites that may embed it (for the page's frame-ancestors policy).</param>
/// <param name="VirtualAgent">The virtual agent answers first (on for the widget and the AI feature available): a chat can start with nobody online.</param>
internal sealed record PublicChatWidgetResponse(Guid Id, LocalizedTextDto Name, LocalizedTextDto Greeting, bool Online, bool Allowed, IReadOnlyList<string> AllowedOrigins, bool VirtualAgent);

/// <summary>A started chat; visitors keep the token for the chat's lifetime.</summary>
internal sealed record StartChatResponse(Guid SessionId, string? Token);

/// <summary>The incident a left message became.</summary>
internal sealed record LeftMessageResponse(string Number);

/// <summary>A chat message.</summary>
/// <param name="Citations">Virtual agent answers: the knowledge articles it answered from.</param>
internal sealed record ChatMessageResponse(Guid Id, long Sequence, ChatSender Sender, string? AuthorName, string Body, string? Event, DateTimeOffset CreatedAt, IReadOnlyList<ChatCitation> Citations)
{
    public static ChatMessageResponse From(ChatMessage m, string? authorName, IReadOnlyDictionary<Guid, string>? numbers = null) =>
        new(m.Id, m.Sequence, m.Sender, authorName, m.Body, m.Event, m.CreatedAt,
            [.. (m.Citations ?? []).Where(id => numbers is null || numbers.ContainsKey(id)).Select(id => new ChatCitation(id, numbers?.GetValueOrDefault(id) ?? string.Empty))]);
}

/// <summary>A knowledge article a virtual agent answer used.</summary>
/// <param name="Id">The article (its page is /help/articles/{id}).</param>
/// <param name="Number">Its number, for example KB-000012.</param>
internal sealed record ChatCitation(Guid Id, string Number);

/// <summary>Asking for a person.</summary>
public sealed class HandoverRequest
{
    /// <summary>With nobody online: turn the conversation into an incident instead.</summary>
    public bool? LeaveMessage { get; init; }
}

/// <summary>What to translate a chat message into.</summary>
public sealed class ChatTranslationRequest
{
    /// <summary><c>en</c> or <c>ar</c>.</summary>
    [StringLength(5)]
    public string? Language { get; init; }
}

/// <summary>A chat as its visitor sees it.</summary>
internal sealed record VisitorChatResponse(Guid Id, ChatState State, string? AgentName, ChatEndedBy? EndedBy, IReadOnlyList<ChatMessageResponse> Messages);

/// <summary>A chat as agents see it.</summary>
internal sealed record AgentChatResponse(
    Guid Id, ChatState State, string VisitorName, string VisitorEmail, Guid? VisitorUserId, string Language, string? Origin, NamedRef Group, PersonResponse? Agent,
    Guid? TicketId, string? TicketNumber, DateTimeOffset StartedAt, DateTimeOffset? AcceptedAt, DateTimeOffset? EndedAt, ChatEndedBy? EndedBy,
    IReadOnlyList<ChatMessageResponse> Messages);

/// <summary>A chat in the queue.</summary>
internal sealed record ChatSummaryResponse(
    Guid Id, ChatState State, string VisitorName, string Language, LocalizedTextDto Group, string? LastMessage, Guid? TicketId, DateTimeOffset StartedAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? EndedAt)
{
    public static ChatSummaryResponse From(ChatSession c, Dictionary<Guid, LocalizedTextDto> groups, Dictionary<Guid, string> last) => new(
        c.Id, c.State, c.VisitorName, c.Language, groups.GetValueOrDefault(c.GroupId) ?? LocalizedTextDto.From(new LocalizedText(null, null)),
        last.GetValueOrDefault(c.Id), c.TicketId, c.StartedAt, c.AcceptedAt, c.EndedAt);
}

/// <summary>An agent's chats.</summary>
/// <param name="Limit">Most chats the agent answers at a time.</param>
/// <param name="Available">Whether the agent is available (B2); unavailable agents accept no chats.</param>
internal sealed record ChatQueueResponse(
    IReadOnlyList<ChatSummaryResponse> Waiting, IReadOnlyList<ChatSummaryResponse> Mine, IReadOnlyList<ChatSummaryResponse> Recent, int Limit, bool Available);
