using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Settings;
using Zelvo.Channels;
using Zelvo.Channels.Messaging;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Channels;

/// <summary>
/// The Teams app of module 19 (task J4; product owner decision 17 of 2026-11-26), on top of E3's
/// bot: a <b>"Raise a ticket" dialog</b> (Teams task module) with the category's form fields that
/// fit an Adaptive Card (else a link to the full form), a <b>message action</b> that raises a
/// ticket from any Teams message, <b>ticket status cards</b> and the commands "Raise a ticket", "My
/// tickets" and "Help". The person is always the one Microsoft signed the activity for, linked to
/// their Zelvo account; tickets are raised for them, as requesters, with the channel Teams.
/// </summary>
internal static partial class TeamsApp
{
    /// <summary>The message action's command id in the app manifest.</summary>
    public const string MessageActionCommand = "raiseFromMessage";

    /// <summary>Most open tickets a status reply lists.</summary>
    public const int StatusCardLimit = 5;

    private const string CardType = "application/vnd.microsoft.card.adaptive";

    /// <summary>What the person asked for in a message, if it is one of the app's commands (English or Arabic).</summary>
    internal static TeamsCommand? CommandOf(string? text) => text?.Trim().TrimEnd('.', '؟', '?').ToLowerInvariant() switch
    {
        "raise a ticket" or "new ticket" or "raise" or "رفع طلب" or "طلب جديد" => TeamsCommand.Raise,
        "my tickets" or "status" or "طلباتي" or "الحالة" => TeamsCommand.Status,
        "help" or "مساعدة" => TeamsCommand.Help,
        _ => null,
    };

