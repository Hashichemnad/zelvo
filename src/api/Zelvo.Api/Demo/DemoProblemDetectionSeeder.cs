using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Incidents;
using Zelvo.Api.Problems;
using Zelvo.Data;
using Zelvo.Data.Problems;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task H2: suggested problems in each sample organisation. Ten incidents of one category over the
/// last weeks (the customer happiness centre's printer for the government entity, in Arabic; Wi-Fi for the company, in
/// English), six of them reporting the same fault; a suggestion for those six as the AI would make
/// it (the demo has AI off, so it is sample data, without an AI call); and the trend Zelvo's own
/// detection then finds in that category against the six months of F2's history. Fictional.
/// </summary>
internal sealed class DemoProblemDetectionSeeder : IDemoSeeder
{
    public string Key => "h2-problem-detection";

    // After the history of f2-reporting and the problems of h1-problems.
    public int Order => 297;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var requesterEmail = context.Organisation.User("requester") is { } r ? context.Organisation.UsernameOf(r) : null;
        var requester = requesterEmail is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == requesterEmail, cancellationToken).ConfigureAwait(false);
        var categoryName = government ? "Printers" : "Wi-Fi";
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name.En == categoryName, cancellationToken).ConfigureAwait(false);
        if (requester is null || category is null)
        {
            return;
        }

        var similar = government
            ? new[]
            {
                ("طابعة مركز الخدمة تتوقف عن الطباعة", "تتوقف طابعة مركز إسعاد المتعاملين عن الطباعة بعد عدة صفحات وتظهر رسالة انحشار الورق."),
                ("انحشار الورق في طابعة مركز الخدمة", "تظهر رسالة انحشار الورق في طابعة مركز الخدمة بعد عدة صفحات."),
                ("طابعة مركز الخدمة لا تكمل الطباعة", "تتوقف الطابعة في مركز الخدمة عن الطباعة وتظهر رسالة انحشار الورق."),
                ("رسالة انحشار الورق في طابعة مركز الخدمة", "طابعة مركز إسعاد المتعاملين تعرض انحشار الورق بعد عدة صفحات."),
                ("توقف طابعة مركز الخدمة أثناء طباعة الإيصالات", "تتوقف طابعة مركز الخدمة عن الطباعة وتظهر رسالة انحشار الورق عند طباعة الإيصالات."),
                ("طابعة مركز الخدمة تتوقف مرة أخرى", "انحشار الورق في طابعة مركز إسعاد المتعاملين بعد عدة صفحات."),
            }
            : [
                ("Wi-Fi drops in the meeting rooms", "Laptops lose the office Wi-Fi in the meeting rooms every few minutes."),
                ("Office Wi-Fi keeps dropping in meeting room 3", "The Wi-Fi connection drops every few minutes in the meeting rooms."),
                ("Wi-Fi disconnects during calls in the meeting rooms", "Laptops lose the Wi-Fi in the meeting rooms during video calls."),
                ("Meeting rooms Wi-Fi drops every few minutes", "The office Wi-Fi drops in the meeting rooms, other floors are fine."),
                ("Cannot stay on the Wi-Fi in the meeting rooms", "Laptops reconnect to the office Wi-Fi every few minutes in the meeting rooms."),
                ("Wi-Fi drops again in the big meeting room", "The office Wi-Fi drops every few minutes in the meeting rooms."),
            ];
        var others = government
            ? new[] { "طلب حبر للطابعة الملونة", "إضافة طابعة لمكتب المدير", "الطابعة لا تظهر في قائمة الأجهزة", "طلب نقل طابعة إلى الطابق الأول" }
            : ["Guest Wi-Fi password for visitors", "Wi-Fi printer not found on floor 12", "New access point request for the warehouse", "Wi-Fi slow on the terrace"];

        var location = await db.Locations.AsNoTracking().Where(l => l.Active).OrderByDescending(l => l.Depth).ThenBy(l => l.Position)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var settings = context.Get<Settings.TenantSettingsStore>();
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var created = new List<Ticket>();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            async Task<Ticket> IncidentAsync(string title, string? description, DateTimeOffset at)
            {
                var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, TicketType.Incident, cancellationToken).ConfigureAwait(false);
                var ticket = new Ticket
                {
                    Type = TicketType.Incident,
                    Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Incident), sequence),
                    Sequence = sequence,
                    Title = title,
                    Description = description,
                    Channel = TicketChannel.Portal,
                    RequesterId = requester.Id,
                    CreatedById = null,
                    CategoryId = category.Id,
                    LocationId = requester.LocationId ?? location,
                    CreatedAt = at,
                    UpdatedAt = at,
                };
                ticket.Incident = new Incident { TicketId = ticket.Id, Impact = 2, Urgency = 2, Priority = 3 };
                await IncidentEndpoints.RouteAsync(db, settings, ticket, at, cancellationToken).ConfigureAwait(false);
                db.Tickets.Add(ticket);
                created.Add(ticket);
                return ticket;
            }

            var group = new List<Ticket>();
            for (var i = 0; i < similar.Length; i++)
            {
                group.Add(await IncidentAsync(similar[i].Item1, similar[i].Item2, now.AddDays(-(24 - (i * 4))).AddHours(-i)).ConfigureAwait(false));
            }

            for (var i = 0; i < others.Length; i++)
            {
                await IncidentAsync(others[i], null, now.AddDays(-(20 - (i * 5))).AddHours(-3)).ConfigureAwait(false);
            }

            await new Tickets.Sla.SlaTracker(db, context.Get<Data.Auditing.IAuditLog>()).UpdateAsync(created, now, cancellationToken).ConfigureAwait(false);
            var suggestion = new ProblemSuggestion
            {
                Kind = ProblemSuggestionKind.SimilarIncidents,
                Title = government ? "انحشار الورق المتكرر في طابعة مركز إسعاد المتعاملين" : "Wi-Fi drops in the meeting rooms every few minutes",
                Language = context.Organisation.DefaultLanguage,
                Reason = government
                    ? "تصف البلاغات الستة جميعها توقف طابعة مركز إسعاد المتعاملين نفسها عن الطباعة بسبب انحشار الورق خلال الأسابيع الثلاثة الماضية."
                    : "All six report laptops losing the office Wi-Fi in the meeting rooms only, every few minutes.",
                Confidence = 86,
                CategoryId = category.Id,
                WindowStart = group.Min(t => t.CreatedAt),
                WindowEnd = group.Max(t => t.CreatedAt),
                IncidentCount = group.Count,
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddHours(-2),
            };
            db.ProblemSuggestions.Add(suggestion);
            foreach (var ticket in group)
            {
                db.ProblemSuggestionIncidents.Add(new ProblemSuggestionIncident { SuggestionId = suggestion.Id, IncidentId = ticket.Id });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await IncidentEndpoints.IndexAsync(db, context.Get<ISearchIndexer>(), created, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Zelvo's own trend detection over the sample history (AI is off in the demo, so only trends).
        await new ProblemTrendDetector(db, context.Get<Data.Auditing.IAuditLog>()).RunAsync(ProblemDetectionSettingsValue.Default, now, cancellationToken).ConfigureAwait(false);
    }
}
