using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zelvo.Connectors;
using Zelvo.Data;
using Zelvo.Data.Connectors;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task E7: a directory connector (Entra ID for the government entity, Active Directory for the
/// company) and a CRM, both turned off (demo installations reach no real system), a REST connector
/// with an "update the case" operation and the workflow rule that calls it, and a Prometheus
/// monitoring source with an alert that raised an incident. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoConnectorsSeeder : IDemoSeeder
{
    public string Key => "e7-connectors";

    public int Order => 260;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        if (await db.Connectors.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var organisation = context.Organisation;
        var government = organisation.Kind == DemoOrganisationKind.Government;
        var arabic = organisation.DefaultLanguage == LocalizedText.Arabic;
        var domain = organisation.EmailDomain;
        db.Connectors.Add(government
            ? new Connector
            {
                Name = arabic ? "دليل الموظفين (Entra ID)" : "Staff directory (Entra ID)",
                Kind = ConnectorKind.EntraId,
                Active = false,
                Settings = new ConnectorSettings { Entra = new EntraSettings { DirectoryId = Guid.NewGuid().ToString(), ClientId = Guid.NewGuid().ToString() } }.Write(),
            }
            : new Connector
            {
                Name = "Active Directory",
                Kind = ConnectorKind.Ldap,
                Active = false,
                Settings = new ConnectorSettings
                {
                    Ldap = new LdapSettings { Host = $"dc01.{domain}", BindDn = $"CN=zelvo-sync,OU=Service,DC={domain.Split('.')[0]},DC=local", BaseDn = $"OU=Staff,DC={domain.Split('.')[0]},DC=local" },
                }.Write(),
            });
        db.Connectors.Add(new Connector
        {
            Name = government ? "Microsoft Dynamics 365" : "Salesforce",
            Kind = government ? ConnectorKind.Dynamics365 : ConnectorKind.Salesforce,
            Active = false,
            Settings = new ConnectorSettings
            {
                Crm = government
                    ? new CrmSettings { Url = $"https://{organisation.Slug}.crm4.dynamics.com", DirectoryId = Guid.NewGuid().ToString(), ClientId = Guid.NewGuid().ToString() }
                    : new CrmSettings { Url = $"https://{organisation.Slug}.my.salesforce.com", ClientId = "3MVG9-demo" },
            }.Write(),
        });
        var rest = new Connector
        {
            Name = arabic ? "نظام الحالات" : "Case system",
            Kind = ConnectorKind.Rest,
            Active = false,
            Settings = new ConnectorSettings
            {
                Rest = new RestSettings
                {
                    BaseUrl = $"https://cases.{domain}/api",
                    Auth = RestAuth.None,
                    Operations =
                    [
                        new RestOperation
                        {
                            Key = "update-case",
                            Name = new LocalizedText("Update the case", "تحديث الحالة"),
                            Method = "POST",
                            Path = "/cases/{{ticket.number}}",
                            Body = """{"state":"{{ticket.state}}","notes":"{{ticket.resolutionNotes}}"}""",
                            Results = [new RestResultField("status", new LocalizedText("Case status", "حالة الحالة"), "case.status")],
                            ExternalRefPath = "case.id",
                        },
                    ],
                },
            }.Write(),
        };
        db.Connectors.Add(rest);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.WorkflowRules.Add(new Data.Workflows.WorkflowRule
        {
            Name = new LocalizedText("Resolved complaints update the case system", "الشكاوى المحلولة تحدّث نظام الحالات"),
            Description = new LocalizedText(null, null),
            Active = false,
            Trigger = Data.Workflows.WorkflowTrigger.StateChanged,
            RecordType = TicketType.Complaint,
            Position = 900,
            Conditions = """[{"field":"state","op":"in","value":["Resolved"]}]""",
            Actions = $$"""[{"type":"connector","connectorId":"{{rest.Id}}","operation":"update-case"}]""",
        });

        // A Prometheus source and the incident its alert raised.
        var account = await SystemAccounts.CreateAsync(db, "monitoring", "Prometheus", cancellationToken).ConfigureAwait(false);
        var group = await db.SupportGroups.AsNoTracking().OrderBy(g => g.CreatedAt).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var source = new MonitoringSource
        {
            Name = "Prometheus",
            Format = AlertFormat.Alertmanager,
            TokenHash = AlertIntake.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            GroupId = group,
            ResolveOnClear = true,
            SystemUserId = account.Id,
            LastAlertAt = DateTimeOffset.UtcNow.AddMinutes(-40),
        };
        db.MonitoringSources.Add(source);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var prefixes = await TicketSettingKeys.ReadAsync<IReadOnlyDictionary<TicketType, string>>(db, TicketSettingKeys.NumberPrefixes,
            TicketNumbers.DefaultPrefixes, TicketNumbers.AreValid, cancellationToken).ConfigureAwait(false);
        var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken).ConfigureAwait(false);
        var like = await db.Tickets.AsNoTracking().Where(t => t.CategoryId != null && t.LocationId != null).OrderBy(t => t.Sequence)
            .Select(t => new { t.CategoryId, t.LocationId }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, TicketType.Incident, cancellationToken).ConfigureAwait(false);
        var at = DateTimeOffset.UtcNow.AddMinutes(-40);
        var ticket = new Ticket
        {
            Type = TicketType.Incident,
            Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Incident), sequence),
            Sequence = sequence,
            Title = "Disk almost full on db-01",
            Description = "Free space below 5 % on /var/lib/postgresql.",
            Channel = TicketChannel.Monitoring,
            RequesterId = account.Id,
            CreatedById = account.Id,
            GroupId = group,
            CategoryId = like?.CategoryId,
            LocationId = like?.LocationId,
            CreatedAt = at,
            UpdatedAt = at,
        };
        ticket.Incident = new Incident { TicketId = ticket.Id, Impact = 1, Urgency = 1, Priority = matrix.PriorityOf(1, 1) };
        db.Tickets.Add(ticket);
        db.MonitoringAlerts.Add(new MonitoringAlert { SourceId = source.Id, Key = "demo-disk-full-db-01", TicketId = ticket.Id, State = AlertState.Firing, Count = 2, FirstSeenAt = at, LastSeenAt = at.AddMinutes(20) });
        db.TicketComments.Add(new TicketComment
        {
            TicketId = ticket.Id, Visibility = CommentVisibility.Internal, ByStaff = true, AuthorId = account.Id, Language = organisation.DefaultLanguage, CreatedAt = at.AddMinutes(20),
            Body = arabic ? "تكرر التنبيه (2 مرات): Disk almost full on db-01" : "The alert fired again (2 times): Disk almost full on db-01",
        });
        await new Tickets.Sla.SlaTracker(db, context.Get<Data.Auditing.IAuditLog>()).UpdateAsync([ticket], DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // In the API, saving the incident queued "received"; migrate has no notification interceptor.
        if (!await db.Notifications.AnyAsync(n => n.TicketId == ticket.Id && n.EventType == Zelvo.Notifications.NotificationEvents.IncidentReceived, cancellationToken).ConfigureAwait(false))
        {
            db.Notifications.Add(new Data.Notifications.Notification
            {
                EventType = Zelvo.Notifications.NotificationEvents.IncidentReceived, RecipientId = account.Id, TicketId = ticket.Id, LinkPath = $"/my-incidents/{ticket.Id}", CreatedAt = at,
                Data = Zelvo.Notifications.NotificationData.Serialize(new Dictionary<string, Zelvo.Notifications.PlaceholderValue>(StringComparer.Ordinal)
                {
                    [Zelvo.Notifications.Placeholders.TicketNumber] = Zelvo.Notifications.PlaceholderValue.OfText(ticket.Number),
                    [Zelvo.Notifications.Placeholders.TicketTitle] = Zelvo.Notifications.PlaceholderValue.OfText(ticket.Title),
                    [Zelvo.Notifications.Placeholders.TicketCreatedAt] = Zelvo.Notifications.PlaceholderValue.OfDate(ticket.CreatedAt),
                }),
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
