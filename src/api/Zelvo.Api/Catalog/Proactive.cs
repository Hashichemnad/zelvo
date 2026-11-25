using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Proactive services (task C5, module 2): services Zelvo offers or starts without the person
/// asking. A run is due for one person when
/// <list type="bullet">
/// <item>a date comes near: a date answer on the person's completed request of another service
/// (for example 30 days before a trade licence expires), or a date another system sent through the API;</item>
/// <item>a schedule's occurrence comes, for the people it names;</item>
/// <item>another system sends an event through the API (for example a new joiner in the HR system).</item>
/// </list>
/// Each run is unique per service, trigger, person and occurrence. A due run is offered (the
/// person is told, and the offer opens the form filled in with what Zelvo knows; product owner
/// decision 2026-11-25) or started automatically; an automatic start becomes an offer when the
/// form or required documents need the person.
/// </summary>
internal sealed class ProactiveEngine(ZelvoDbContext db, ServiceRequestCreator creator, PendingNotifications pending, IAuditLog audit)
{
    /// <summary>Most people a schedule's occurrence reaches.</summary>
    public const int MaxPeoplePerOccurrence = 5000;

    /// <summary>Discovers due dates and schedule occurrences, then offers or starts due runs and expires old offers. Returns how many runs changed.</summary>
    public async Task<int> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var services = await ProactiveServicesAsync(cancellationToken);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        foreach (var service in services)
        {
            foreach (var trigger in service.Card.Proactive.Triggers)
            {
                if (trigger.Kind == ProactiveTriggerKind.Date && trigger.SourceServiceId is { } source && trigger.SourceFieldKey is { } field)
                {
                    await DiscoverDatesAsync(service, trigger, source, field, today, cancellationToken);
                }
                else if (trigger.Kind == ProactiveTriggerKind.Schedule)
                {
                    await DiscoverScheduleAsync(service, trigger, today, cancellationToken);
                }
            }
        }