    /// <summary>Answers a command: a card with the dialog button, the person's open tickets, or help.</summary>
    public static async Task AnswerCommandAsync(
        ZelvoDbContext db, MessagingChannel channel, MessagingContact contact, UserAccount? user, TeamsCommand command, string language, Uri webUrl,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case TeamsCommand.Status when user is not null:
                var tickets = await db.Tickets.AsNoTracking()
                    .Where(t => t.RequesterId == user.Id && t.State != TicketState.Closed && !t.Held)
                    .OrderByDescending(t => t.UpdatedAt).Take(StatusCardLimit).ToListAsync(cancellationToken);
                if (tickets.Count == 0)
                {
                    Send(db, channel, contact, Term(language, "teams.status.none"), null, language);
                    break;
                }

                var teamsOf = await TeamNamesAsync(db, tickets, cancellationToken);
                foreach (var ticket in tickets)
                {
                    Send(db, channel, contact, $"{ticket.Number}: {ticket.Title}", StatusCard(ticket, teamsOf.GetValueOrDefault(ticket.Id), language, webUrl), language);
                }

                break;
            case TeamsCommand.Status:
                Send(db, channel, contact, Term(language, "teams.not-linked-short"), null, language);
                break;
            default:
                Send(db, channel, contact, Term(language, command == TeamsCommand.Help ? "teams.help" : "teams.raise.prompt"), RaiseButtonCard(language), language);
                break;
        }
    }

    /// <summary>
    /// A Teams <c>invoke</c>: <c>task/fetch</c> and <c>task/submit</c> of the dialog, and
    /// <c>composeExtension/fetchTask</c> and <c>composeExtension/submitAction</c> of the message
    /// action. Answers with the invoke response body Teams shows (the next card, or a message).
    /// </summary>
    public static async Task<JsonObject> InvokeAsync(
        TeamsInvoke invoke, ZelvoDbContext db, IServiceProvider services, MessagingChannel channel, MessagingContact contact, UserAccount? user,
        string language, Uri webUrl, CancellationToken cancellationToken)
    {
        if (user is null)
        {
            var tenant = (await services.GetRequiredService<NotificationComposer>().LetterheadAsync(cancellationToken)).Name.Get(language) ?? string.Empty;
            return Message(Term(language, "teams.not-linked").Replace("{tenant}", tenant, StringComparison.Ordinal));
        }

        var data = invoke.Data ?? [];
        switch (invoke.Name)
        {
            case "task/fetch":
                return Continue(Term(language, "teams.raise.title"), await RaiseCardAsync(db, language, null, null, null, null, cancellationToken));
            case "composeExtension/fetchTask":
            {
                // Raise from a message: its text as the description, a link back to it.
                var text = PlainText(JsonPath.Text(invoke.Value, "messagePayload.body.content"));
                var link = JsonPath.Text(invoke.Value, "messagePayload.linkToMessage");
                var title = text is { Length: > 0 } ? MessagingIntake.TitleOf(text) : null;
                return Continue(Term(language, "teams.raise.title"), await RaiseCardAsync(db, language, title, text, link, null, cancellationToken));
            }

            case "task/submit" or "composeExtension/submitAction":
                return await SubmitAsync(data, db, services, channel, contact, user, language, webUrl, cancellationToken);
            default:
                return [];
        }
    }

    private static async Task<JsonObject> SubmitAsync(
        JsonObject data, ZelvoDbContext db, IServiceProvider services, MessagingChannel channel, MessagingContact contact, UserAccount user, string language,
        Uri webUrl, CancellationToken cancellationToken)
    {
        var title = JsonPath.Text(data, "title")?.Trim();
        var description = JsonPath.Text(data, "description")?.Trim();
        var link = JsonPath.Text(data, "messageLink");
        var categoryId = Guid.TryParse(JsonPath.Text(data, "categoryId"), out var parsed) ? parsed : (Guid?)null;
        if (categoryId is { } chosen && !await db.Categories.AsNoTracking().AnyAsync(c => c.Id == chosen && c.Active, cancellationToken))
        {
            categoryId = null;
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length < 3)
        {
            return Continue(Term(language, "teams.raise.title"),
                await RaiseCardAsync(db, language, title, description, link, Term(language, "teams.raise.title-required"), cancellationToken, categoryId));
        }

        var form = await FormResolver.ForTicketAsync(db, TicketType.Incident, categoryId ?? channel.CategoryId, cancellationToken);
        var fullForm = new Uri(webUrl, $"{language}/my-incidents/new{(categoryId is { } c ? $"?category={c}" : string.Empty)}");
        JsonObject? answers = null;
        if (form is not null && form.PlainFields.Count > 0)
        {
            // A required field a card cannot show: the full form in Zelvo instead.
            if (form.PlainFields.Any(f => f.Required && !FitsCard(f)))
            {
                return Continue(Term(language, "teams.raise.title"), FullFormCard(language, fullForm));
            }

            if (JsonPath.Text(data, "step") != "form")
            {
                return Continue(Term(language, "teams.raise.title"), FormCard(form, language, title, description, link, categoryId, fullForm, null));
            }

            answers = AnswersOf(form, data);
        }

        var accounts = services.GetRequiredService<CurrentUser>();
        accounts.Set(user, [], hasMfa: false);
        var audit = services.GetRequiredService<IAuditLog>();
        var auditContext = services.GetRequiredService<AuditContext>();
        auditContext.SetActor(AuditActorType.User, user.Id.ToString(), user.DisplayName ?? user.Email);
        auditContext.SetMetadata("via", "teams");
        var policy = await services.GetRequiredService<TenantSettingsStore>().GetAsync(IncidentSettings.Attachments, cancellationToken);
        var (prepared, problem) = form is null
            ? (null, null)
            : await FormSubmission.PrepareAsync(db, accounts, audit, form, user.Id, form.Version.Id, answers, policy, cancellationToken);
        if (problem is not null)
        {
            return Continue(Term(language, "teams.raise.title"),
                FormCard(form!, language, title, description, link, categoryId, fullForm, ProblemText(problem, language), answers));
        }

        var text = link is { Length: > 0 } ? $"{description}\n\n{Term(language, "teams.raise.from-message")}: {link}".Trim() : description;
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ticket = await services.GetRequiredService<IncidentIntake>().CreateAsync(channel.TenantId, new IncidentIntakeRequest(
            title, text, TicketChannel.Teams, user.Id, CategoryId: categoryId ?? channel.CategoryId, GroupId: categoryId is null ? channel.GroupId : null,
            MessagingContactId: contact.TeamsConversationId is null ? null : contact.Id), now, cancellationToken);
        prepared?.Attach(db, ticket);
        audit.Add(AuditCategory.Create, "channels.teams.ticket-raised", "ticket", ticket.Id.ToString(),
            new Dictionary<string, object?> { ["fromMessage"] = link is { Length: > 0 } });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The status card in the person's chat with the app, when there is one.
        if (contact.TeamsConversationId is not null)
        {
            var team = (await TeamNamesAsync(db, [ticket], cancellationToken)).GetValueOrDefault(ticket.Id);
            Send(db, channel, contact, $"{ticket.Number}: {ticket.Title}", StatusCard(ticket, team, language, webUrl), language);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Message(Term(language, "teams.raise.done").Replace("{number}", ticket.Number, StringComparison.Ordinal));
    }

    /// <summary>Field types an Adaptive Card can ask for (files, people, places, signatures, tables and configuration items cannot).</summary>
    internal static bool FitsCard(FormField field) =>
        field.Type is FieldType.Text or FieldType.Number or FieldType.Date or FieldType.Dropdown or FieldType.Checkbox or FieldType.Radio or FieldType.RichText;

    /// <summary>The card's answers as the form engine reads them: numbers, true or false, lists for several choices.</summary>
    internal static JsonObject AnswersOf(ResolvedForm form, JsonObject data)
    {
        var answers = new JsonObject();
        foreach (var field in form.PlainFields.Where(FitsCard))
        {
            var raw = JsonPath.Text(data, $"f_{field.Key}");
            switch (field.Type)
            {
                case FieldType.Checkbox:
                    answers[field.Key] = raw == "true";
                    break;
                case FieldType.Number when decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                    answers[field.Key] = number;
                    break;
                case FieldType.Dropdown when field.Multiple:
                    if (!string.IsNullOrEmpty(raw))
                    {
                        answers[field.Key] = new JsonArray([.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => (JsonNode)v)]);
                    }

                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        answers[field.Key] = raw;
                    }

                    break;
            }
        }

        return answers;
    }

    /// <summary>The first card: title, description and category (a link to the Teams message when raised from one).</summary>
    internal static async Task<JsonObject> RaiseCardAsync(
        ZelvoDbContext db, string language, string? title, string? description, string? messageLink, string? error, CancellationToken cancellationToken, Guid? categoryId = null)
    {
        var categories = await db.Categories.AsNoTracking().Where(c => c.Active).OrderBy(c => c.Depth).ThenBy(c => c.Position).ToListAsync(cancellationToken);
        var names = categories.ToDictionary(c => c.Id);
        string PathOf(Category category)
        {
            var parts = new List<string>();
            for (var node = category; node is not null; node = node.ParentId is { } parent ? names.GetValueOrDefault(parent) : null)
            {
                parts.Insert(0, node.Name.Get(language) ?? node.Name.En ?? node.Name.Ar ?? string.Empty);
            }

            return string.Join(" › ", parts);
        }

        var choices = new JsonArray([.. categories.Select(c => (JsonNode)new JsonObject { ["title"] = PathOf(c), ["value"] = c.Id.ToString() }).OrderBy(c => (string)c["title"]!, StringComparer.CurrentCulture)]);
        var body = new JsonArray();
        if (error is not null)
        {
            body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = error, ["color"] = "Attention", ["wrap"] = true });
        }

        body.Add(new JsonObject { ["type"] = "Input.Text", ["id"] = "title", ["label"] = Term(language, "teams.raise.title-label"), ["isRequired"] = true, ["maxLength"] = Ticket.TitleMaxLength, ["value"] = title ?? string.Empty });
        body.Add(new JsonObject { ["type"] = "Input.Text", ["id"] = "description", ["label"] = Term(language, "teams.raise.description-label"), ["isMultiline"] = true, ["maxLength"] = 4000, ["value"] = description ?? string.Empty });
        if (choices.Count > 0)
        {
            body.Add(new JsonObject
            {
                ["type"] = "Input.ChoiceSet", ["id"] = "categoryId", ["label"] = Term(language, "teams.raise.category-label"), ["style"] = "compact",
                ["placeholder"] = Term(language, "teams.raise.category-none"), ["choices"] = choices, ["value"] = categoryId?.ToString() ?? string.Empty,
            });
        }

        return Card(language, body, new JsonArray(Submit(Term(language, "teams.raise.next"), new JsonObject { ["zelvo"] = "raise", ["step"] = "ticket", ["messageLink"] = messageLink })));
    }

    /// <summary>The second card: the category's form fields that fit a card, with the answers so far and what was wrong.</summary>
    internal static JsonObject FormCard(
        ResolvedForm form, string language, string title, string? description, string? messageLink, Guid? categoryId, Uri fullForm, string? error, JsonObject? answers = null)
    {
        var body = new JsonArray();
        if (error is not null)
        {
            body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = error, ["color"] = "Attention", ["wrap"] = true });
        }

        body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = title, ["weight"] = "Bolder", ["wrap"] = true });
        foreach (var field in form.PlainFields)
        {
            if (!FitsCard(field))
            {
                continue;
            }

            var label = field.Label.Get(language) ?? field.Label.En ?? field.Label.Ar ?? field.Key;
            var input = new JsonObject { ["id"] = $"f_{field.Key}", ["label"] = label, ["isRequired"] = field.Required };
            var value = answers?[field.Key];
            switch (field.Type)
            {
                case FieldType.Number:
                    input["type"] = "Input.Number";
                    if (field.Validation?.Min is { } min)
                    {
                        input["min"] = min;
                    }

                    if (field.Validation?.Max is { } max)
                    {
                        input["max"] = max;
                    }

                    break;
                case FieldType.Date:
                    input["type"] = "Input.Date";
                    break;
                case FieldType.Checkbox:
                    input["type"] = "Input.Toggle";
                    input["title"] = label;
                    input["valueOn"] = "true";
                    input["valueOff"] = "false";
                    break;
                case FieldType.Dropdown or FieldType.Radio:
                    input["type"] = "Input.ChoiceSet";
                    input["style"] = field.Type == FieldType.Radio ? "expanded" : "compact";
                    input["isMultiSelect"] = field.Type == FieldType.Dropdown && field.Multiple;
                    input["choices"] = new JsonArray([.. (field.Options ?? []).Select(o => (JsonNode)new JsonObject { ["title"] = o.Label.Get(language) ?? o.Label.En ?? o.Value, ["value"] = o.Value })]);
                    break;
                default:
                    input["type"] = "Input.Text";
                    input["isMultiline"] = field.Multiline || field.Type == FieldType.RichText;
                    if (field.Validation?.MaxLength is { } maxLength)
                    {
                        input["maxLength"] = maxLength;
                    }

                    break;
            }

            if (value is not null)
            {
                input["value"] = value switch
                {
                    JsonArray list => string.Join(',', list.Select(v => v?.ToString())),
                    _ when field.Type == FieldType.Checkbox => value.ToString().ToLowerInvariant(),
                    _ => value.ToString(),
                };
            }

            body.Add(input);
        }

        if (form.PlainFields.Any(f => !FitsCard(f)))
        {
            body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = Term(language, "teams.raise.more-in-zelvo"), ["isSubtle"] = true, ["wrap"] = true });
        }

        var carry = new JsonObject
        {
            ["zelvo"] = "raise", ["step"] = "form", ["title"] = title, ["description"] = description, ["messageLink"] = messageLink, ["categoryId"] = categoryId?.ToString(),
        };
        return Card(language, body, new JsonArray(
            Submit(Term(language, "teams.raise.submit"), carry),
            new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = Term(language, "teams.raise.full-form"), ["url"] = fullForm.AbsoluteUri }));
    }

    /// <summary>A form a card cannot show: the link to the full form in Zelvo.</summary>
    internal static JsonObject FullFormCard(string language, Uri fullForm) =>
        Card(language,
            new JsonArray(new JsonObject { ["type"] = "TextBlock", ["text"] = Term(language, "teams.raise.needs-full-form"), ["wrap"] = true }),
            new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = Term(language, "teams.raise.full-form"), ["url"] = fullForm.AbsoluteUri }));

    /// <summary>A ticket's status: number, title, state and the team that has it (never a person, decided 2026-11-24), and a link.</summary>
    internal static JsonObject StatusCard(Ticket ticket, LocalizedText? team, string language, Uri webUrl)
    {
        var facts = new JsonArray
        {
            new JsonObject { ["title"] = Term(language, "teams.status.state"), ["value"] = Term(language, $"ticket.state.{ticket.State}") },
        };
        if ((team?.Get(language) ?? team?.En ?? team?.Ar) is { } teamName)
        {
            facts.Add(new JsonObject { ["title"] = Term(language, "teams.status.team"), ["value"] = teamName });
        }

        var path = ticket.Type switch
        {
            TicketType.Incident => $"{language}/my-incidents/{ticket.Id}",
            TicketType.ServiceRequest => $"{language}/my-requests/{ticket.Id}",
            _ => $"{language}/my-tickets",
        };
        return Card(language,
            new JsonArray(
                new JsonObject { ["type"] = "TextBlock", ["text"] = ticket.Number, ["weight"] = "Bolder", ["size"] = "Medium" },
                new JsonObject { ["type"] = "TextBlock", ["text"] = ticket.Title, ["wrap"] = true },
                new JsonObject { ["type"] = "FactSet", ["facts"] = facts }),
            new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = Term(language, "teams.status.open"), ["url"] = new Uri(webUrl, path).AbsoluteUri }));
    }

    /// <summary>A card with the button that opens the "Raise a ticket" dialog (<c>task/fetch</c>).</summary>
    internal static JsonObject RaiseButtonCard(string language) =>
        Card(language,
            new JsonArray(new JsonObject { ["type"] = "TextBlock", ["text"] = Term(language, "teams.raise.prompt"), ["wrap"] = true }),
            new JsonArray(new JsonObject
            {
                ["type"] = "Action.Submit", ["title"] = Term(language, "teams.raise.title"),
                ["data"] = new JsonObject { ["msteams"] = new JsonObject { ["type"] = "task/fetch" }, ["zelvo"] = "raise" },
            }));

    /// <summary>The text of a Teams message (HTML) without tags, as plain lines.</summary>
    internal static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = BlockBreak().Replace(html, "\n");
        text = Tag().Replace(text, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return text.Length <= 4000 ? text : text[..4000];
    }

    private static string ProblemText(Microsoft.AspNetCore.Http.IResult problem, string language) =>
        problem is Microsoft.AspNetCore.Http.HttpResults.ValidationProblem validation
            ? string.Join(" ", validation.ProblemDetails.Errors.SelectMany(e => e.Value))
            : Term(language, "teams.raise.failed");

    private static async Task<Dictionary<Guid, LocalizedText?>> TeamNamesAsync(ZelvoDbContext db, IReadOnlyCollection<Ticket> tickets, CancellationToken cancellationToken)
    {
        var groupIds = tickets.Where(t => t.GroupId is not null).Select(t => t.GroupId!.Value).Distinct().ToList();
        var groups = await db.SupportGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        return tickets.ToDictionary(t => t.Id, t => t.GroupId is { } g ? groups.GetValueOrDefault(g) : null);
    }

    private static void Send(ZelvoDbContext db, MessagingChannel channel, MessagingContact contact, string text, JsonObject? card, string language) =>
        db.ChannelMessages.Add(new ChannelMessage
        {
            TenantId = channel.TenantId,
            ChannelId = channel.Id,
            ContactId = contact.Id,
            Direction = MessageDirection.Out,
            Kind = card is null ? ChannelMessageKind.Text : ChannelMessageKind.Card,
            Address = contact.Address,
            Text = text.Length <= ChannelMessage.TextMaxLength ? text : text[..ChannelMessage.TextMaxLength],
            Payload = card?.ToJsonString(),
            Language = language,
            Status = ChannelMessageStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow,
        });

    private static JsonObject Card(string language, JsonArray body, JsonArray actions) => new()
    {
        ["type"] = "AdaptiveCard",
        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
        ["version"] = "1.4",
        ["rtl"] = language == LocalizedText.Arabic,
        ["body"] = body,
        ["actions"] = actions,
    };

    private static JsonObject Submit(string title, JsonObject data) => new() { ["type"] = "Action.Submit", ["title"] = title, ["data"] = data };

    private static JsonObject Continue(string title, JsonObject card) => new()
    {
        ["task"] = new JsonObject
        {
            ["type"] = "continue",
            ["value"] = new JsonObject { ["title"] = title, ["width"] = "medium", ["height"] = "large", ["card"] = new JsonObject { ["contentType"] = CardType, ["content"] = card } },
        },
    };

    private static JsonObject Message(string text) => new() { ["task"] = new JsonObject { ["type"] = "message", ["value"] = text } };

    private static string Term(string language, string key) => NotificationTexts.For(language).Terms.GetValueOrDefault(key) ?? key;

    [GeneratedRegex("<(br|/p|/div|/li)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockBreak();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();
}

/// <summary>The commands of the Teams app.</summary>
internal enum TeamsCommand
{
    /// <summary>Open the "Raise a ticket" dialog.</summary>
    Raise,

    /// <summary>The person's open tickets as status cards.</summary>
    Status,

    /// <summary>What the app does.</summary>
    Help,
}

/// <summary>A Teams invoke activity.</summary>
/// <param name="Name">For example <c>task/fetch</c>.</param>
/// <param name="Value">The activity's value.</param>
/// <param name="Data">The submitted data (the card's inputs and the button's data).</param>
internal sealed record TeamsInvoke(string Name, JsonObject? Value, JsonObject? Data)
{
    /// <summary>Reads an invoke activity.</summary>
    public static TeamsInvoke Of(JsonNode activity)
    {
        var value = activity["value"] as JsonObject;
        return new TeamsInvoke(JsonPath.Text(activity, "name") ?? string.Empty, value, value?["data"] as JsonObject);
    }
}
