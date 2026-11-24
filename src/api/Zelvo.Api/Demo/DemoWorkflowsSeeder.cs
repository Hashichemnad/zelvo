using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task C3: workflow rules for each sample organisation, in English and Arabic. New laptops wait
/// for the requester's manager (the team lead) and then the approver; a salary advance is signed
/// off by the approver after fulfilment; urgent incidents (P1 and P2) are announced to the group's
/// team leads. Approval processes are not seeded: requesting a laptop as the sample requester
/// starts one.
/// </summary>
internal sealed class DemoWorkflowsSeeder : IDemoSeeder
{
    public string Key => "c3-workflows";

    public int Order => 160;

    private static LocalizedText T(string en, string ar) => new(en, ar);

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var users = await db.Users.ToListAsync(cancellationToken).ConfigureAwait(false);
        UserAccount? Persona(string persona) =>
            context.Organisation.User(persona) is { } demo ? users.SingleOrDefault(u => u.Email == context.Organisation.UsernameOf(demo)) : null;
        var lead = Persona("lead");
        var approver = Persona("approver");
        var requester = Persona("requester");
        if (requester is not null && lead is not null && requester.ManagerId is null)
        {
            requester.ManagerId = lead.Id;
        }

        var services = await db.CatalogServices.AsNoTracking().Where(s => s.Code == "IT-LAPTOP" || s.Code == "FIN-ADVANCE")
            .ToDictionaryAsync(s => s.Code, s => s.Id, cancellationToken).ConfigureAwait(false);
        var position = 0;
        void Rule(LocalizedText name, LocalizedText description, WorkflowTrigger trigger, TicketType type, IReadOnlyList<WorkflowCondition> conditions, IReadOnlyList<WorkflowAction> actions) =>
            db.WorkflowRules.Add(new WorkflowRule
            {
                Name = name,
                Description = description,
                Position = position++,
                Trigger = trigger,
                RecordType = type,
                Conditions = RuleDocuments.Write(conditions),
                Actions = RuleDocuments.Write(actions),
            });

        ApproverRef ApproverUser() => approver is null ? new ApproverRef { Kind = ApproverKind.RequesterManager } : new ApproverRef { Kind = ApproverKind.User, Id = approver.Id };

        if (services.TryGetValue("IT-LAPTOP", out var laptop))
        {
            Rule(T("New laptops need two approvals", "يحتاج الحاسوب المحمول الجديد إلى اعتمادين"),
                T("The requester's manager approves first, then the IT budget owner.", "يعتمد مدير مقدّم الطلب أولاً، ثم مسؤول ميزانية تقنية المعلومات."),
                WorkflowTrigger.TicketCreated, TicketType.ServiceRequest,
                [new WorkflowCondition { Field = RuleFields.Service, Op = RuleOperators.In, Value = new JsonArray(laptop.ToString()) }],
                [new ApprovalAction
                {
                    Stage = ApprovalStage.BeforeAssignment,
                    Levels =
                    [
                        new ApprovalLevelDefinition { Name = T("Manager approval", "اعتماد المدير"), Approvers = [new ApproverRef { Kind = ApproverKind.RequesterManager }] },
                        new ApprovalLevelDefinition { Name = T("IT budget approval", "اعتماد ميزانية تقنية المعلومات"), Approvers = [ApproverUser()] },
                    ],
                }]);
        }

        if (services.TryGetValue("FIN-ADVANCE", out var advance))
        {
            Rule(T("Salary advances are signed off", "يُعتمد إنجاز طلبات السلفة"),
                T("Finance signs off the advance before the request is resolved.", "يعتمد قسم المالية إنجاز السلفة قبل إغلاق الطلب."),
                WorkflowTrigger.TicketCreated, TicketType.ServiceRequest,
                [new WorkflowCondition { Field = RuleFields.Service, Op = RuleOperators.In, Value = new JsonArray(advance.ToString()) }],
                [new ApprovalAction
                {
                    Stage = ApprovalStage.AfterFulfilment,
                    Levels = [new ApprovalLevelDefinition { Name = T("Finance sign-off", "اعتماد المالية"), Approvers = [ApproverUser()] }],
                }]);
        }

        Rule(T("Urgent incidents to the team leads", "إبلاغ قادة الفرق بالبلاغات العاجلة"),
            T("P1 and P2 incidents are announced to the group's team leads.", "تُبلَّغ قادة فرق المجموعة بالبلاغات ذات الأولوية الأولى والثانية."),
            WorkflowTrigger.TicketCreated, TicketType.Incident,
            [new WorkflowCondition { Field = RuleFields.Priority, Op = RuleOperators.Lte, Value = JsonValue.Create(2) }],
            [new NotifyAction
            {
                Recipients = ["groupLeads"],
                Subject = T("Urgent: {{ticket.number}} {{ticket.title}}", "عاجل: {{ticket.number}} {{ticket.title}}"),
                Body = T("Dear {{recipient.name}},\n\nThe urgent incident {{ticket.number}} from {{requester.name}} is with {{ticket.group}}.",
                    "حضرة {{recipient.name}} المحترم،\n\nالبلاغ العاجل {{ticket.number}} المقدّم من {{requester.name}} لدى {{ticket.group}}."),
                Email = true,
            }]);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
