using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Api.Workspace;

/// <summary>
/// Bulk actions of the agent workspace (module 5, task B4; decision 2026-11-24): up to 100
/// incidents at once. Each incident is changed by the same code as a single change, with the same
/// permissions, rules and audit entries; an incident that fails is left as it was and the others
/// are saved, all in one transaction. The answer has a result per incident.
/// </summary>
internal static class BulkIncidentEndpoints
{
    /// <summary>Most incidents in one bulk action.</summary>
    internal const int MaxTickets = 100;

    public static RouteGroupBuilder MapBulkIncidentEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/incidents/bulk", BulkAsync)
            .WithTags("Agent workspace")
            .RequireTenant()
            .WithName("BulkChangeIncidents")
            // Each change records the person who acts.
            .RequirePerson()
            .WithSummary("Apply one action to up to 100 incidents: assign (assigneeId), assignment (groupId and assigneeId), route (run the assignment rules), transition (state, onHoldReason, resolutionNotes), priority (impact and urgency), category (categoryId), location (locationId) or reply (a public reply or internal note to each). Each incident is checked like a single change; those that fail stay as they were. Needs incidents.edit, and incidents.assign for assign, assignment and route.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<BulkResponse>()
            .ProducesValidationProblem();
        return group;
    }

    internal static async Task<Results<Ok<BulkResponse>, ValidationProblem, ProblemHttpResult>> BulkAsync(
        BulkIncidentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ids = (request.Ids ?? []).Distinct().ToList();
        if (ids.Count is 0 or > MaxTickets)
        {
            return UserEndpoints.Validation("ids", $"Choose 1 to {MaxTickets} incidents.");
        }

        var action = request.Action!.Value;
        if (action is BulkAction.Assign or BulkAction.Assignment or BulkAction.Route && !user.Has(Permissions.IncidentsAssign))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You may not assign incidents.");
        }

        if (Missing(request) is { } missing)
        {
            return missing;
        }

        var services = http.RequestServices;
        var settings = services.GetRequiredService<TenantSettingsStore>();
        var indexer = services.GetRequiredService<ISearchIndexer>();
        var results = new List<BulkItemResult>(ids.Count);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids)
        {
            await transaction.CreateSavepointAsync("bulk_item", cancellationToken);
            IResult outcome;
            try
            {
                outcome = await ApplyAsync(action, id, request, http, db, user, settings, indexer, cancellationToken);
            }
            catch (DbUpdateException)
            {
                outcome = ApiProblem.Create(StatusCodes.Status409Conflict, "conflict", "The incident could not be saved; try again.");
            }

            var item = BulkItemResult.From(id, outcome);
            if (item.Ok)
            {
                await transaction.ReleaseSavepointAsync("bulk_item", cancellationToken);
            }
            else
            {
                await transaction.RollbackToSavepointAsync("bulk_item", cancellationToken);
            }

            // Nothing of one incident carries over to the next.
            db.ChangeTracker.Clear();
            results.Add(item);
        }

        await transaction.CommitAsync(cancellationToken);
        http.Response.Headers.ETag = default;
        return TypedResults.Ok(new BulkResponse(results.Count(r => r.Ok), results.Count(r => !r.Ok), results));
    }

    private static async Task<IResult> ApplyAsync(
        BulkAction action, Guid id, BulkIncidentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case BulkAction.Assign:
                return (await IncidentEndpoints.AssignAsync(id, new AssignIncidentRequest { AssigneeId = request.AssigneeId }, http, db, user, indexer, cancellationToken)).Result;
            case BulkAction.Assignment:
                return (await IncidentEndpoints.SetAssignmentAsync(id, new SetAssignmentRequest { GroupId = request.GroupId, AssigneeId = request.AssigneeId }, http, db, user, indexer, cancellationToken)).Result;
            case BulkAction.Route:
                return (await IncidentEndpoints.RouteAgainAsync(id, http, db, user, settings, indexer, cancellationToken)).Result;
            case BulkAction.Transition:
                var transition = new ChangeIncidentStateRequest { State = request.State, OnHoldReason = request.OnHoldReason, ResolutionNotes = request.ResolutionNotes };
                return (await IncidentEndpoints.TransitionAsync(id, transition, http, db, user, settings, indexer, cancellationToken)).Result;
            case BulkAction.Priority:
                return (await IncidentEndpoints.UpdateAsync(id, new UpdateIncidentRequest { Impact = request.Impact, Urgency = request.Urgency }, http, db, user, settings, indexer, cancellationToken)).Result;
            case BulkAction.Category:
            case BulkAction.Location:
                // The other of the two stays as it is on each incident.
                var current = await db.Tickets.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.CategoryId, t.LocationId }).SingleOrDefaultAsync(cancellationToken);
                if (current is null)
                {
                    return IncidentEndpoints.NotFound();
                }

                var classification = action == BulkAction.Category
                    ? new SetClassificationRequest { CategoryId = request.CategoryId, LocationId = current.LocationId }
                    : new SetClassificationRequest { CategoryId = current.CategoryId, LocationId = request.LocationId };
                return (await IncidentEndpoints.SetClassificationAsync(id, classification, http, db, user, indexer, cancellationToken)).Result;
            default:
                var comment = new CreateCommentRequest
                {
                    Body = request.Body,
                    Visibility = request.Visibility,
                    Language = request.Language,
                    CannedResponseId = request.CannedResponseId,
                };
                return (await CommentEndpoints.CreateAsync(id, comment, http, db, user, settings, cancellationToken)).Result;
        }
    }

    /// <summary>What each action needs, checked once before any incident changes.</summary>
    private static ValidationProblem? Missing(BulkIncidentRequest request) => request.Action switch
    {
        BulkAction.Transition when request.State is null => UserEndpoints.Validation("state", "Choose the new state."),
        BulkAction.Priority when request.Impact is null && request.Urgency is null => UserEndpoints.Validation("impact", "Choose the impact, the urgency or both."),
        BulkAction.Reply when string.IsNullOrWhiteSpace(request.Body) => UserEndpoints.Validation("body", "Write the reply or note."),
        _ => null,
    };
}

