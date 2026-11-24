using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Forms;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task C1: departments with the demo users in them (manager, location, employee number and phone
/// on the requester), a "Requester details" field set filled from the profile, a published default
/// incident form that uses it, and a laptop form on the Laptops category with the module 2 example
/// rule: "Monitor size" is shown and required only when "Add a monitor" is ticked.
/// </summary>
internal sealed class DemoFormsSeeder : IDemoSeeder
{
    public string Key => "c1-forms";

    public int Order => 130;

    private static LocalizedText T(string en, string ar) => new(en, ar);

    private static FormField Text(string key, string en, string ar, Func<FormField, FormField>? more = null)
    {
        var field = new FormField { Key = key, Type = FieldType.Text, Label = T(en, ar) };
        return more?.Invoke(field) ?? field;
    }

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;

        // Departments, and the demo users in them.
        var it = new Department { Name = T("Information technology", "تقنية المعلومات") };
        var finance = new Department { Name = T("Finance", "المالية") };
        var service = government
            ? new Department { Name = T("Customer happiness", "إسعاد المتعاملين") }
            : new Department { Name = T("Sales", "المبيعات") };
        db.Departments.AddRange(it, finance, service);

        var users = await db.Users.ToListAsync(cancellationToken).ConfigureAwait(false);
        var byPersona = context.Organisation.Users.ToDictionary(u => u.Persona, u => users.SingleOrDefault(a => a.Email == context.Organisation.UsernameOf(u)));
        var location = await db.Locations.AsNoTracking().Where(l => l.Depth == 2).OrderBy(l => l.Position).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var number = 1000;
        foreach (var (persona, account) in byPersona)
        {
            if (account is null)
            {
                continue;
            }

            account.DepartmentId = persona is "agent" or "lead" or "admin" ? it.Id : persona == "approver" ? finance.Id : service.Id;
            account.EmployeeId = $"{(government ? "AJ" : "DC")}-{++number}";
            account.LocationId = location;
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (byPersona.GetValueOrDefault("requester") is { } requester)
        {
            requester.ManagerId = byPersona.GetValueOrDefault("approver")?.Id;
            // A fictional number in the UAE mobile range for demos.
            requester.Phone = government ? "+971500000101" : "+971500000201";
        }

        // A field set shared by both forms.
        var details = new FieldSet
        {
            Key = "requester-details",
            Name = T("Requester details", "بيانات مقدّم الطلب"),
            Description = T("Who the ticket is for, filled in from their profile.", "بيانات صاحب التذكرة، تُعبّأ من ملفه الشخصي."),
            DraftSchema = FormJson.Write(new FieldSetSchema
            {
                Fields =
                [
                    Text("department", "Department", "الإدارة", f => f with { ProfileDefault = ProfileSource.Department, ReadOnly = true }),
                    Text("employeeNumber", "Employee number", "الرقم الوظيفي", f => f with { ProfileDefault = ProfileSource.EmployeeId }),
                    Text("mobile", "Mobile number", "رقم الهاتف المتحرك", f => f with
                    {
                        ProfileDefault = ProfileSource.Phone,
                        Validation = new FieldValidation { Format = FieldFormat.Phone },
                        Help = T("We call this number if we need more details.", "نتصل بهذا الرقم إذا احتجنا إلى مزيد من التفاصيل."),
                    }),
                ],
            }),
            HasUnpublishedChanges = false,
        };
        var detailsVersion = new FieldSetVersion { FieldSetId = details.Id, Number = 1, Schema = details.DraftSchema };
        db.FieldSets.Add(details);
        db.FieldSetVersions.Add(detailsVersion);

        var general = Form("general-incident", T("General incident", "بلاغ عام"),
            T("A few questions that help us respond faster.", "أسئلة قليلة تساعدنا على الاستجابة بشكل أسرع."),
            new FormSchema
            {
                Elements =
                [
                    new FormElement { Type = FormElementType.FieldSet, FieldSetId = details.Id },
                    new FormElement { Type = FormElementType.Field, Field = new FormField
                    {
                        Key = "started", Type = FieldType.Date, Label = T("When did it start?", "متى بدأت المشكلة؟"),
                    } },
                    new FormElement { Type = FormElementType.Field, Field = new FormField
                    {
                        Key = "affected", Type = FieldType.Radio, Label = T("Who is affected?", "من المتأثر؟"), Required = true,
                        Options =
                        [
                            new FieldOption("me", T("Only me", "أنا فقط")),
                            new FieldOption("team", T("My team", "فريقي")),
                            new FieldOption("everyone", T("Everyone", "الجميع")),
                        ],
                    } },
                ],
            });

        var laptop = Form("laptop-problem", T("Laptop problem", "مشكلة في الحاسوب المحمول"),
            T("Tell us about the laptop and what you need.", "أخبرنا عن الحاسوب المحمول وما تحتاج إليه."),
            new FormSchema
            {
                Elements =
                [
                    new FormElement { Type = FormElementType.FieldSet, FieldSetId = details.Id },
                    new FormElement { Type = FormElementType.Field, Field = Text("assetTag", "Asset tag", "رقم الأصل", f => f with
                    {
                        Required = true,
                        Validation = new FieldValidation { Pattern = "[A-Z]{2}-[0-9]{4}" },
                        Placeholder = T("For example LT-0042", "مثال: LT-0042"),
                        Help = T("On the sticker under the laptop.", "على الملصق أسفل الحاسوب."),
                    }) },
                    new FormElement { Type = FormElementType.Field, Field = new FormField { Key = "addMonitor", Type = FieldType.Checkbox, Label = T("Add a monitor", "إضافة شاشة") } },
                    new FormElement { Type = FormElementType.Field, Field = new FormField
                    {
                        Key = "monitorSize", Type = FieldType.Dropdown, Label = T("Monitor size", "حجم الشاشة"),
                        Options = [new FieldOption("24", T("24 inch", "24 بوصة")), new FieldOption("27", T("27 inch", "27 بوصة"))],
                        Rules =
                        [
                            new FieldRule(RuleAction.Show, RuleMatch.All, [new RuleCondition("addMonitor", ConditionOperator.Checked)]),
                            new FieldRule(RuleAction.Require, RuleMatch.All, [new RuleCondition("addMonitor", ConditionOperator.Checked)]),
                        ],
                    } },
                    new FormElement { Type = FormElementType.Field, Field = new FormField
                    {
                        Key = "photo", Type = FieldType.File, Label = T("Photo of the problem", "صورة المشكلة"),
                        Validation = new FieldValidation { FileTypes = ["jpg", "jpeg", "png"], MaxFiles = 2 },
                    } },
                ],
            });

        db.FormBindings.Add(new FormBinding { RecordType = TicketType.Incident, CategoryId = null, FormId = general.Form.Id });
        var laptops = await db.Categories.AsNoTracking().Where(c => c.Name.En == "Laptops").Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (laptops is { } laptopsId)
        {
            db.FormBindings.Add(new FormBinding { RecordType = TicketType.Incident, CategoryId = laptopsId, FormId = laptop.Form.Id });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Published versions point back at their forms; set once both rows exist.
        foreach (var (form, version) in new[] { general, laptop })
        {
            form.PublishedVersionId = version.Id;
            form.State = FormState.Published;
        }

        details.PublishedVersionId = detailsVersion.Id;
        details.State = FormState.Published;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        (Form Form, FormVersion Version) Form(string key, LocalizedText name, LocalizedText description, FormSchema schema)
        {
            var json = FormJson.Write(schema);
            var form = new Form { Key = key, Name = name, Description = description, DraftSchema = json, HasUnpublishedChanges = false };
            var version = new FormVersion { FormId = form.Id, Number = 1, Schema = json };
            db.Forms.Add(form);
            db.FormVersions.Add(version);
            return (form, version);
        }
    }
}
