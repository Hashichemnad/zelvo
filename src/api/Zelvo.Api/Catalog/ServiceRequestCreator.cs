using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Settings;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;
using Zelvo.Tickets.Sla;
using Zelvo.Workflows.Approvals;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Catalog;

/// <summary>What a new service request is made of (one service or a bundle).</summary>
internal sealed record NewServiceRequest
{
    /// <summary>The published service or bundle, as the requester may see it.</summary>
    public required PublishedService Service { get; init; }

    /// <summary>Who it is for.</summary>
    public required UserAccount Requester { get; init; }

    /// <summary>Who made it (a person); none for AI agents and proactive starts.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>The AI agent that made it (task C4).</summary>
    public Guid? ApiClientId { get; init; }

    /// <summary>How it came in.</summary>
    public TicketChannel Channel { get; init; } = TicketChannel.Portal;

    /// <summary>Title; the service's name when empty.</summary>
    public string? Title { get; init; }

    /// <summary>More details.</summary>
    public string? Description { get; init; }

    /// <summary>The version of the service's (or bundle's own) form that was filled.</summary>
    public Guid? FormVersionId { get; init; }

    /// <summary>
    /// The answers. For a bundle, one combined set: each form takes the answers of its own fields,
    /// so a question several forms ask is answered once (task C5).
    /// </summary>
    public JsonObject? Answers { get; init; }

    /// <summary>Required documents of a single service.</summary>
    public IReadOnlyDictionary<string, List<Guid>>? Documents { get; init; }

    /// <summary>Who uploaded the documents (the caller).</summary>
    public Guid? UploaderId { get; init; }

    /// <summary>A bundle's parts: the optional services chosen, and each part's form version and documents.</summary>
    public IReadOnlyList<BundlePartInput>? Parts { get; init; }

    /// <summary>The proactive run it comes from (task C5).</summary>
    public Guid? ProactiveRunId { get; init; }
}

/// <summary>One part of a bundle request: a service of the bundle with its form version and documents.</summary>
internal sealed class BundlePartInput
{
    /// <summary>The service of the bundle. Mandatory services are always included; optional ones only when listed.</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>The version of the service's form that was filled.</summary>
    public Guid? FormVersionId { get; init; }

    /// <summary>Required documents: document key → uploaded file ids.</summary>
    public Dictionary<string, List<Guid>>? Documents { get; init; }
}

