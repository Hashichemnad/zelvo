using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Directory;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Notifications;
using Zelvo.Tickets;

namespace Zelvo.Workflows.Rules;

/// <summary>
/// Runs workflow rules with every save (task C3): finds tickets that were created, changed state
/// or changed assignment (and approval processes that ended), tries the active rules of those
/// triggers in order, and applies the actions of each rule whose conditions hold, in the same
/// transaction: assign changes the ticket before it is saved; notify queues notifications for the
/// notification interceptor (registered after this one). Approval actions are read when a request
/// is submitted or resolved (<c>ApprovalPlanner</c>). Each save runs the rules once: changes made by
/// rules do not trigger rules again. Every rule that ran is audited.
/// </summary>
public sealed class WorkflowRuleInterceptor(ITenantContext tenantContext, PendingNotifications pending, IServiceProvider services)
    : SaveChangesInterceptor, IZelvoSaveChangesInterceptor
{
    private bool _running;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        RunAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await RunAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task RunAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (_running || context is not ZelvoDbContext db || tenantContext.TenantId is null)
        {
            return;
        }

        _running = true;
        // Changes are detected once here; looking up tracked entries for each ticket below must not
        // detect them again every time (with many tickets in one save that grew quadratically).
        // SaveChanges detects the rules' own changes after the interceptors ran.
        var autoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            db.ChangeTracker.DetectChanges();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var work = Collect(db);
            if (work.Count == 0)
            {
                return;
            }

            var triggers = work.Select(w => w.Trigger).Distinct().ToList();
            // Task K1: drafts (from plain-language rules) never run until an administrator approves them.
            var rules = await db.WorkflowRules.AsNoTracking().Where(r => r.Active && !r.IsDraft && triggers.Contains(r.Trigger))
                .OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (rules.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var lookups = new RuleFactLookups();
            foreach (var (ticket, trigger) in work)
            {
                var candidates = rules.Where(r => r.Trigger == trigger && (r.RecordType is null || r.RecordType == ticket.Type)
                    && (r.CustomRecordTypeId is null || r.CustomRecordTypeId == ticket.RecordTypeId)).ToList();
                if (candidates.Count == 0)
                {
                    continue;
                }

                var facts = await RuleFacts.LoadAsync(db, ticket, lookups, cancellationToken).ConfigureAwait(false);
                foreach (var rule in candidates)
                {
                    if (!RuleDocuments.Holds(RuleDocuments.ReadConditions(rule.Conditions), rule.Match, facts.Subject))
                    {
                        continue;
                    }

                    var applied = new List<string>();
                    foreach (var action in RuleDocuments.ReadActions(rule.Actions))
                    {
                        switch (action)
                        {
                            case AssignAction assign when await AssignAsync(db, ticket, assign, now, cancellationToken).ConfigureAwait(false):
                                applied.Add("assign");
                                break;
                            case NotifyAction notify:
                                await NotifyAsync(db, ticket, facts, notify, cancellationToken).ConfigureAwait(false);
                                applied.Add("notify");
                                break;
                            case ConnectorAction { ConnectorId: { } connectorId, Operation: { Length: > 0 } operation }:
                                // Task E7: the call goes out from the worker after the save, with retries.
                                db.ConnectorCalls.Add(new Data.Connectors.ConnectorCall
                                {
                                    TenantId = ticket.TenantId, ConnectorId = connectorId, Kind = Data.Connectors.ConnectorCallKind.Rest, Operation = operation,
                                    TicketId = ticket.Id, RuleId = rule.Id, CreatedAt = now, NextAttemptAt = now,
                                });
                                applied.Add("connector");
                                break;
                        }
                    }

                    // Resolved here: the audit log needs the database context this interceptor belongs to.
                    var audit = (IAuditLog)services.GetService(typeof(IAuditLog))!;
                    audit.Add(AuditCategory.Change, "workflows.rule.applied", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ruleId"] = rule.Id.ToString(),
                        ["trigger"] = trigger.ToString(),
                        ["actions"] = applied,
                    });

                    if (rule.StopProcessing)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
            _running = false;
        }
    }

    /// <summary>Tickets and the triggers their changes in this save fire.</summary>
    private static List<(Ticket Ticket, WorkflowTrigger Trigger)> Collect(ZelvoDbContext db)
    {
        var work = new List<(Ticket, WorkflowTrigger)>();
        foreach (var entry in db.ChangeTracker.Entries<Ticket>().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                work.Add((entry.Entity, WorkflowTrigger.TicketCreated));
                continue;
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            if (Changed(entry, t => t.State))
            {
                work.Add((entry.Entity, WorkflowTrigger.StateChanged));
            }

            if (Changed(entry, t => t.GroupId) || Changed(entry, t => t.AssigneeId))
            {
                work.Add((entry.Entity, WorkflowTrigger.AssignmentChanged));
            }
        }

        foreach (var process in db.ChangeTracker.Entries<ApprovalProcess>()
            .Where(e => e.State == EntityState.Modified && e.Property(p => p.State).IsModified
                && e.Property(p => p.State).OriginalValue == ApprovalProcessState.Pending
                && e.Entity.State is ApprovalProcessState.Approved or ApprovalProcessState.Rejected)
            .Select(e => e.Entity))
        {
            if (db.ChangeTracker.Entries<Ticket>().FirstOrDefault(e => e.Entity.Id == process.TicketId)?.Entity is { } ticket)
            {
                work.Add((ticket, WorkflowTrigger.ApprovalDecided));
            }
        }

        return work;
    }

    private static bool Changed<TValue>(EntityEntry<Ticket> entry, System.Linq.Expressions.Expression<Func<Ticket, TValue>> property)
    {
        var p = entry.Property(property);
        return p.IsModified && !EqualityComparer<TValue>.Default.Equals(p.OriginalValue, p.CurrentValue);
    }

    /// <summary>Applies an assign action; nothing while the ticket waits for approval or is resolved or closed.</summary>
    private static async Task<bool> AssignAsync(ZelvoDbContext db, Ticket ticket, AssignAction action, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ticket.AwaitingApproval || ticket.Held || !TicketWorkflow.IsOpen(ticket.State) || action.GroupId is not { } groupId
            || !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Task C5: bundles are containers; their parts are routed.
        if (ticket.Type == TicketType.ServiceRequest
            && (db.ChangeTracker.Entries<Data.Catalog.ServiceRequestDetails>().Any(e => e.Entity.TicketId == ticket.Id && e.Entity.IsBundle)
                || await db.ServiceRequests.AnyAsync(r => r.TicketId == ticket.Id && r.IsBundle, cancellationToken).ConfigureAwait(false)))
        {
            return false;
        }

        Guid? assignee = null;
        if (action.AssigneeId is { } member)
        {
            if (!await db.SupportGroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == member && m.User!.Status == UserStatus.Active, cancellationToken)
                .ConfigureAwait(false))
            {
                return false;
            }

            assignee = member;
        }
        else if (action.Method == AssignmentMethod.RoundRobin)
        {
            assignee = await TicketRouter.RoundRobinAsync(db, groupId, null, null, now, commit: true, cancellationToken).ConfigureAwait(false);
        }

        ticket.GroupId = groupId;
        ticket.RoutingRuleId = null;
        if (ticket.AssigneeId != assignee)
        {
            TicketWorkflow.Assign(ticket, assignee, now);
        }

        ticket.UpdatedAt = now;
        return true;
    }

    private async Task NotifyAsync(ZelvoDbContext db, Ticket ticket, RuleFacts facts, NotifyAction action, CancellationToken cancellationToken)
    {
        var requesterSide = new HashSet<Guid>();
        var staffSide = new HashSet<Guid>();
        foreach (var recipient in action.Recipients)
        {
            switch (recipient)
            {
                case "requester":
                    requesterSide.Add(ticket.RequesterId);
                    break;
                case "requesterManager" when facts.Requester?.ManagerId is { } manager:
                    staffSide.Add(manager);
                    break;
                case "assignee" when ticket.AssigneeId is { } assignee:
                    staffSide.Add(assignee);
                    break;
                case "group" when ticket.GroupId is { } group:
                    staffSide.UnionWith(await MembersAsync(db, group, false, cancellationToken).ConfigureAwait(false));
                    break;
                case "groupLeads" when ticket.GroupId is { } group:
                    staffSide.UnionWith(await MembersAsync(db, group, true, cancellationToken).ConfigureAwait(false));
                    break;
                case { } r when r.StartsWith("user:", StringComparison.Ordinal) && Guid.TryParse(r[5..], out var user):
                    if (user == ticket.RequesterId)
                    {
                        requesterSide.Add(user);
                    }
                    else
                    {
                        staffSide.Add(user);
                    }

                    break;
                case { } r when r.StartsWith("group:", StringComparison.Ordinal) && Guid.TryParse(r[6..], out var other):
                    staffSide.UnionWith(await MembersAsync(db, other, false, cancellationToken).ConfigureAwait(false));
                    break;
                case { } r when r.StartsWith("groupLeads:", StringComparison.Ordinal) && Guid.TryParse(r[11..], out var led):
                    staffSide.UnionWith(await MembersAsync(db, led, true, cancellationToken).ConfigureAwait(false));
                    break;
                case { } r when r.StartsWith("role:", StringComparison.Ordinal):
                    var key = r[5..];
                    staffSide.UnionWith(await db.Users.AsNoTracking()
                        .Where(u => u.Status == UserStatus.Active && u.Roles.Any(x => x.Role!.Key == key))
                        .Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        staffSide.ExceptWith(requesterSide);
        if (requesterSide.Count + staffSide.Count == 0)
        {
            return;
        }

        var groupName = ticket.GroupId is { } g
            ? await db.SupportGroups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
        {
            [Placeholders.RuleSubject] = PlaceholderValue.OfText(action.Subject),
            [Placeholders.RuleBody] = PlaceholderValue.OfText(action.Body),
            [Placeholders.RuleEmail] = PlaceholderValue.OfText(action.Email ? "true" : "false"),
            [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
            [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
            [Placeholders.TicketState] = PlaceholderValue.OfTerm($"ticket.state.{ticket.State}"),
            [Placeholders.TicketGroup] = groupName is null ? PlaceholderValue.OfText(string.Empty) : PlaceholderValue.OfText(groupName),
            [Placeholders.RequesterName] = PlaceholderValue.OfText(facts.Requester?.DisplayName ?? facts.Requester?.Email),
            [Placeholders.ServiceName] = facts.ServiceName is null ? PlaceholderValue.OfText(string.Empty) : PlaceholderValue.OfText(facts.ServiceName),
        };
        if (action.Channels is { } channels)
        {
            // Task K1: the rule names its messaging channels (none: in the app and by email only).
            values[Placeholders.RuleChannels] = PlaceholderValue.OfText(string.Join(',', channels.Distinct()));
        }

        var request = ticket.Type == TicketType.ServiceRequest;
        if (requesterSide.Count > 0)
        {
            pending.Add(new(NotificationEvents.WorkflowRule, [.. requesterSide], values,
                request ? RequestLookups.RequesterPath(ticket.Id) : $"/my-incidents/{ticket.Id}", TicketId: ticket.Id));
        }

        if (staffSide.Count > 0)
        {
            pending.Add(new(NotificationEvents.WorkflowRule, [.. staffSide], values,
                request ? RequestLookups.StaffPath(ticket.Id) : $"/incidents/{ticket.Id}", TicketId: ticket.Id));
        }
    }

    private static Task<List<Guid>> MembersAsync(ZelvoDbContext db, Guid groupId, bool leadsOnly, CancellationToken cancellationToken) =>
        db.SupportGroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && (!leadsOnly || m.IsLead) && m.User!.Status == UserStatus.Active)
            .Select(m => m.UserId).ToListAsync(cancellationToken);
}

/// <summary>What rules look at for one ticket, read from the save's tracked entities first, then the database.</summary>
/// <param name="Subject">The condition subject.</param>
/// <param name="Requester">The requester.</param>
/// <param name="ServiceName">The requested service's name.</param>
/// <param name="ServiceId">The requested service.</param>
public sealed record RuleFacts(RuleSubject Subject, UserAccount? Requester, Data.Localization.LocalizedText? ServiceName, Guid? ServiceId)
{
    /// <summary>Loads the facts of <paramref name="ticket"/>.</summary>
    public static Task<RuleFacts> LoadAsync(ZelvoDbContext db, Ticket ticket, CancellationToken cancellationToken) =>
        LoadAsync(db, ticket, new RuleFactLookups(), cancellationToken);

    /// <summary>Loads the facts of <paramref name="ticket"/>, reusing what <paramref name="lookups"/> already read in this save.</summary>
    public static async Task<RuleFacts> LoadAsync(ZelvoDbContext db, Ticket ticket, RuleFactLookups lookups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(lookups);
        var requester = db.ChangeTracker.Entries<UserAccount>().FirstOrDefault(e => e.Entity.Id == ticket.RequesterId)?.Entity
            ?? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ticket.RequesterId, cancellationToken).ConfigureAwait(false);
        var categories = await lookups.AncestryAsync(db.Categories, ticket.CategoryId, cancellationToken).ConfigureAwait(false);
        var locations = await lookups.AncestryAsync(db.Locations, ticket.LocationId, cancellationToken).ConfigureAwait(false);
        var priority = db.ChangeTracker.Entries<Incident>().FirstOrDefault(e => e.Entity.TicketId == ticket.Id)?.Entity.Priority
            ?? (ticket.Type == TicketType.Incident
                ? await db.Incidents.AsNoTracking().Where(i => i.TicketId == ticket.Id).Select(i => (int?)i.Priority).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : null)
            // Tasks H1 and K2: problems and custom records carry their own priority.
            ?? ticket.Custom?.Priority ?? ticket.Problem?.Priority;

        Guid? serviceId = null;
        decimal? fee = null;
        Data.Localization.LocalizedText? serviceName = null;
        if (ticket.Type == TicketType.ServiceRequest)
        {
            var details = db.ChangeTracker.Entries<ServiceRequestDetails>().FirstOrDefault(e => e.Entity.TicketId == ticket.Id)?.Entity
                ?? await db.ServiceRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TicketId == ticket.Id, cancellationToken).ConfigureAwait(false);
            if (details is not null)
            {
                serviceId = details.ServiceId;
                var content = await db.CatalogServiceVersions.AsNoTracking().Where(v => v.Id == details.ServiceVersionId).Select(v => v.Content)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                fee = FeeOf(content);
                serviceName = await db.CatalogServices.AsNoTracking().Where(s => s.Id == details.ServiceId).Select(s => s.Name)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var response = db.ChangeTracker.Entries<TicketFormResponse>().FirstOrDefault(e => e.Entity.TicketId == ticket.Id)?.Entity
            ?? await db.TicketFormResponses.AsNoTracking().FirstOrDefaultAsync(r => r.TicketId == ticket.Id, cancellationToken).ConfigureAwait(false);
        var answers = new JsonObject();
        foreach (var json in new[] { response?.Answers, response?.PersonalAnswers })
        {
            if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonObject part)
            {
                foreach (var (key, value) in part)
                {
                    answers[key] = value?.DeepClone();
                }
            }
        }

        var language = requester?.PreferredLanguage is Data.Localization.LocalizedText.English or Data.Localization.LocalizedText.Arabic ? requester.PreferredLanguage : null;
        var customerGroups = requester is null ? [] : lookups.CustomerGroups.TryGetValue(requester.Id, out var known) ? known
            : lookups.CustomerGroups[requester.Id] = await CustomerGroupsAsync(db, requester, cancellationToken).ConfigureAwait(false);
        var subject = new RuleSubject(ticket.Type, ticket.State, ticket.Channel, serviceId, fee, categories, locations, priority, ticket.GroupId,
            requester?.DepartmentId, language, answers, customerGroups);
        return new RuleFacts(subject, requester, serviceName, serviceId);
    }

    /// <summary>Task K1: the active customer groups (B3) the requester is in, by hand or by email domain.</summary>
    private static async Task<IReadOnlyCollection<Guid>> CustomerGroupsAsync(ZelvoDbContext db, UserAccount? requester, CancellationToken cancellationToken)
    {
        if (requester is null)
        {
            return [];
        }

        var groups = await db.CustomerGroups.AsNoTracking().Where(g => g.Active).Include(g => g.Members.Where(m => m.UserId == requester.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. groups.Where(g => g.Members.Count > 0 || Tickets.Sla.SlaMatcher.InDomain(g, requester.Email)).Select(g => g.Id)];
    }

    /// <summary>A card's fee in AED: its amount when paid, else 0.</summary>
    public static decimal FeeOf(string? cardJson)
    {
        if (string.IsNullOrWhiteSpace(cardJson) || JsonNode.Parse(cardJson) is not JsonObject card || card["fee"] is not JsonObject fee)
        {
            return 0m;
        }

        return fee["paid"]?.GetValue<bool>() == true && fee["amountAed"] is JsonValue amount && amount.TryGetValue<decimal>(out var aed) ? aed : 0m;
    }
}

/// <summary>
/// What loading rule facts read that is the same for many tickets of one save: the ancestry of a
/// category or location and a requester's customer groups.
/// </summary>
public sealed class RuleFactLookups
{
    private readonly Dictionary<(Type, Guid), IReadOnlyList<Guid>> _ancestry = [];

    /// <summary>Customer groups per requester.</summary>
    internal Dictionary<Guid, IReadOnlyCollection<Guid>> CustomerGroups { get; } = [];

    /// <summary>The node and the nodes above it, read once per node.</summary>
    public async Task<IReadOnlyList<Guid>> AncestryAsync<TNode>(IQueryable<TNode> nodes, Guid? id, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        if (id is not { } nodeId)
        {
            return [];
        }

        if (!_ancestry.TryGetValue((typeof(TNode), nodeId), out var ancestry))
        {
            ancestry = await TicketRouter.AncestryAsync(nodes, nodeId, cancellationToken).ConfigureAwait(false);
            _ancestry[(typeof(TNode), nodeId)] = ancestry;
        }

        return ancestry;
    }
}
