using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Api.Ai;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Changes;

/// <summary>
/// The what-if simulation of a change (module 17, task H5): before approval, the blast radius from
/// the CMDB, similar past changes with their review outcomes and the predicted risk beside the
/// questionnaire's level; optionally explained by the AI (E4 gateway, masked), which only explains.
/// Shown to the requester, approvers and the board's agenda; kept with the change when it is approved.
/// </summary>
internal static class ChangeWhatIfEndpoints
{
    private const string Instructions = """
        Task: changeWhatIf.
        You explain a planned change's what-if simulation to the people who approve it at a service desk. The data blocks hold the change, its blast radius (what depends on the items it touches and who uses them), similar past changes with how they went, and a predicted risk worked out from past outcomes. Explain in plain language what the figures mean and what to look at before deciding: which services and how many people could be affected, what went wrong in similar changes, and why the predicted risk is what it is compared with the questionnaire's level. Do not recommend approving or rejecting, and do not invent facts beyond the data. Write at most 150 words of plain text, without headings or lists, in the language named in the "language" block (Modern Standard Arabic for Arabic).
        """;

    public static RouteGroupBuilder MapChangeWhatIfEndpoints(this RouteGroupBuilder group)
    {
        var whatIf = group.MapGroup("/changes/{id:guid}/what-if").WithTags("Changes").RequireTenant();
        whatIf.MapGet("/", GetAsync).WithName("GetChangeWhatIf")
            .WithSummary("The what-if simulation: the blast radius (affected items, business services, the users, departments and customer groups that use them, people with an affected asset), similar past changes with their review outcomes, and the predicted risk from past outcomes on the same items, classes and team beside the questionnaire's level. Worked out live; kept with the change once approved (kept=true). Holders of changes.read and the change's approvers.")
            .RequireAuthorization().Produces<WhatIfResponse>();
        whatIf.MapPost("/explain", ExplainAsync).WithName("ExplainChangeWhatIf")
            .WithSummary("Ask the AI (feature changeWhatIf, off until set up) to explain the simulation in English or Arabic; personal data is masked and the AI never decides. The explanation is kept with the change. 409 ai-off or limit-reached; 502 when the provider fails. Holders of changes.read and the change's approvers; audited.")
            .RequirePerson().RequireAuthorization().Produces<WhatIfResponse>().ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status502BadGateway);
        group.MapGet("/changes/what-if/availability", AvailabilityAsync).WithName("GetChangeWhatIfAvailability").WithTags("Changes").RequireTenant()
            .WithSummary("Whether the AI can explain simulations now (the feature changeWhatIf is on and the monthly limit not reached). Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<WhatIfAvailability>();
        return group;
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await VisibleAsync(db, user, id, cancellationToken);
        return ticket is null ? ChangeCore.NotFound() : TypedResults.Ok(await ChangeSimulator.CurrentAsync(db, ticket, cancellationToken));
    }

    internal static async Task<IResult> AvailabilityAsync(IAiGateway gateway, CancellationToken cancellationToken)
    {
        var availability = (await gateway.AvailabilityAsync(cancellationToken)).FirstOrDefault(a => a.Feature == AiFeatures.ChangeWhatIf);
        return TypedResults.Ok(new WhatIfAvailability(availability?.Available ?? false, availability?.Reason ?? "ai-off"));
    }

    internal static async Task<IResult> ExplainAsync(
        Guid id, ExplainWhatIfRequest? request, ZelvoDbContext db, CurrentUser user, IAiGateway gateway, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await VisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return ChangeCore.NotFound();
        }

