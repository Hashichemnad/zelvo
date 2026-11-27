using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Channels;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task K1 (module 20): the requirement's story as a plain-language rule waiting for approval in
/// each sample organisation, in its own language: "when a VIP raises a critical incident, notify
/// the IT manager on WhatsApp". The VIP group is B3's customer group (product owner decision
/// 2026-11-26); the IT manager is the service desk's team lead. The draft does not run until the
/// demo administrator approves it in Settings → Workflow rules. The AI feature stays off, like
/// every AI feature in the demo, until a provider is set up.
/// </summary>
internal sealed class DemoPlainLanguageRulesSeeder : IDemoSeeder
{
    public string Key => "k1-plain-language-rules";

    public int Order => 270;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var vip = await db.CustomerGroups.AsNoTracking().OrderBy(g => g.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var desk = await db.SupportGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Name.En == "Service desk", cancellationToken).ConfigureAwait(false)
            ?? await db.SupportGroups.AsNoTracking().OrderBy(g => g.Name.En).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (vip is null || desk is null)
        {
            return;
        }

        var text = government
            ? "عندما يقدّم أحد كبار المسؤولين بلاغًا حرجًا، أبلغ مدير تقنية المعلومات عبر واتساب"
            : "When a VIP raises a critical incident, notify the IT manager on WhatsApp";
        var position = await db.WorkflowRules.AnyAsync(cancellationToken).ConfigureAwait(false)
            ? await db.WorkflowRules.MaxAsync(r => r.Position, cancellationToken).ConfigureAwait(false) + 1
            : 0;
        db.WorkflowRules.Add(new WorkflowRule
        {
            Name = new LocalizedText("VIP critical incidents to the IT manager", "البلاغات الحرجة لكبار المتعاملين إلى مدير تقنية المعلومات"),
            Position = position,
            Trigger = WorkflowTrigger.TicketCreated,
            RecordType = TicketType.Incident,
            IsDraft = true,
            SourceText = text,
            SourceLanguage = government ? LocalizedText.Arabic : LocalizedText.English,
            Unmapped = "[]",
            Conditions = RuleDocuments.Write<WorkflowCondition>(
            [
                new WorkflowCondition { Field = RuleFields.RequesterCustomerGroup, Op = RuleOperators.In, Value = new JsonArray(vip.Id.ToString()) },
                new WorkflowCondition { Field = RuleFields.Priority, Op = RuleOperators.Eq, Value = JsonValue.Create(1) },
            ]),
            Actions = RuleDocuments.Write<WorkflowAction>(
            [
                new NotifyAction
                {
                    Recipients = [$"groupLeads:{desk.Id}"],
                    Subject = new LocalizedText("VIP critical incident {{ticket.number}}", "بلاغ حرج لأحد كبار المتعاملين {{ticket.number}}"),
                    Body = new LocalizedText(
                        "Dear {{recipient.name}},\n\n{{requester.name}} raised the critical incident {{ticket.number}}: {{ticket.title}}.",
                        "حضرة {{recipient.name}} المحترم،\n\nقدّم {{requester.name}} البلاغ الحرج {{ticket.number}}: {{ticket.title}}."),
                    Email = true,
                    Channels = [MessagingKind.WhatsApp],
                },
            ]),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