        var changed = 0;
        var due = await db.ProactiveRuns.AsNoTracking().Where(r => r.State == ProactiveRunState.Scheduled && r.DueOn <= today)
            .OrderBy(r => r.DueOn).Select(r => r.Id).Take(500).ToListAsync(cancellationToken);
        foreach (var id in due)
        {
            // One run at a time: a request that cannot be saved does not hold the others back.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var run = await db.ProactiveRuns.FromSql($"SELECT *, xmin FROM proactive_runs WHERE id = {id} FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(cancellationToken);
            if (run is null || run.State != ProactiveRunState.Scheduled)
            {
                continue;
            }

            try
            {
                if (await ProcessAsync(run, services.FirstOrDefault(s => s.Service.Id == run.ServiceId), now, cancellationToken))
                {
                    changed++;
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                await using var retry = await db.Database.BeginTransactionAsync(cancellationToken);
                var failed = await db.ProactiveRuns.SingleAsync(r => r.Id == id, cancellationToken);
                Fail(failed, "The request could not be saved.", now);
                await db.SaveChangesAsync(cancellationToken);
                await retry.CommitAsync(cancellationToken);
            }
        }

        foreach (var offer in await db.ProactiveRuns.Where(r => r.State == ProactiveRunState.Offered && r.ExpiresAt <= now).ToListAsync(cancellationToken))
        {
            offer.State = ProactiveRunState.Expired;
            offer.EndedAt = now;
            changed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Records a date or an event another system sent through the API for <paramref name="person"/>.
    /// An event (or a date already due) is processed at once. The same reference never runs twice.
    /// </summary>
    public async Task<(ProactiveRun Run, bool Created)> RecordAsync(
        PublishedService service, ProactiveTrigger trigger, UserAccount person, string reference, DateOnly? date, JsonObject? answers, Guid? apiClientId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var occurrence = $"api:{reference}";
        var existing = await db.ProactiveRuns.SingleOrDefaultAsync(r => r.ServiceId == service.Service.Id && r.TriggerKey == trigger.Key
            && r.PersonId == person.Id && r.Occurrence == occurrence, cancellationToken);
        if (existing is not null)
        {
            return (existing, false);
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var run = new ProactiveRun
        {
            ServiceId = service.Service.Id,
            TriggerKey = trigger.Key,
            Kind = trigger.Kind,
            PersonId = person.Id,
            Occurrence = occurrence,
            DueOn = date is { } d ? d.AddDays(trigger.OffsetDays) : today,
            Answers = (answers ?? []).ToJsonString(),
            ApiClientId = apiClientId,
            CreatedAt = now,
        };
        db.ProactiveRuns.Add(run);
        if (run.DueOn <= today)
        {
            await ProcessAsync(run, service, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (run, true);
    }

    /// <summary>Offers or starts a due run. Returns whether it changed.</summary>
    public async Task<bool> ProcessAsync(ProactiveRun run, PublishedService? service, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == run.PersonId, cancellationToken);
        if (person is null || person.Status != UserStatus.Active)
        {
            return Fail(run, "The person is not an active user.", now);
        }

        if (service is null || !service.Card.Proactive.Enabled || service.Card.Proactive.Triggers.All(t => t.Key != run.TriggerKey))
        {
            return Fail(run, "The service is no longer published as proactive with this trigger.", now);
        }

        if (service.Service.State != ServiceState.Active)
        {
            // Suspended: tried again once it is active.
            return false;
        }

        // Hidden services stay hidden: only what the person may see is offered or started.
        var scope = await CatalogScope.OfUserAsync(db, person, cancellationToken);
        var visible = await PublishedServices.FindAsync(db, scope, service.Service.Id, cancellationToken);
        if (visible is null)
        {
            return Fail(run, "The service is not offered to the person.", now);
        }

        if (visible.Card.Proactive.Mode == ProactiveMode.AutoStart)
        {
            var answers = await AnswersForAsync(visible.Card, JsonNode.Parse(run.Answers) as JsonObject, cancellationToken);
            var (prepared, problem) = await creator.PrepareAsync(new NewServiceRequest
            {
                Service = visible,
                Requester = person,
                Channel = TicketChannel.Proactive,
                Answers = answers,
                ProactiveRunId = run.Id,
                FormVersionId = null,
            }, cancellationToken);
            if (problem is null && !NeedsDocuments(visible.Card))
            {
                var ticket = await creator.SaveAsync(prepared!, cancellationToken);
                run.State = ProactiveRunState.Started;
                run.TicketId = ticket.Id;
                run.EndedAt = now;
                audit.Add(AuditCategory.Change, "catalog.proactive.started", "ticket", ticket.Id.ToString(), Metadata(run));
                return true;
            }

            run.Reason = NeedsDocuments(visible.Card)
                ? "Offered: the service needs documents from the person."
                : "Offered: the form needs answers from the person.";
        }

        run.State = ProactiveRunState.Offered;
        run.OfferedAt = now;
        run.ExpiresAt = now.AddDays(visible.Card.Proactive.OfferDays);
        pending.Add(new NotificationRequest(NotificationEvents.ProactiveOffered, [person.Id],
            new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
            {
                [Placeholders.ServiceName] = PlaceholderValue.OfText(visible.Card.Name),
                [Placeholders.OfferExpiresAt] = PlaceholderValue.OfDate(run.ExpiresAt),
            },
            OfferPath(visible.Service.Id, run.Id)));
        audit.Add(AuditCategory.Change, "catalog.proactive.offered", "proactive-run", run.Id.ToString(), Metadata(run));
        return true;
    }

    /// <summary>Where an offer opens: the service's request form, filled in.</summary>
    public static string OfferPath(Guid serviceId, Guid runId) => $"/catalog/{serviceId}/request?offer={runId}";

    /// <summary>
    /// The answers to fill in, limited to the fields of the service's forms (a bundle's forms
    /// together): what the source request or the event gave.
    /// </summary>
    public async Task<JsonObject> AnswersForAsync(ServiceCard card, JsonObject? given, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var formIds = new List<Guid>();
        if (card.FormId is { } own)
        {
            formIds.Add(own);
        }

        if (card.Kind == ServiceKind.Bundle)
        {
            var ids = card.Bundle.Items.Where(i => i.Required).Select(i => i.ServiceId).OfType<Guid>().ToList();
            var contents = await db.CatalogServices.AsNoTracking().Where(s => ids.Contains(s.Id) && s.PublishedVersionId != null)
                .Join(db.CatalogServiceVersions.AsNoTracking(), s => s.PublishedVersionId, v => v.Id, (s, v) => v.Content).ToListAsync(cancellationToken);
            formIds.AddRange(contents.Select(ServiceCards.Read).Select(c => c.FormId).OfType<Guid>());
        }

        foreach (var formId in formIds.Distinct())
        {
            if (await FormResolver.PublishedAsync(db, formId, cancellationToken) is { } form)
            {
                foreach (var field in form.PlainFields.Where(f => f.Type is not (FieldType.File or FieldType.Signature)))
                {
                    keys.Add(field.Key);
                }
            }
        }

        return new JsonObject((given ?? []).Where(a => keys.Contains(a.Key)).Select(a => KeyValuePair.Create(a.Key, a.Value?.DeepClone())));
    }

    private static bool NeedsDocuments(ServiceCard card) => card.Documents.Any(d => d.Required);

    private static bool Fail(ProactiveRun run, string reason, DateTimeOffset now)
    {
        run.State = ProactiveRunState.Failed;
        run.Reason = reason;
        run.EndedAt = now;
        return true;
    }

    private static Dictionary<string, object?> Metadata(ProactiveRun run) => new(StringComparer.Ordinal)
    {
        ["serviceId"] = run.ServiceId.ToString(),
        ["trigger"] = run.TriggerKey,
        ["personId"] = run.PersonId.ToString(),
        ["runId"] = run.Id.ToString(),
    };

    /// <summary>Published, active services that are proactive.</summary>
    private async Task<List<PublishedService>> ProactiveServicesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.CatalogServices.AsNoTracking().Where(s => s.State == ServiceState.Active || s.State == ServiceState.Suspended)
            .Join(db.CatalogServiceVersions.AsNoTracking(), s => s.PublishedVersionId, v => v.Id, (s, v) => new { Service = s, Version = v })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new PublishedService(r.Service, r.Version, ServiceCards.Read(r.Version.Content))).Where(p => p.Card.Proactive.Enabled)];
    }

    /// <summary>
    /// Dates on completed requests of the source service: a run per request and date, due
    /// <see cref="ProactiveTrigger.OffsetDays"/> from it. Dates already past are left alone.
    /// </summary>
    private async Task DiscoverDatesAsync(PublishedService service, ProactiveTrigger trigger, Guid source, string field, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await db.ServiceRequests.AsNoTracking().Where(r => r.ServiceId == source)
            .Join(db.Tickets.AsNoTracking().Where(t => t.ResolvedAt != null), r => r.TicketId, t => t.Id, (r, t) => new { t.Id, t.RequesterId })
            .Join(db.TicketFormResponses.AsNoTracking(), x => x.Id, f => f.TicketId, (x, f) => new { x.Id, x.RequesterId, f.Answers })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var known = (await db.ProactiveRuns.AsNoTracking().Where(r => r.ServiceId == service.Service.Id && r.TriggerKey == trigger.Key)
            .Select(r => r.PersonId.ToString() + "|" + r.Occurrence).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var answers = JsonNode.Parse(row.Answers) as JsonObject;
            if (answers?[field] is not JsonValue value || !value.TryGetValue<string>(out var text)
                || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date < today)
            {
                continue;
            }

            var occurrence = $"{row.Id}:{date:yyyy-MM-dd}";
            if (!known.Add(row.RequesterId + "|" + occurrence))
            {
                continue;
            }

            db.ProactiveRuns.Add(new ProactiveRun
            {
                ServiceId = service.Service.Id,
                TriggerKey = trigger.Key,
                Kind = ProactiveTriggerKind.Date,
                PersonId = row.RequesterId,
                Occurrence = occurrence,
                DueOn = date.AddDays(trigger.OffsetDays),
                SourceTicketId = row.Id,
                Answers = answers.ToJsonString(),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The schedule's latest occurrence (on or before today): a run for each person it names, once.</summary>
    private async Task DiscoverScheduleAsync(PublishedService service, ProactiveTrigger trigger, DateOnly today, CancellationToken cancellationToken)
    {
        if (LatestOccurrence(trigger, today) is not { } occurrenceDate || occurrenceDate < (service.Version.PublishedAt is { } published ? DateOnly.FromDateTime(published.UtcDateTime) : today))
        {
            return;
        }

        var occurrence = occurrenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var people = await PeopleAsync(trigger.People, cancellationToken);
        var done = (await db.ProactiveRuns.AsNoTracking().Where(r => r.ServiceId == service.Service.Id && r.TriggerKey == trigger.Key && r.Occurrence == occurrence)
            .Select(r => r.PersonId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var person in people.Where(p => !done.Contains(p)))
        {
            db.ProactiveRuns.Add(new ProactiveRun
            {
                ServiceId = service.Service.Id,
                TriggerKey = trigger.Key,
                Kind = ProactiveTriggerKind.Schedule,
                PersonId = person,
                Occurrence = occurrence,
                DueOn = occurrenceDate,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The latest occurrence of a schedule on or before <paramref name="today"/>; none before it starts.</summary>
    public static DateOnly? LatestOccurrence(ProactiveTrigger trigger, DateOnly today)
    {
        if (trigger.StartOn is not { } start || start > today || trigger.Every < 1)
        {
            return null;
        }

        switch (trigger.Unit)
        {
            case ScheduleUnit.Day:
            case ScheduleUnit.Week:
                var step = trigger.Every * (trigger.Unit == ScheduleUnit.Week ? 7 : 1);
                var days = today.DayNumber - start.DayNumber;
                return start.AddDays(days / step * step);
            default:
                var months = trigger.Every * (trigger.Unit == ScheduleUnit.Year ? 12 : 1);
                var elapsed = ((today.Year - start.Year) * 12) + today.Month - start.Month;
                var n = elapsed / months;
                var candidate = start.AddMonths(n * months);
                return candidate > today ? start.AddMonths((n - 1) * months) : candidate;
        }
    }

    /// <summary>Active people of the named groups, roles, departments and users.</summary>
    private async Task<List<Guid>> PeopleAsync(ProactivePeople people, CancellationToken cancellationToken)
    {
        var groups = people.GroupIds.ToList();
        var roles = people.RoleKeys.ToList();
        var departments = people.DepartmentIds.ToList();
        var users = people.UserIds.ToList();
        return await db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active && (users.Contains(u.Id)
                || (u.DepartmentId != null && departments.Contains(u.DepartmentId.Value))
                || u.Roles.Any(r => roles.Contains(r.Role!.Key))
                || db.SupportGroupMembers.Any(m => m.UserId == u.Id && groups.Contains(m.GroupId))))
            .OrderBy(u => u.Id).Select(u => u.Id).Take(MaxPeoplePerOccurrence).ToListAsync(cancellationToken);
    }
}
