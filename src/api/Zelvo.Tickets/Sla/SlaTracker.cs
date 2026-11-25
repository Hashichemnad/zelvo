using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Directory;
using Zelvo.Data.Sla;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets.Sla;

/// <summary>What SLA policy matching looks at.</summary>
/// <param name="RecordType">The ticket's record type.</param>
/// <param name="Priority">The ticket's priority, 1 (highest); <see langword="null"/> for record types without one.</param>
/// <param name="CategoryAncestry">The ticket's category and the categories above it.</param>
/// <param name="CustomerGroups">Active customer groups the requester belongs to.</param>
public sealed record SlaSubject(TicketType RecordType, int? Priority, IReadOnlyCollection<Guid> CategoryAncestry, IReadOnlyCollection<Guid> CustomerGroups);

/// <summary>
/// SLA policy matching (module 8, task B3): policies are tried in order and the first active one
/// whose conditions all hold wins (record type, category or below, customer group).
/// </summary>
public static class SlaMatcher
{
    /// <summary>Whether <paramref name="policy"/>'s conditions hold for <paramref name="subject"/>.</summary>
    public static bool Matches(SlaPolicy policy, SlaSubject subject)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(subject);
        return policy.Active
            && (policy.RecordType is not { } type || type == subject.RecordType)
            && (policy.CategoryId is not { } category || subject.CategoryAncestry.Contains(category))
            && (policy.CustomerGroupId is not { } group || subject.CustomerGroups.Contains(group));
    }

    /// <summary>The first matching policy in position order.</summary>
    public static SlaPolicy? FirstMatch(IEnumerable<SlaPolicy> policies, SlaSubject subject)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return policies.OrderBy(p => p.Position).ThenBy(p => p.Id).FirstOrDefault(p => Matches(p, subject));
    }

    /// <summary>The policy's target of <paramref name="metric"/> for <paramref name="priority"/>, in minutes.</summary>
    public static int? TargetOf(SlaPolicy? policy, SlaMetric metric, int? priority)
    {
        if (policy is null || priority is not { } p)
        {
            return null;
        }

        var target = policy.Targets.FirstOrDefault(t => t.Priority == p);
        return metric == SlaMetric.Response ? target?.ResponseMinutes : target?.ResolutionMinutes;
    }

    /// <summary>Thresholds of <paramref name="metric"/> in ascending order; the breach (100 %) is always one.</summary>
    public static IReadOnlyList<int> Thresholds(SlaPolicy? policy, SlaMetric metric) =>
        [.. (policy?.Escalations ?? []).Where(e => e.Metric is null || e.Metric == metric).Select(e => e.Percent).Append(100).Distinct().Order()];

    /// <summary>Whether the requester with <paramref name="email"/> belongs to <paramref name="group"/> by one of its domains.</summary>
    public static bool InDomain(CustomerGroup group, string? email)
    {
        ArgumentNullException.ThrowIfNull(group);
        var at = email?.LastIndexOf('@') ?? -1;
        if (at < 0)
        {
            return false;
        }

        var domain = email![(at + 1)..].Trim().ToLowerInvariant();
        return group.EmailDomains.Contains(domain, StringComparer.Ordinal);
    }
}

/// <summary>
/// The SLA set-up of a tenant, loaded once per save or per worker batch: active policies with
/// targets and escalations, calendars, the default calendar, customer groups, group calendars,
/// categories and the on-hold reasons that pause SLAs.
/// </summary>
public sealed class SlaSetup
{
    private readonly Dictionary<Guid, WorkingCalendar> _calendars;
    private readonly Dictionary<Guid, Guid?> _parents;
    private readonly Dictionary<Guid, Guid?> _groupCalendars;

