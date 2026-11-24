using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Mcp;

/// <summary>
/// The catalog request tools of the MCP server (task C4, product owner decision 2026-11-24): an AI
/// agent finds the services a named person may request, reads a service's card and form, requests
/// it for that person, and follows the request. Hidden services stay hidden: the agent sees what
/// the person may see. Requesting changes data, so it waits for a person's approval unless turned off.
/// </summary>
internal static partial class McpCatalogTools
{
    public static IReadOnlyList<McpTool> All { get; } =
    [
        new("search_services",
            "Finds published catalog services that a person (named by email address) may request: by words in the name, summary or keywords. Returns ids, codes, names, summaries, whether it is suspended, whether it has a form and required documents, and its fee in AED.",
            Permissions.RequestsRead, false,
            McpTools.Schema(new()
            {
                ["requesterEmail"] = McpTools.Str("Email address of the person the services are for; they must be a user of the organisation.", UserAccount.EmailMaxLength),
                ["text"] = McpTools.Str("Words to look for.", 200),
            }, "requesterEmail"),
            SearchAsync),
        new("get_service",
            "Reads a published service by code (IT-LAPTOP) or id, as the person named by email would see it: description, steps, fee, working days, required documents, FAQ, and its form's fields (key, label, type, required, options) with the form version id to send with request_service.",
            Permissions.RequestsRead, false,
            McpTools.Schema(new()
            {
                ["service"] = McpTools.Str("Service code or id.", 64),
                ["requesterEmail"] = McpTools.Str("Email address of the person it is for.", UserAccount.EmailMaxLength),
            }, "service", "requesterEmail"),
            GetAsync),
        new("request_service",
            "Requests a service for a person named by email, with the form's answers (field key to value) and the form version id from get_service. Services with required documents are requested by the person on the portal. Approvals and fulfilment follow as for any request. May need a person's approval: the answer then says pending-approval with an action id to check with get_action_status.",
            Permissions.RequestsEdit, true,
            McpTools.Schema(new()
            {
                ["service"] = McpTools.Str("Service code or id.", 64),
                ["requesterEmail"] = McpTools.Str("Email address of the person it is for.", UserAccount.EmailMaxLength),
                ["title"] = McpTools.Str("Short title; the service's name when left out.", Ticket.TitleMaxLength),
                ["description"] = McpTools.Str("More details.", Ticket.DescriptionMaxLength),
                ["formVersionId"] = McpTools.Str("The form version id from get_service.", 36),
                ["formAnswers"] = new JsonObject { ["type"] = "object", ["description"] = "Answers by form field key." },
            }, "service", "requesterEmail"),
            RequestAsync, PrepareRequestAsync),
        new("get_service_request",
            "Reads a service request by number (REQ-000123) or id: state, the service, its steps with the current one (the requester's journey), whether it waits for an approval, assignee, group and SLA clocks.",
            Permissions.RequestsRead, false,
            McpTools.Schema(new() { ["request"] = McpTools.Str("Request number (REQ-000123) or id.", 64) }, "request"),
            GetRequestAsync),
    ];

    private static async Task<(UserAccount? Person, McpToolResult? Problem)> PersonAsync(McpToolContext context, JsonElement args)
    {
        var email = McpTools.OptionalString(args, "requesterEmail")?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return (null, McpToolResult.Problem("validation-failed", "requesterEmail: name the person."));
        }