/// <summary>
/// Makes service requests (tasks C2–C5): checks the service, its prerequisites, its form and its
/// documents, numbers the ticket and either asks for approval before assignment or releases it to
/// the service desk. A bundle becomes a bundle request with one child request per service, the
/// first stage released and later stages held until the stage before is complete. The caller saves
/// inside its transaction.
/// </summary>
internal sealed class ServiceRequestCreator(
    ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings, IAuditLog audit,
    ApprovalService approvals, SlaTracker sla, Zelvo.Search.ISearchIndexer indexer)
{
    /// <summary>A checked request, ready to save.</summary>
    internal sealed class Prepared
    {
        public required NewServiceRequest Input { get; init; }

        public PreparedFormResponse? Form { get; init; }

        public List<TicketAttachment> Documents { get; init; } = [];

        public List<PreparedPart> Parts { get; init; } = [];
    }

    /// <summary>A checked part of a bundle.</summary>
    internal sealed record PreparedPart(PublishedService Service, int Stage, bool Optional, PreparedFormResponse? Form, List<TicketAttachment> Documents);

    /// <summary>Checks everything; returns the problem to answer with, or what to save.</summary>
    public async Task<(Prepared? Request, IResult? Problem)> PrepareAsync(NewServiceRequest input, CancellationToken cancellationToken)
    {
        _requesterId = input.Requester.Id;
        _uploader = input.UploaderId ?? Guid.Empty;
        var card = input.Service.Card;
        if (Unavailable(input.Service) is { } unavailable)
        {
            return (null, unavailable);
        }

        var policy = await settings.GetAsync(IncidentSettings.Attachments, cancellationToken);
        if (card.Kind != ServiceKind.Bundle)
        {
            if (input.Parts is { Count: > 0 })
            {
                return (null, UserEndpoints.Validation("items", "Only bundles have parts."));
            }

            if (await PrerequisiteProblemAsync(card, [], cancellationToken) is { } missing)
            {
                return (null, missing);
            }

            var (form, formProblem) = await FormAsync(card, input.FormVersionId, input.Answers, policy, combined: false, null, cancellationToken);
            if (formProblem is not null)
            {
                return (null, formProblem);
            }

            var (documents, documentProblem) = await DocumentsAsync(card, input.Documents, policy, null, cancellationToken);
            return documentProblem is not null ? (null, documentProblem) : (new Prepared { Input = input, Form = form, Documents = documents }, null);
        }

        // Task C5: a bundle. Its services must be offered to the requester; optional ones only when chosen.
        if (input.Documents is { Count: > 0 })
        {
            return (null, UserEndpoints.Validation("documents", "Send each service's documents with its part."));
        }

        var given = input.Parts ?? [];
        var inBundle = card.Bundle.Items.Select(i => i.ServiceId).OfType<Guid>().ToHashSet();
        if (given.Any(p => p.ServiceId is not { } id || !inBundle.Contains(id)) || given.Select(p => p.ServiceId).Distinct().Count() != given.Count)
        {
            return (null, UserEndpoints.Validation("items", "Name each service of the bundle at most once."));
        }

        var scope = await CatalogScope.OfUserAsync(db, input.Requester, cancellationToken);
        var parts = new List<(PublishedService Service, BundleItem Item, BundlePartInput? Input, int Stage)>();
        var stage = -1;
        foreach (var item in card.Bundle.Items)
        {
            stage += item.ParallelWithPrevious && stage >= 0 ? 0 : 1;
            var chosen = given.FirstOrDefault(p => p.ServiceId == item.ServiceId);
            if (!item.Required && chosen is null)
            {
                continue;
            }

            var part = await PublishedServices.FindAsync(db, scope, item.ServiceId!.Value, cancellationToken);
            if (part is null || part.Card.Kind == ServiceKind.Bundle || Unavailable(part) is not null)
            {
                var code = await db.CatalogServices.AsNoTracking().Where(s => s.Id == item.ServiceId).Select(s => s.Code).SingleOrDefaultAsync(cancellationToken);
                return (null, TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "A service of the bundle cannot be requested now.",
                    type: ApiProblem.TypeFor("bundle-part-unavailable"),
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "bundle-part-unavailable", ["service"] = code }));
            }

            parts.Add((part, item, chosen, stage));
        }

        // The bundle's own prerequisites, then each part's; earlier parts of the bundle count as done.
        if (await PrerequisiteProblemAsync(card, [], cancellationToken) is { } bundleMissing)
        {
            return (null, bundleMissing);
        }

        foreach (var part in parts)
        {
            if (await PrerequisiteProblemAsync(part.Service.Card, inBundle, cancellationToken) is { } missing)
            {
                return (null, missing);
            }
        }

        // One combined form: every answer belongs to a field of at least one form.
        var forms = new List<ResolvedForm?>();
        var bundleForm = card.FormId is { } bf ? await FormResolver.PublishedAsync(db, bf, cancellationToken) : null;
        foreach (var part in parts)
        {
            forms.Add(part.Service.Card.FormId is { } pf ? await FormResolver.PublishedAsync(db, pf, cancellationToken) : null);
        }

        var keys = forms.Append(bundleForm).OfType<ResolvedForm>().SelectMany(f => f.PlainFields.Select(x => x.Key)).ToHashSet(StringComparer.Ordinal);
        var unknown = (input.Answers ?? []).Select(a => a.Key).Where(k => !keys.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            return (null, FormsCommon.AnswerErrors([.. unknown.Select(k => new FormAnswerError(k, "field-unknown", $"No form of the bundle has a field '{k}'."))]));
        }

        var (ownForm, ownProblem) = await FormAsync(card, input.FormVersionId, input.Answers, policy, combined: true, bundleForm, cancellationToken);
        if (ownProblem is not null)
        {
            return (null, ownProblem);
        }

        var prepared = new Prepared { Input = input, Form = ownForm };
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var (form, formProblem) = await FormAsync(part.Service.Card, part.Input?.FormVersionId, input.Answers, policy, combined: true, forms[i], cancellationToken);
            if (formProblem is not null)
            {
                return (null, formProblem);
            }

            var (documents, documentProblem) = await DocumentsAsync(part.Service.Card, part.Input?.Documents, policy, part.Service.Service.Code, cancellationToken);
            if (documentProblem is not null)
            {
                return (null, documentProblem);
            }

            prepared.Parts.Add(new PreparedPart(part.Service, part.Stage, !part.Item.Required, form, documents));
        }

        return (prepared, null);
    }

    /// <summary>
    /// Saves <paramref name="prepared"/> (inside the caller's transaction): the request, or the
    /// bundle and its parts; asks for approvals or releases what may start now; updates clocks and search.
    /// </summary>
    public async Task<Ticket> SaveAsync(Prepared prepared, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Service requests are saved inside a transaction.");
        }

        var input = prepared.Input;
        var now = DateTimeOffset.UtcNow;
        var language = LocalizationSettings.IsLanguage(input.Requester.PreferredLanguage)
            ? input.Requester.PreferredLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken);
        var title = string.IsNullOrWhiteSpace(input.Title) ? input.Service.Card.Name.GetOrFallback(language) ?? input.Service.Service.Code : input.Title.Trim();
        var isBundle = input.Service.Card.Kind == ServiceKind.Bundle;
        var main = await NewTicketAsync(input, title, prefixes, now, cancellationToken);
        db.Tickets.Add(main);
        db.ServiceRequests.Add(new ServiceRequestDetails
        {
            TicketId = main.Id, ServiceId = input.Service.Service.Id, ServiceVersionId = input.Service.Version.Id, IsBundle = isBundle, ProactiveRunId = input.ProactiveRunId,
        });
        prepared.Form?.Attach(db, main);
        foreach (var file in prepared.Documents)
        {
            file.TicketId = main.Id;
        }

        var saved = new List<Ticket> { main };
        if (!isBundle)
        {
            await StartAsync(main, input.Service.Card, now, cancellationToken);
        }
        else
        {
            foreach (var part in prepared.Parts)
            {
                var partTitle = part.Service.Card.Name.GetOrFallback(language) ?? part.Service.Service.Code;
                var child = await NewTicketAsync(input, partTitle, prefixes, now, cancellationToken);
                child.ParentId = main.Id;
                child.Description = null;
                child.CategoryId = part.Service.Card.CategoryIds.Count > 0 ? part.Service.Card.CategoryIds[0] : null;
                var held = part.Stage > 0;
                child.Held = held;
                db.Tickets.Add(child);
                db.ServiceRequests.Add(new ServiceRequestDetails
                {
                    TicketId = child.Id, ServiceId = part.Service.Service.Id, ServiceVersionId = part.Service.Version.Id,
                    BundleId = main.Id, BundleStage = part.Stage, BundleOptional = part.Optional, Hold = held ? ServiceRequestHold.Bundle : ServiceRequestHold.None,
                    ProactiveRunId = input.ProactiveRunId,
                });
                part.Form?.Attach(db, child);
                foreach (var file in part.Documents)
                {
                    file.TicketId = child.Id;
                }

                if (!held)
                {
                    await StartAsync(child, part.Service.Card, now, cancellationToken);
                }

                saved.Add(child);
            }

            main.State = TicketState.InProgress;
            audit.Add(AuditCategory.Change, "catalog.bundle.requested", "ticket", main.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["service"] = input.Service.Service.Code,
                ["parts"] = prepared.Parts.Count,
                ["optional"] = prepared.Parts.Count(p => p.Optional),
            });
        }

        await sla.UpdateAsync(saved, now, cancellationToken);
        await RequestSearch.IndexAsync(db, indexer, saved, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return main;
    }

    /// <summary>
    /// A request reaches the service desk (or its approvers): approvals before assignment (the
    /// service's and matching rules') hold it back from agents; otherwise it is released.
    /// </summary>
    public async Task StartAsync(Ticket ticket, ServiceCard card, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var facts = await RuleFacts.LoadAsync(db, ticket, cancellationToken);
        var levels = await ApprovalPlanner.PlanAsync(db, ticket, ApprovalStage.BeforeAssignment, card.Approvals.BeforeAssignment, facts, cancellationToken);
        if (levels.Count > 0)
        {
            await approvals.StartAsync(ticket, ApprovalStage.BeforeAssignment, levels, user.Account?.Id, null, cancellationToken);
        }
        else
        {
            await ServiceRequestRouting.ReleaseAsync(db, settings, ticket, card, now, cancellationToken);
        }
    }

    private async Task<Ticket> NewTicketAsync(NewServiceRequest input, string title, IReadOnlyDictionary<TicketType, string> prefixes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sequence = await TicketNumbers.NextAsync(db, tenant.TenantId!.Value, TicketType.ServiceRequest, cancellationToken);
        var card = input.Service.Card;
        return new Ticket
        {
            Type = TicketType.ServiceRequest,
            Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.ServiceRequest), sequence),
            Sequence = sequence,
            Title = title[..Math.Min(title.Length, Ticket.TitleMaxLength)],
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            Channel = input.Channel,
            RequesterId = input.Requester.Id,
            CreatedById = input.CreatedById,
            CreatedByApiClientId = input.ApiClientId,
            CategoryId = card.CategoryIds.Count > 0 ? card.CategoryIds[0] : null,
            LocationId = input.Requester.LocationId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static IResult? Unavailable(PublishedService found) =>
        found.Service.State == ServiceState.Suspended
            ? ApiProblem.Create(StatusCodes.Status409Conflict, "service-suspended", "The service is suspended and cannot be requested now.")
            : found.Card.Type == ServiceType.External
                ? ApiProblem.Create(StatusCodes.Status409Conflict, "service-external", "The service is requested on the provider's site.")
                : null;

    private async Task<IResult?> PrerequisiteProblemAsync(ServiceCard card, HashSet<Guid> satisfied, CancellationToken cancellationToken)
    {
        var wanted = card.PrerequisiteServiceIds.Where(p => !satisfied.Contains(p)).ToList();
        if (wanted.Count == 0)
        {
            return null;
        }

        var completed = await CatalogBrowseEndpoints.CompletedAsync(db, _requesterId, wanted, cancellationToken);
        var missing = wanted.Where(p => !completed.Contains(p)).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        var codes = await db.CatalogServices.AsNoTracking().Where(s => missing.Contains(s.Id)).Select(s => s.Code).ToListAsync(cancellationToken);
        return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Complete the prerequisite services first.",
            type: ApiProblem.TypeFor("prerequisite-missing"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "prerequisite-missing", ["services"] = codes });
    }

    private Guid _requesterId;

    private async Task<(PreparedFormResponse? Form, IResult? Problem)> FormAsync(
        ServiceCard card, Guid? formVersionId, JsonObject? answers, AttachmentPolicy policy, bool combined, ResolvedForm? resolved, CancellationToken cancellationToken)
    {
        var form = resolved ?? (card.FormId is { } formId ? await FormResolver.PublishedAsync(db, formId, cancellationToken) : null);
        if (card.FormId is not null && form is null)
        {
            return (null, ApiProblem.Create(StatusCodes.Status409Conflict, "service-unavailable", "The service's form is not available. Try again later."));
        }

        if (!combined)
        {
            return await FormSubmission.PrepareAsync(db, user, audit, form, _requesterId, formVersionId, answers, policy, cancellationToken);
        }

        // A bundle's combined answers (task C5): each form takes those of its own fields.
        if (form is null)
        {
            return (null, null);
        }

        var own = new JsonObject(form.PlainFields.Where(f => answers?.ContainsKey(f.Key) == true)
            .Select(f => KeyValuePair.Create(f.Key, answers![f.Key]?.DeepClone())));
        return await FormSubmission.PrepareAsync(db, user, audit, form, _requesterId, formVersionId, own, policy, cancellationToken);
    }

    private async Task<(List<TicketAttachment> Files, IResult? Problem)> DocumentsAsync(
        ServiceCard card, IReadOnlyDictionary<string, List<Guid>>? sent, AttachmentPolicy policy, string? part, CancellationToken cancellationToken)
    {
        if (user.Account is null && sent is { Count: > 0 })
        {
            return ([], UserEndpoints.Validation("documents", "AI agents cannot send documents; the requester adds them on the portal."));
        }

        var (files, errors) = await ServiceRequestEndpoints.CheckDocumentsAsync(db, _uploader, card, sent, policy, cancellationToken);
        if (errors.Count == 0)
        {
            return (files, null);
        }

        if (part is not null)
        {
            errors = [.. errors.Select(e => e with { Field = $"items.{part}.{e.Field}" })];
        }

        return ([], TypedResults.ValidationProblem(
            errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal),
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["documentErrors"] = errors }));
    }

    private Guid _uploader;
}
