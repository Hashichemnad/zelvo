using System.Text.Json;
using System.Text.Json.Nodes;
using Zelvo.Api.Apps;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Workflows;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task K3: each sample organisation's <b>HR app</b> (<c>hr</c>, "Human resources" / "الموارد
/// البشرية"), installed through the same package installer an approved import uses: the data list
/// "Leave types", the record type "Leave request" (<c>LVE-</c>) with its states, its form (leave
/// type from the data list, dates, notes), the custom role "HR officer", list views, menu entries
/// and a portal entry. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoAppBuilderSeeder : IDemoSeeder
{
    public string Key => "k3-app-builder";

    public int Order => 296;

    /// <summary>The HR app as a package.</summary>
    internal static AppPackage HrPackage()
    {
        static PackageText T(string en, string ar) => new(en, ar);
        var fields = new List<FormField>
        {
            new() { Key = "leaveType", Type = FieldType.Dropdown, Label = new LocalizedText("Leave type", "نوع الإجازة"), Required = true, DataList = "leave-types" },
            new() { Key = "startDate", Type = FieldType.Date, Label = new LocalizedText("First day", "اليوم الأول"), Required = true },
            new() { Key = "endDate", Type = FieldType.Date, Label = new LocalizedText("Last day", "اليوم الأخير"), Required = true },
            new() { Key = "notes", Type = FieldType.Text, Multiline = true, Label = new LocalizedText("Notes", "ملاحظات") },
        };
        return new AppPackage
        {
            Format = AppPackage.CurrentFormat,
            App = new PackageApp
            {
                Key = "hr",
                Name = T("Human resources", "الموارد البشرية"),
                Description = T("Leave requests and other HR processes.", "طلبات الإجازة وإجراءات الموارد البشرية الأخرى."),
                Icon = "people",
                Definition = new AppDefinition
                {
                    ListViews =
                    [
                        new AppListView { Key = "open", Name = T("Open leave requests", "طلبات الإجازة المفتوحة"), RecordType = "leave", Open = true },
                        new AppListView { Key = "mine", Name = T("Assigned to me", "المسندة إليّ"), RecordType = "leave", Open = true, Assignee = "me" },
                    ],
                    MenuEntries =
                    [
                        new AppMenuEntry { Key = "open", Label = T("Open leave requests", "طلبات الإجازة المفتوحة"), Target = "records", RecordType = "leave", View = "open" },
                        new AppMenuEntry { Key = "mine", Label = T("Assigned to me", "المسندة إليّ"), Target = "records", RecordType = "leave", View = "mine" },
                        new AppMenuEntry { Key = "new", Label = T("Log a leave request", "تسجيل طلب إجازة"), Target = "new", RecordType = "leave" },
                    ],
                    PortalEntries =
                    [
                        new AppPortalEntry
                        {
                            Key = "request-leave", Label = T("Request leave", "طلب إجازة"),
                            Description = T("Annual, sick and other leave, approved by human resources.", "الإجازة السنوية والمرضية وغيرها، باعتماد الموارد البشرية."),
                            RecordType = "leave",
                        },
                    ],
                },
            },
            DataLists =
            [
                new PackageDataList
                {
                    Key = "leave-types",
                    Name = T("Leave types", "أنواع الإجازات"),
                    Columns = [new DataListColumn("days", T("Days a year", "الأيام في السنة"))],
                    Items =
                    [
                        new PackageDataListItem { Key = "annual", Label = T("Annual leave", "إجازة سنوية"), Values = new() { ["days"] = "30" } },
                        new PackageDataListItem { Key = "sick", Label = T("Sick leave", "إجازة مرضية"), Values = new() { ["days"] = "90" } },
                        new PackageDataListItem { Key = "emergency", Label = T("Emergency leave", "إجازة طارئة"), Values = new() { ["days"] = "5" } },
                        new PackageDataListItem { Key = "hajj", Label = T("Hajj leave", "إجازة الحج"), Values = new() { ["days"] = "30" } },
                        new PackageDataListItem { Key = "study", Label = T("Study leave", "إجازة دراسية"), Values = new() { ["days"] = "10" } },
                    ],
                },
            ],
            RecordTypes =
            [
                new PackageRecordType
                {
                    Key = "leave",
                    Name = T("Leave request", "طلب إجازة"),
                    Description = T("Ask for leave; human resources checks your balance and approves it.", "اطلب إجازة؛ تتحقق الموارد البشرية من رصيدك وتعتمدها."),
                    NumberPrefix = "LVE",
                    PortalEntry = true,
                    DefaultPriority = 4,
                    States =
                    [
                        new PackageState("submitted", T("Submitted", "مقدّم"), RecordStateCategory.New),
                        new PackageState("checking", T("Checking the balance", "التحقق من الرصيد"), RecordStateCategory.InProgress),
                        new PackageState("waiting", T("Waiting for documents", "بانتظار المستندات"), RecordStateCategory.OnHold),
                        new PackageState("approved", T("Approved", "معتمد"), RecordStateCategory.Resolved),
                        new PackageState("closed", T("Closed", "مغلق"), RecordStateCategory.Closed),
                    ],
                    GrantRoles = [BuiltInRoles.Agent, BuiltInRoles.TeamLead, "hr-officer"],
                },
            ],
            Roles =
            [
                new PackageRole
                {
                    Key = "hr-officer", Name = T("HR officer", "موظف الموارد البشرية"), RequiresMfa = true,
                    Permissions = ["records.leave.read", "records.leave.edit", "records.leave.assign"],
                },
            ],
            Forms =
            [
                new PackageForm
                {
                    Key = "leave-request",
                    Name = T("Leave request", "طلب إجازة"),
                    Fields = JsonSerializer.SerializeToNode(fields, FormJson.Options)!.AsArray(),
                },
            ],
            FormBindings = [new PackageFormBinding("leave", "leave-request", null)],
            Rules =
            [
                new PackageRule
                {
                    Name = T("Tell the requester their leave request arrived", "إبلاغ مقدّم الطلب باستلام طلب الإجازة"),
                    Trigger = WorkflowTrigger.TicketCreated,
                    RecordType = "leave",
                    Conditions = [],
                    Actions = JsonSerializer.SerializeToNode(new List<Zelvo.Workflows.Rules.WorkflowAction>
                    {
                        new Zelvo.Workflows.Rules.NotifyAction
                        {
                            Recipients = ["requester"],
                            Subject = new LocalizedText("Your leave request {{ticket.number}} arrived", "وصل طلب الإجازة {{ticket.number}}"),
                            Body = new LocalizedText(
                                "Dear {{recipient.name}}, human resources received your leave request {{ticket.number}} and will check your balance.",
                                "السيد/السيدة {{recipient.name}}، تسلّمت الموارد البشرية طلب الإجازة {{ticket.number}} وستتحقق من رصيدكم."),
                            Email = false,
                        },
                    }, Zelvo.Workflows.Rules.RuleDocuments.Json)!.AsArray(),
                },
            ],
        };
    }

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var audit = context.Get<IAuditLog>();
        var package = HrPackage();
        var plan = await AppPackages.PlanAsync(db, settings, package, null, cancellationToken).ConfigureAwait(false);
        if (!plan.Valid || plan.Conflicts.Count > 0)
        {
            throw new InvalidOperationException($"The demo HR app does not install: {string.Join("; ", plan.Problems.Select(p => $"{p.Path}: {p.Message}"))}");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AppPackages.InstallAsync(db, settings, audit, package, new Dictionary<string, string>(), null, null, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