    private SlaSetup(
        List<SlaPolicy> policies, Dictionary<Guid, WorkingCalendar> calendars, Guid? defaultCalendarId, List<CustomerGroup> customerGroups,
        Dictionary<Guid, Guid?> groupCalendars, Dictionary<Guid, Guid?> parents, HashSet<string> pausingReasons)
    {
        Policies = policies;
        _calendars = calendars;
        DefaultCalendarId = defaultCalendarId;
        CustomerGroups = customerGroups;
        _groupCalendars = groupCalendars;
        _parents = parents;
        PausingReasons = pausingReasons;
    }

    /// <summary>Active policies in order.</summary>
    public IReadOnlyList<SlaPolicy> Policies { get; }

    /// <summary>The tenant's default calendar.</summary>
    public Guid? DefaultCalendarId { get; }

    /// <summary>Active customer groups with their hand-added members.</summary>
    public IReadOnlyList<CustomerGroup> CustomerGroups { get; }

    /// <summary>Keys of on-hold reasons that pause SLA clocks.</summary>
    public IReadOnlySet<string> PausingReasons { get; }

    /// <summary>Loads the current tenant's set-up.</summary>
    public static async Task<SlaSetup> LoadAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var policies = await db.SlaPolicies.AsNoTracking().Where(p => p.Active)
            .Include(p => p.Targets).Include(p => p.Escalations).AsSplitQuery()
            .OrderBy(p => p.Position).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var calendars = await db.BusinessCalendars.AsNoTracking().Include(c => c.Periods).Include(c => c.Holidays).AsSplitQuery()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var groups = await db.CustomerGroups.AsNoTracking().Where(g => g.Active).Include(g => g.Members)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var groupCalendars = await db.SupportGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.CalendarId, cancellationToken).ConfigureAwait(false);
        var parents = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId, cancellationToken).ConfigureAwait(false);
        var reasons = await TicketSettingKeys.ReadOnHoldReasonsAsync(db, cancellationToken).ConfigureAwait(false);
        return new SlaSetup(
            policies,
            calendars.ToDictionary(c => c.Id, WorkingCalendar.From),
            calendars.FirstOrDefault(c => c.IsDefault)?.Id,
            groups,
            groupCalendars,
            parents,
            [.. reasons.Where(r => r.Pauses).Select(r => r.Key)]);
    }

    /// <summary>The calendar with this id; around the clock for none (or a calendar that no longer exists).</summary>
    public WorkingCalendar Calendar(Guid? id) =>
        id is { } c && _calendars.TryGetValue(c, out var calendar) && calendar.HasWorkingTime ? calendar : WorkingCalendar.AllDay;

    /// <summary>
    /// The calendar a ticket's clocks count on (product owner decision 2026-11-24): its group's
    /// calendar when the group has one, else the policy's, else the tenant default; none means
    /// around the clock.
    /// </summary>
    public Guid? CalendarIdFor(Guid? groupId, SlaPolicy? policy)
    {
        if (groupId is { } g && _groupCalendars.TryGetValue(g, out var own) && own is { } groupCalendar && _calendars.ContainsKey(groupCalendar))
        {
            return groupCalendar;
        }

        if (policy?.CalendarId is { } policyCalendar && _calendars.ContainsKey(policyCalendar))
        {
            return policyCalendar;
        }

        return DefaultCalendarId;
    }

    /// <summary>The category and the categories above it.</summary>
    public IReadOnlyList<Guid> CategoryAncestry(Guid? categoryId)
    {
        var ancestry = new List<Guid>();
        var current = categoryId;
        while (current is { } id && ancestry.Count < TreeNode.MaxDepth)
        {
            ancestry.Add(id);
            current = _parents.GetValueOrDefault(id);
        }

        return ancestry;
    }

    /// <summary>Active customer groups of a requester: added by hand or by email domain.</summary>
    public IReadOnlyList<Guid> CustomerGroupsOf(Guid requesterId, string? email) =>
        [.. CustomerGroups.Where(g => g.Members.Any(m => m.UserId == requesterId) || SlaMatcher.InDomain(g, email)).Select(g => g.Id)];
}