        var language = request?.Language is LocalizedText.Arabic or LocalizedText.English ? request.Language
            : user.Account?.PreferredLanguage is LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English;
        var simulation = await ChangeSimulator.CurrentAsync(db, ticket, cancellationToken);
        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.ChangeWhatIf,
            Instructions = Instructions,
            Inputs = [.. Inputs(ticket, simulation, language)],
            MaxOutputTokens = 600,
            EntityType = "change",
            EntityId = ticket.Id.ToString(),
        }, cancellationToken);
        if (!result.Succeeded)
        {
            return AiFeatureEndpoints.AiProblem(result.ErrorCode, result.Error);
        }

        var tracked = await db.Changes.SingleAsync(c => c.TicketId == id, cancellationToken);
        var text = result.Text!.Trim();
        tracked.WhatIfExplanation = text.Length <= Data.Changes.ChangeRecord.PlanMaxLength ? text : text[..Data.Changes.ChangeRecord.PlanMaxLength];
        tracked.WhatIfExplanationLanguage = language;
        tracked.WhatIfExplainedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Ai, "changes.what-if.explained", "ticket", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["aiCallId"] = result.CallId, ["language"] = language });
        await db.SaveChangesAsync(cancellationToken);
        var saved = await ChangeCore.Changes(db).AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken);
        return TypedResults.Ok(await ChangeSimulator.CurrentAsync(db, saved, cancellationToken));
    }

    /// <summary>The change, for holders of changes.read and for the people asked to approve it.</summary>
    private static async Task<Ticket?> VisibleAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await ChangeCore.Changes(db).AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket?.Change is null)
        {
            return null;
        }

        var me = user.Account?.Id;
        return user.Has(Permissions.ChangesRead) || (me is not null && await db.ApprovalDecisions.AnyAsync(d => d.TicketId == id && d.ApproverId == me, cancellationToken))
            ? ticket
            : null;
    }

    /// <summary>The simulation as data blocks: figures and names of services, departments and changes, never people's names.</summary>
    private static IEnumerable<AiInput> Inputs(Ticket ticket, WhatIfResponse simulation, string language)
    {
        var change = ticket.Change!;
        var c = CultureInfo.InvariantCulture;
        yield return new AiInput("language", language == LocalizedText.Arabic ? "Arabic" : "English");
        yield return new AiInput("change", string.Create(c,
            $"{ticket.Number}: {ticket.Title}\nType: {change.Kind}\nQuestionnaire risk: {change.RiskLevel?.ToString() ?? "not assessed"} (score {change.RiskScore?.ToString(c) ?? "-"})\nWindow: {change.PlannedStart:u} to {change.PlannedEnd:u}"));
        var blast = simulation.Blast;
        var services = string.Join(", ", blast.Impact.BusinessServices.Select(s => s.Node.Name).Distinct());
        var departments = string.Join(", ", blast.Impact.Consumers.Where(x => x.Node.Kind == Cmdb.CiNodeKind.Department).Select(x => x.Node.LocalizedName?.En ?? x.Node.Name).Distinct());
        yield return new AiInput("blast", string.Create(c,
            $"Items touched: {string.Join(", ", blast.Items.Select(i => i.Name))}\nAffected items: {blast.Impact.Items.Count}\nBusiness services: {(services.Length == 0 ? "none" : services)}\nDepartments using them: {(departments.Length == 0 ? "none" : departments)}\nPeople affected: {blast.PeopleCount}\nPeople with an affected asset: {blast.AssetUsers}"));
        var similar = new StringBuilder();
        foreach (var s in simulation.Similar)
        {
            similar.Append(c, $"{s.Number}: {s.Title} | outcome {s.Outcome} | similar by {string.Join(", ", s.Reasons)}");
            if (s.ReviewNotes is { Length: > 0 } notes)
            {
                similar.Append(" | review: ").Append(notes.Length > 300 ? notes[..300] : notes);
            }

            similar.Append('\n');
        }

        yield return new AiInput("similar", similar.Length == 0 ? "No similar past changes." : similar.ToString());
        var p = simulation.Predicted;
        yield return new AiInput("predicted", string.Create(c,
            $"Predicted risk: {p.Level} ({p.FailureChance}% chance of failing or being backed out; usual rate {p.BaseRate}%)\nHistory found: {p.History}\n{string.Join('\n', p.Reasons.Select(r => $"{r.Cohort}: {r.Changes} changes, {r.Failed} failed or backed out, {r.WithProblems} with problems, rate {r.FailureRate}%"))}"));
    }
}

/// <summary>Whether the AI can explain simulations.</summary>
internal sealed record WhatIfAvailability(bool Available, string? Reason);

/// <summary>Which language to explain in.</summary>
public sealed class ExplainWhatIfRequest
{
    /// <summary><c>en</c> or <c>ar</c>; the caller's language when not given.</summary>
    [StringLength(2)]
    public string? Language { get; init; }
}