/// <summary>Bulk actions on incidents.</summary>
public enum BulkAction
{
    /// <summary>Assign to an agent (or nobody); the agent must be in each incident's group.</summary>
    Assign,

    /// <summary>Set the group and the agent together.</summary>
    Assignment,

    /// <summary>Run the assignment rules again.</summary>
    Route,

    /// <summary>Move to another state.</summary>
    Transition,

    /// <summary>Set impact and/or urgency; the priority follows the matrix.</summary>
    Priority,

    /// <summary>Set the category (the location stays).</summary>
    Category,

    /// <summary>Set the location (the category stays).</summary>
    Location,

    /// <summary>Post the same public reply or internal note on each.</summary>
    Reply,
}

/// <summary>One action for many incidents.</summary>
public sealed class BulkIncidentRequest
{
    /// <summary>The incidents, 1 to 100.</summary>
    [Required]
    public List<Guid>? Ids { get; init; }

    /// <summary>What to do.</summary>
    [Required]
    public BulkAction? Action { get; init; }

    /// <summary>assign, assignment: the agent, or null for nobody.</summary>
    public Guid? AssigneeId { get; init; }

    /// <summary>assignment: the group, or null for none.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>transition: the new state.</summary>
    public TicketState? State { get; init; }

    /// <summary>transition to OnHold: an active on-hold reason.</summary>
    [StringLength(Ticket.ReasonKeyMaxLength)]
    public string? OnHoldReason { get; init; }

    /// <summary>transition to Resolved: how they were resolved.</summary>
    [StringLength(Ticket.ResolutionNotesMaxLength)]
    public string? ResolutionNotes { get; init; }

    /// <summary>priority: impact level.</summary>
    public int? Impact { get; init; }

    /// <summary>priority: urgency level.</summary>
    public int? Urgency { get; init; }

    /// <summary>category: the category, or null for none.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>location: the location, or null for none.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>reply: the text.</summary>
    [StringLength(TicketComment.BodyMaxLength)]
    public string? Body { get; init; }

    /// <summary>reply: Public (default) or Internal.</summary>
    public CommentVisibility? Visibility { get; init; }

    /// <summary>reply: language of the text.</summary>
    [AllowedValues(null, "en", "ar")]
    public string? Language { get; init; }

    /// <summary>reply: the canned response it started from.</summary>
    public Guid? CannedResponseId { get; init; }
}

/// <summary>What happened to each incident.</summary>
/// <param name="Succeeded">How many were changed.</param>
/// <param name="Failed">How many were left as they were.</param>
/// <param name="Results">One per incident, in the order asked.</param>
internal sealed record BulkResponse(int Succeeded, int Failed, IReadOnlyList<BulkItemResult> Results);

/// <summary>The result for one incident of a bulk action.</summary>
/// <param name="Id">The incident.</param>
/// <param name="Ok">Changed.</param>
/// <param name="Status">HTTP status the single change would have answered.</param>
/// <param name="Code">Problem code when it failed, for example <c>transition-not-allowed</c>.</param>
/// <param name="Title">What went wrong.</param>
/// <param name="Errors">Validation errors by field.</param>
internal sealed record BulkItemResult(Guid Id, bool Ok, int Status, string? Code, string? Title, IDictionary<string, string[]>? Errors)
{
    public static BulkItemResult From(Guid id, IResult outcome)
    {
        switch (outcome)
        {
            case ValidationProblem validation:
                return new(id, false, StatusCodes.Status400BadRequest, "validation-failed", validation.ProblemDetails.Title, validation.ProblemDetails.Errors);
            case ProblemHttpResult problem:
                var details = problem.ProblemDetails;
                return new(id, false, details.Status ?? StatusCodes.Status400BadRequest, CodeOf(details), details.Title, null);
            case IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } success:
                return new(id, true, success.StatusCode!.Value, null, null, null);
            default:
                return new(id, false, StatusCodes.Status500InternalServerError, "unexpected", null, null);
        }
    }

    private static string? CodeOf(ProblemDetails details) =>
        details.Extensions.TryGetValue("code", out var code) ? code?.ToString() : null;
}