        var id = await McpTools.RequesterIdAsync(context, email);
        var person = id is { } i ? await context.Db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == i, context.CancellationToken) : null;
        return person is null ? (null, McpToolResult.Problem("validation-failed", "requesterEmail: there is no active user with this email address.")) : (person, null);
    }

    private static async Task<(PublishedService? Service, UserAccount? Person, McpToolResult? Problem)> ServiceAsync(McpToolContext context, JsonElement args)
    {
        var (person, problem) = await PersonAsync(context, args);
        if (problem is not null)
        {
            return (null, null, problem);
        }

        var key = McpTools.OptionalString(args, "service")?.Trim() ?? string.Empty;
        var scope = await CatalogScope.OfUserAsync(context.Db, person!, context.CancellationToken);
        var visible = await PublishedServices.VisibleAsync(context.Db, scope, context.CancellationToken);
        var found = visible.FirstOrDefault(v => Guid.TryParse(key, out var id) ? v.Service.Id == id : string.Equals(v.Service.Code, key, StringComparison.OrdinalIgnoreCase));
        return found is null
            ? (null, null, McpToolResult.Problem("not-found", "There is no published service with this code that the person may request."))
            : (found, person, null);
    }

    private static async Task<McpToolResult> SearchAsync(McpToolContext context, JsonElement args)
    {
        var (person, problem) = await PersonAsync(context, args);
        if (problem is not null)
        {
            return problem;
        }

        var text = McpTools.OptionalString(args, "text")?.Trim();
        var scope = await CatalogScope.OfUserAsync(context.Db, person!, context.CancellationToken);
        var visible = await PublishedServices.VisibleAsync(context.Db, scope, context.CancellationToken);
        bool Matches(PublishedService s) => string.IsNullOrEmpty(text)
            || new[] { s.Card.Name.En, s.Card.Name.Ar, s.Card.Summary.En, s.Card.Summary.Ar, s.Service.Code }.Concat(s.Card.Keywords)
                .Any(v => v?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        var items = new JsonArray([.. visible.Where(Matches).Take(25).Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Service.Id.ToString(),
            ["code"] = s.Service.Code,
            ["name"] = Text(s.Card.Name),
            ["summary"] = Text(s.Card.Summary),
            ["suspended"] = s.Service.State == Data.Catalog.ServiceState.Suspended,
            ["external"] = s.Card.Type == ServiceType.External,
            ["hasForm"] = s.Card.FormId is not null,
            ["requiredDocuments"] = s.Card.Documents.Count(d => d.Required),
            ["feeAed"] = s.Card.Fee.Paid ? s.Card.Fee.AmountAed : 0,
        })]);
        return McpToolResult.Ok(new JsonObject { ["items"] = items });
    }

    private static async Task<McpToolResult> GetAsync(McpToolContext context, JsonElement args)
    {
        var (found, _, problem) = await ServiceAsync(context, args);
        if (problem is not null)
        {
            return problem;
        }

        var card = found!.Card;
        JsonNode? form = null;
        if (card.FormId is { } formId && await FormResolver.PublishedAsync(context.Db, formId, context.CancellationToken) is { } resolved)
        {
            form = new JsonObject
            {
                ["formVersionId"] = resolved.Version.Id.ToString(),
                ["fields"] = JsonSerializer.SerializeToNode(resolved.PlainFields, FormJson.Options),
            };
        }

        return McpToolResult.Ok(new JsonObject
        {
            ["id"] = found.Service.Id.ToString(),
            ["code"] = found.Service.Code,
            ["state"] = found.Service.State.ToString(),
            ["name"] = Text(card.Name),
            ["summary"] = Text(card.Summary),
            ["description"] = Text(card.Description),
            ["steps"] = new JsonArray([.. card.Steps.Select(s => (JsonNode)Text(s.Name))]),
            ["feeAed"] = card.Fee.Paid ? card.Fee.AmountAed : 0,
            ["workingDays"] = card.Days,
            ["documents"] = new JsonArray([.. card.Documents.Select(d => (JsonNode)new JsonObject { ["key"] = d.Key, ["name"] = Text(d.Name), ["required"] = d.Required })]),
            ["faq"] = new JsonArray([.. card.Faq.Select(f => (JsonNode)new JsonObject { ["question"] = Text(f.Question), ["answer"] = Text(f.Answer) })]),
            ["form"] = form,
        });
    }

    private static async Task<(CreateServiceRequestRequest? Request, McpToolResult? Problem, string? Summary)> BuildAsync(McpToolContext context, JsonElement args)
    {
        var (found, person, problem) = await ServiceAsync(context, args);
        if (problem is not null)
        {
            return (null, problem, null);
        }

        JsonObject? answers = null;
        if (args.TryGetProperty("formAnswers", out var raw) && raw.ValueKind == JsonValueKind.Object)
        {
            answers = JsonNode.Parse(raw.GetRawText()) as JsonObject;
        }

        Guid? version = Guid.TryParse(McpTools.OptionalString(args, "formVersionId"), out var v) ? v : null;
        var request = new CreateServiceRequestRequest
        {
            ServiceId = found!.Service.Id,
            RequesterId = person!.Id,
            Title = McpTools.OptionalString(args, "title")?.Trim(),
            Description = McpTools.OptionalString(args, "description"),
            FormVersionId = version,
            FormAnswers = answers,
        };
        return (request, null, $"Request {found.Service.Code} for {person.Email}");
    }

    private static async Task<(McpPreparedChange?, McpToolResult?)> PrepareRequestAsync(McpToolContext context, JsonElement args)
    {
        var (_, problem, summary) = await BuildAsync(context, args);
        return problem is not null ? (null, problem) : (new McpPreparedChange(summary!, null), null);
    }

    private static async Task<McpToolResult> RequestAsync(McpToolContext context, JsonElement args)
    {
        var (request, problem, _) = await BuildAsync(context, args);
        if (problem is not null)
        {
            return problem;
        }

        return McpTools.From(await ServiceRequestEndpoints.CreateAsync(request!, context.Http, context.Db, context.User, context.Get<ITenantContext>(),
            context.Get<TenantSettingsStore>(), context.Get<Data.Auditing.IAuditLog>(), context.CancellationToken));
    }

    private static async Task<McpToolResult> GetRequestAsync(McpToolContext context, JsonElement args)
    {
        var key = McpTools.OptionalString(args, "request")?.Trim() ?? string.Empty;
        Guid? id = Guid.TryParse(key, out var parsed) ? parsed : null;
        if (id is null)
        {
            var number = key.ToUpperInvariant();
            id = await ServiceRequestEndpoints.Requests(context.Db).Where(t => t.Number == number).Select(t => (Guid?)t.Id).SingleOrDefaultAsync(context.CancellationToken);
        }

        return id is null
            ? McpToolResult.Problem("not-found", "There is no service request with this number.")
            : McpTools.From(await ServiceRequestEndpoints.GetAsync(id.Value, context.Http, context.Db, context.User, context.CancellationToken));
    }

    private static JsonObject Text(LocalizedText text) => new() { ["en"] = text.En, ["ar"] = text.Ar };
}