/// <summary>
/// Keeps the SLA clocks of tickets up to date (module 8, task B3). Called with every save of a
/// ticket (inside its transaction) and by the timer job for clocks that are due a look. For each
/// ticket it counts the business time that passed since the last look, matches the SLA policy again
/// (the elapsed time is kept), sets the state from the ticket (running, paused on hold with a
/// pausing reason, stopped when responded to or resolved), records thresholds reached and works out
/// when to look next.
/// </summary>
public sealed class SlaTracker(ZelvoDbContext db, IAuditLog audit)
{
    /// <summary>
    /// Updates the clocks of <paramref name="tickets"/> (with their incident details loaded). Locks
    /// the tickets' rows and then their clocks, in that order, so the API and the timer job never
    /// deadlock. Needs an open transaction.
    /// </summary>
    public async Task UpdateAsync(IReadOnlyCollection<Ticket> tickets, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        if (tickets.Count == 0)
        {
            return;
        }

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("SLA clocks are updated inside the transaction that saves the ticket.");
        }

        var setup = await SlaSetup.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var ids = tickets.Select(t => t.Id).Distinct().Order().ToArray();
        var saved = tickets.Where(t => db.Entry(t).State != EntityState.Added).Select(t => t.Id).Distinct().Order().ToArray();
        if (saved.Length > 0)
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tickets WHERE id = ANY({saved}) ORDER BY id FOR UPDATE", cancellationToken).ConfigureAwait(false);
        }

        var clocks = saved.Length == 0
            ? []
            : await db.TicketSlas.FromSql($"SELECT * FROM ticket_slas WHERE ticket_id = ANY({saved}) ORDER BY ticket_id, metric FOR UPDATE")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var requesterIds = tickets.Select(t => t.RequesterId).Distinct().ToList();
        var emails = await db.Users.AsNoTracking().Where(u => requesterIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken).ConfigureAwait(false);
        var people = new Recipients(db);
        var services = await ServiceSla.LoadAsync(db, tickets, cancellationToken).ConfigureAwait(false);

        // Task C5: bundles are containers; their parts have the clocks.
        var bundles = await ServiceSla.BundlesAsync(db, tickets, cancellationToken).ConfigureAwait(false);
        foreach (var ticket in tickets.DistinctBy(t => t.Id))
        {
            if (bundles.Contains(ticket.Id))
            {
                continue;
            }

            var subject = new SlaSubject(ticket.Type, ticket.Incident?.Priority ?? ticket.Voice?.Priority, setup.CategoryAncestry(ticket.CategoryId),
                setup.CustomerGroupsOf(ticket.RequesterId, emails.GetValueOrDefault(ticket.RequesterId)));
            var matched = SlaMatcher.FirstMatch(setup.Policies, subject);
            // Task C4: a service's own ownership and completion targets and escalations replace the policy's.
            var service = services.GetValueOrDefault(ticket.Id);
            var policy = service?.Apply(matched) ?? matched;
            var calendarId = setup.CalendarIdFor(ticket.GroupId, matched);
            var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var metric in Enum.GetValues<SlaMetric>())
            {
                var clock = clocks.FirstOrDefault(c => c.TicketId == ticket.Id && c.Metric == metric);
                var isNew = clock is null;
                if (clock is null)
                {
                    clock = new TicketSla
                    {
                        TicketId = ticket.Id,
                        Metric = metric,
                        State = SlaClockState.Running,
                        CountedUntil = ticket.CreatedAt,
                        CalendarId = calendarId,
                    };
                    db.TicketSlas.Add(clock);
                }

                var before = (clock.PolicyId, clock.TargetMinutes);
                await UpdateClockAsync(clock, isNew, ticket, policy, service?.TargetOf(clock.Metric), matched?.Id, calendarId, setup, people, now, cancellationToken)
                    .ConfigureAwait(false);
                if (before != (clock.PolicyId, clock.TargetMinutes) || (isNew && clock.TargetMinutes is not null))
                {
                    changes[metric.ToString().ToLowerInvariant()] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["policyId"] = clock.PolicyId?.ToString(),
                        ["targetMinutes"] = clock.TargetMinutes,
                    };
                }
            }

            if (changes.Count > 0)
            {
                audit.Add(AuditCategory.Change, "tickets.sla.policy", "ticket", ticket.Id.ToString(), changes);
            }
        }
    }

    private async Task UpdateClockAsync(
        TicketSla clock, bool isNew, Ticket ticket, SlaPolicy? policy, int? serviceTarget, Guid? policyId, Guid? calendarId, SlaSetup setup, Recipients people,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var desired = DesiredState(clock.Metric, ticket, setup.PausingReasons);

        // 1. Count the business time since the last look, on the calendar the clock counted on.
        //    A clock created for a ticket from before B3 counts up to when the ticket stopped it.
        var oldCalendar = setup.Calendar(clock.CalendarId);
        var countTo = now;
        if (isNew && desired == SlaClockState.Stopped)
        {
            countTo = StoppedAt(clock.Metric, ticket) ?? now;
        }

        var previousElapsed = clock.ElapsedSeconds;
        var previousCounted = clock.CountedUntil;
        if (clock.State == SlaClockState.Running && countTo > clock.CountedUntil)
        {
            clock.ElapsedSeconds += oldCalendar.SecondsBetween(clock.CountedUntil, countTo);
        }

        clock.CountedUntil = countTo > clock.CountedUntil ? countTo : clock.CountedUntil;

        // 2. The policy, target and calendar that apply now; the elapsed time is kept.
        clock.PolicyId = serviceTarget is null ? policyId : null;
        clock.TargetMinutes = serviceTarget ?? SlaMatcher.TargetOf(policy, clock.Metric, ticket.Incident?.Priority ?? ticket.Voice?.Priority);
        clock.CalendarId = calendarId;
        var calendar = setup.Calendar(calendarId);

        // 3. Thresholds reached by the time counted so far (the breach is 100 %).
        if (clock.TargetMinutes is { } target)
        {
            var targetSeconds = target * 60L;
            foreach (var percent in SlaMatcher.Thresholds(policy, clock.Metric).Where(p => p > clock.ReachedPercent))
            {
                var thresholdSeconds = (long)Math.Ceiling(targetSeconds * percent / 100.0);
                if (clock.ElapsedSeconds < thresholdSeconds)
                {
                    break;
                }

                var reachedAt = previousElapsed < thresholdSeconds && clock.State == SlaClockState.Running
                    ? oldCalendar.Add(previousCounted, thresholdSeconds - previousElapsed) ?? clock.CountedUntil
                    : clock.CountedUntil;
                if (reachedAt > clock.CountedUntil)
                {
                    reachedAt = clock.CountedUntil;
                }

                if (percent == 100)
                {
                    clock.BreachedAt ??= reachedAt;
                }

                clock.ReachedPercent = percent;
                if (!isNew || desired != SlaClockState.Stopped)
                {
                    await RecordAlertAsync(clock, ticket, policy, percent, reachedAt, people, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // 4. Follow the ticket: running, paused or stopped.
        if (desired == SlaClockState.Stopped && clock.State != SlaClockState.Stopped)
        {
            clock.StoppedAt = isNew ? countTo : now;
        }

        clock.State = desired;

        // 5. When the target falls due and when to look next.
        if (clock.State == SlaClockState.Running && clock.TargetMinutes is { } running)
        {
            var targetSeconds = running * 60L;
            clock.DueAt = clock.BreachedAt ?? calendar.Add(clock.CountedUntil, targetSeconds - clock.ElapsedSeconds);
            var next = SlaMatcher.Thresholds(policy, clock.Metric).FirstOrDefault(p => p > clock.ReachedPercent);
            clock.NextCheckAt = next == 0
                ? null
                : calendar.Add(clock.CountedUntil, Math.Max(0, (long)Math.Ceiling(targetSeconds * next / 100.0) - clock.ElapsedSeconds));
        }
        else
        {
            clock.DueAt = null;
            clock.NextCheckAt = null;
        }
    }

    private async Task RecordAlertAsync(
        TicketSla clock, Ticket ticket, SlaPolicy? policy, int percent, DateTimeOffset reachedAt, Recipients people, CancellationToken cancellationToken)
    {
        var escalations = (policy?.Escalations ?? []).Where(e => e.Percent == percent && (e.Metric is null || e.Metric == clock.Metric)).ToList();
        var recipients = new SortedSet<Guid>();
        foreach (var escalation in escalations)
        {
            if (escalation.NotifyAssignee && ticket.AssigneeId is { } assignee)
            {
                recipients.Add(assignee);
            }

            if (escalation.NotifyGroupLeads && ticket.GroupId is { } group)
            {
                recipients.UnionWith(await people.LeadsAsync(group, cancellationToken).ConfigureAwait(false));
            }

            if (escalation.NotifyGroupId is { } other)
            {
                recipients.UnionWith(await people.MembersAsync(other, cancellationToken).ConfigureAwait(false));
            }
        }

        // Each threshold once per clock (also kept by a unique index).
        if (await db.SlaAlerts.AnyAsync(a => a.TicketSlaId == clock.Id && a.Percent == percent, cancellationToken).ConfigureAwait(false)
            || db.ChangeTracker.Entries<SlaAlert>().Any(e => e.Entity.TicketSlaId == clock.Id && e.Entity.Percent == percent))
        {
            return;
        }

        db.SlaAlerts.Add(new SlaAlert
        {
            TicketId = ticket.Id,
            TicketSlaId = clock.Id,
            Metric = clock.Metric,
            Percent = percent,
            ReachedAt = reachedAt,
            RecipientIds = [.. recipients],
        });
    }

    /// <summary>What the ticket says the clock should do.</summary>
    public static SlaClockState DesiredState(SlaMetric metric, Ticket ticket, IReadOnlySet<string> pausingReasons)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(pausingReasons);
        var stopped = metric == SlaMetric.Response
            ? ticket.RespondedAt is not null || ticket.State is TicketState.Resolved or TicketState.Closed
            : ticket.State is TicketState.Resolved or TicketState.Closed;
        if (stopped)
        {
            return SlaClockState.Stopped;
        }

        // Task C3 (module 2): clocks also pause while the ticket waits for an approval.
        // Tasks C5 and E8: also while held back (a later part of a bundle, a fee to pay).
        return ticket.AwaitingApproval || ticket.Held || (ticket.State == TicketState.OnHold && ticket.OnHoldReason is { } reason && pausingReasons.Contains(reason))
            ? SlaClockState.Paused
            : SlaClockState.Running;
    }

    private static DateTimeOffset? StoppedAt(SlaMetric metric, Ticket ticket) =>
        metric == SlaMetric.Response ? ticket.RespondedAt ?? ticket.ResolvedAt ?? ticket.ClosedAt : ticket.ResolvedAt ?? ticket.ClosedAt;

    /// <summary>Group leads and members, loaded once per group.</summary>
    private sealed class Recipients(ZelvoDbContext db)
    {
        private readonly Dictionary<(Guid, bool), List<Guid>> _cache = [];

        public Task<List<Guid>> LeadsAsync(Guid groupId, CancellationToken cancellationToken) => LoadAsync(groupId, true, cancellationToken);

        public Task<List<Guid>> MembersAsync(Guid groupId, CancellationToken cancellationToken) => LoadAsync(groupId, false, cancellationToken);

        private async Task<List<Guid>> LoadAsync(Guid groupId, bool leadsOnly, CancellationToken cancellationToken)
        {
            if (!_cache.TryGetValue((groupId, leadsOnly), out var ids))
            {
                ids = await db.SupportGroupMembers.AsNoTracking()
                    .Where(m => m.GroupId == groupId && (!leadsOnly || m.IsLead) && m.User!.Status == Data.Identity.UserStatus.Active)
                    .Select(m => m.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
                _cache[(groupId, leadsOnly)] = ids;
            }

            return ids;
        }
    }
}

/// <summary>
/// A service request's own SLA (task C4): the ownership target (response clock), the completion
/// target (resolution clock) and escalation thresholds, copied from the service when the request
/// reached the service desk.
/// </summary>
internal sealed class ServiceSla
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private int? _ownership;
    private int? _completion;
    private List<Data.Catalog.ServiceEscalation> _escalations = [];

    /// <summary>The bundle requests among <paramref name="tickets"/> (task C5).</summary>
    public static async Task<HashSet<Guid>> BundlesAsync(ZelvoDbContext db, IReadOnlyCollection<Ticket> tickets, CancellationToken cancellationToken)
    {
        var ids = tickets.Where(t => t.Type == TicketType.ServiceRequest).Select(t => t.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var result = db.ChangeTracker.Entries<Data.Catalog.ServiceRequestDetails>().Where(e => e.Entity.IsBundle && ids.Contains(e.Entity.TicketId)).Select(e => e.Entity.TicketId).ToHashSet();
        foreach (var id in await db.ServiceRequests.AsNoTracking().Where(r => r.IsBundle && ids.Contains(r.TicketId)).Select(r => r.TicketId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(id);
        }

        return result;
    }

    /// <summary>The service SLAs of the request tickets among <paramref name="tickets"/> that have one.</summary>
    public static async Task<Dictionary<Guid, ServiceSla>> LoadAsync(ZelvoDbContext db, IReadOnlyCollection<Ticket> tickets, CancellationToken cancellationToken)
    {
        var ids = tickets.Where(t => t.Type == TicketType.ServiceRequest).Select(t => t.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var tracked = db.ChangeTracker.Entries<Data.Catalog.ServiceRequestDetails>().Where(e => ids.Contains(e.Entity.TicketId)).Select(e => e.Entity).ToList();
        var missing = ids.Where(id => tracked.All(t => t.TicketId != id)).ToList();
        if (missing.Count > 0)
        {
            tracked.AddRange(await db.ServiceRequests.AsNoTracking().Where(r => missing.Contains(r.TicketId)).ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        var result = new Dictionary<Guid, ServiceSla>();
        foreach (var details in tracked)
        {
            var escalations = string.IsNullOrWhiteSpace(details.Escalations)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<Data.Catalog.ServiceEscalation>>(details.Escalations, Json) ?? [];
            if (details.OwnershipMinutes is null && details.CompletionMinutes is null && escalations.Count == 0)
            {
                continue;
            }

            result[details.TicketId] = new ServiceSla { _ownership = details.OwnershipMinutes, _completion = details.CompletionMinutes, _escalations = escalations };
        }

        return result;
    }

    /// <summary>The service's target of <paramref name="metric"/>, if it has one.</summary>
    public int? TargetOf(SlaMetric metric) => metric == SlaMetric.Response ? _ownership : _completion;

    /// <summary>The policy whose escalations apply: the service's thresholds when it has any (on an unsaved copy), else the policy's.</summary>
    public SlaPolicy? Apply(SlaPolicy? policy)
    {
        if (_escalations.Count == 0)
        {
            return policy;
        }

        var effective = new SlaPolicy { Name = policy?.Name ?? new Data.Localization.LocalizedText(null, null), CalendarId = policy?.CalendarId };
        effective.Targets.AddRange(policy?.Targets ?? []);
        effective.Escalations.AddRange(_escalations.Select(e => new SlaEscalation
        {
            Metric = e.Metric,
            Percent = e.Percent,
            NotifyAssignee = e.NotifyAssignee,
            NotifyGroupLeads = e.NotifyGroupLeads,
            NotifyGroupId = e.NotifyGroupId,
        }));
        return effective;
    }
}
