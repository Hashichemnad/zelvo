using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Api.ServiceDesk;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Service cards in bulk (module 2: "bulk import and export of service cards through Excel and the
/// API"), through the E1 bulk framework (CSV, Excel and JSON over the API). One row per service.
/// Lists of documents, steps, FAQ entries and partners are JSON cells, as exported. An import
/// never publishes: new codes become drafts and existing services get a working version, which
/// goes through review like any change (four-eyes). Nothing changes while any row has a problem.
/// </summary>
internal sealed class ServicesBulkType(ZelvoDbContext db, CurrentUser user, IAuditLog audit) : IBulkType
{
    private const string Code = "code";
    private const string NameEn = "nameEn";
    private const string NameAr = "nameAr";
    private const string SummaryEn = "summaryEn";
    private const string SummaryAr = "summaryAr";
    private const string DescriptionEn = "descriptionEn";
    private const string DescriptionAr = "descriptionAr";
    private const string State = "state";
    private const string Catalogs = "catalogs";
    private const string Categories = "categories";
    private const string Parent = "parent";
    private const string Department = "department";
    private const string Owner = "owner";
    private const string Type = "type";
    private const string ExternalUrl = "externalUrl";
    private const string Form = "form";
    private const string Paid = "paid";
    private const string Amount = "amountAed";
    private const string FeeNotesEn = "feeNotesEn";
    private const string FeeNotesAr = "feeNotesAr";
    private const string Days = "days";
    private const string Channels = "channels";
    private const string Audiences = "audiences";
    private const string Keywords = "keywords";
    private const string Icon = "icon";
    private const string Access = "access";
    private const string AccessRoles = "accessRoles";
    private const string OnBehalfRoles = "onBehalfRoles";
    private const string Related = "related";
    private const string Prerequisites = "prerequisites";
    private const string Documents = "documents";
    private const string Steps = "steps";
    private const string Faq = "faq";
    private const string Partners = "partners";

    public string Key => "services";

    public string ExportPermission => Permissions.CatalogManage;

    public string ImportPermission => Permissions.CatalogManage;

    public IReadOnlyList<BulkColumn> Columns { get; } =
    [
        new(Code, true, "Service code (2–30 capital letters, digits and hyphens); matches the row to an existing service."),
        new(NameEn, false, "Name in English."),
        new(NameAr, false, "Name in Arabic."),
        new(SummaryEn, false, "One line for lists, in English."),
        new(SummaryAr, false, "One line for lists, in Arabic."),
        new(DescriptionEn, false, "Description in English (Markdown)."),
        new(DescriptionAr, false, "Description in Arabic (Markdown)."),
        new(State, false, "Export only: Draft, UnderReview, Active, Suspended or Retired. Ignored on import (imports never publish)."),
        new(Catalogs, false, "Catalog keys separated by semicolons."),
        new(Categories, false, "Category paths separated by semicolons, levels by ' > ', in English or Arabic."),
        new(Parent, false, "Code of the main service."),
        new(Department, false, "Owning department's name in English or Arabic."),
        new(Owner, false, "Service owner's email address."),
        new(Type, false, "Internal or External."),
        new(ExternalUrl, false, "External services: the provider's HTTPS address."),
        new(Form, false, "Key of the form requesters fill."),
        new(Paid, false, "true for a paid service, false for a free one."),
        new(Amount, false, "Fee in AED, for example 150.00."),
        new(FeeNotesEn, false, "Fee notes in English."),
        new(FeeNotesAr, false, "Fee notes in Arabic."),
        new(Days, false, "Delivery time in working days."),
        new(Channels, false, "Delivery channels separated by semicolons: Portal, Mobile, WhatsApp, Chat, Email, CallCentre, ServiceCentre."),
        new(Audiences, false, "Target audience separated by semicolons: Individuals, Businesses, GovernmentEntities, Employees, Visitors."),
        new(Keywords, false, "Search keywords separated by semicolons."),
        new(Icon, false, "Icon from Zelvo's set, for example laptop."),
        new(Access, false, "Everyone, Public or Restricted. Groups, departments, locations and users of a restricted service are kept from the existing card."),
        new(AccessRoles, false, "Restricted services: role keys separated by semicolons."),
        new(OnBehalfRoles, false, "Role keys separated by semicolons whose holders may request on behalf of others."),
        new(Related, false, "Codes of related services separated by semicolons."),
        new(Prerequisites, false, "Codes of prerequisite services separated by semicolons."),
        new(Documents, false, "Required documents as JSON, as exported: [{\"key\",\"name\":{\"en\",\"ar\"},\"required\",\"fileTypes\",\"maxSizeMb\"}]."),
        new(Steps, false, "Service steps as JSON, as exported: [{\"name\":{\"en\",\"ar\"},\"description\",\"stage\"}]."),
        new(Faq, false, "FAQ as JSON, as exported: [{\"question\":{\"en\",\"ar\"},\"answer\":{\"en\",\"ar\"}}]."),
        new(Partners, false, "Service partners as JSON, as exported: [{\"kind\",\"departmentId\",\"name\":{\"en\",\"ar\"}}]."),
    ];

    public async Task<BulkTable> ExportAsync(CancellationToken cancellationToken)
    {
        var services = await db.CatalogServices.AsNoTracking().OrderBy(s => s.Code).ToListAsync(cancellationToken);
        var versionIds = services.Select(s => s.WorkingVersionId ?? s.PublishedVersionId).Where(v => v is not null).Select(v => v!.Value).ToList();
        var contents = await db.CatalogServiceVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Content, cancellationToken);
        var refs = await Refs.LoadAsync(db, cancellationToken);
        var codes = services.ToDictionary(s => s.Id, s => s.Code);
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var service in services)
        {
            var id = service.WorkingVersionId ?? service.PublishedVersionId;
            var card = ServiceCards.Read(id is { } v ? contents.GetValueOrDefault(v) : null);
            rows.Add([.. Columns.Select(c => c.Key switch
            {
                Code => service.Code,
                NameEn => card.Name.En,
                NameAr => card.Name.Ar,
                SummaryEn => card.Summary.En,
                SummaryAr => card.Summary.Ar,
                DescriptionEn => card.Description.En,
                DescriptionAr => card.Description.Ar,
                State => service.State.ToString(),
                Catalogs => string.Join(';', card.CatalogIds.Select(refs.CatalogKey).Where(k => k is not null)),
                Categories => string.Join(';', card.CategoryIds.Select(refs.CategoryPath).Where(k => k is not null)),
                Parent => card.ParentId is { } p ? codes.GetValueOrDefault(p) : null,
                Department => card.DepartmentId is { } d ? refs.DepartmentName(d) : null,
                Owner => card.OwnerId is { } o ? refs.Email(o) : null,
                Type => card.Type.ToString(),
                ExternalUrl => card.ExternalUrl,
                Form => card.FormId is { } f ? refs.FormKey(f) : null,
                Paid => card.Fee.Paid ? "true" : "false",
                Amount => card.Fee.AmountAed?.ToString("0.00", CultureInfo.InvariantCulture),
                FeeNotesEn => card.Fee.Notes.En,
                FeeNotesAr => card.Fee.Notes.Ar,
                Days => card.Days?.ToString(CultureInfo.InvariantCulture),
                Channels => string.Join(';', card.Channels),
                Audiences => string.Join(';', card.Audiences),
                Keywords => string.Join(';', card.Keywords),
                Icon => card.Icon,
                Access => card.Access.Mode.ToString(),
                AccessRoles => string.Join(';', card.Access.RoleKeys),
                OnBehalfRoles => string.Join(';', card.OnBehalfRoleKeys),
                Related => string.Join(';', card.RelatedServiceIds.Select(r => codes.GetValueOrDefault(r)).Where(r => r is not null)),
                Prerequisites => string.Join(';', card.PrerequisiteServiceIds.Select(r => codes.GetValueOrDefault(r)).Where(r => r is not null)),
                Documents => card.Documents.Count == 0 ? null : JsonSerializer.Serialize(card.Documents.Select(d => d with { TemplateFileId = null }), ServiceCards.Json),
                Steps => card.Steps.Count == 0 ? null : JsonSerializer.Serialize(card.Steps, ServiceCards.Json),
                Faq => card.Faq.Count == 0 ? null : JsonSerializer.Serialize(card.Faq, ServiceCards.Json),
                Partners => card.Partners.Count == 0 ? null : JsonSerializer.Serialize(card.Partners, ServiceCards.Json),
                _ => null,
            })]);
        }

        await audit.RecordAsync(AuditCategory.Admin, "bulk.services.exported", "catalog_service", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = services.Count }, cancellationToken);
        return new BulkTable([.. Columns.Select(c => c.Key)], rows);
    }

    public async Task<BulkImportResult> ImportAsync(BulkTable table, bool dryRun, CancellationToken cancellationToken)
    {
        var errors = new List<BulkImportError>();
        var index = MapHeader(table.Header, errors);
        if (errors.Count > 0)
        {
            return new BulkImportResult(dryRun, false, table.Rows.Count, 0, 0, 0, errors);
        }

        var refs = await Refs.LoadAsync(db, cancellationToken);
        var services = await db.CatalogServices.ToListAsync(cancellationToken);
        var byCode = services.ToDictionary(s => s.Code, StringComparer.Ordinal);
        var rowCodes = table.Rows.Select(r => Cell(r, index, Code)?.ToUpperInvariant()).Where(c => c is not null).ToHashSet(StringComparer.Ordinal);
        // New services in the file get their ids first, so rows can refer to each other.
        var newIds = rowCodes.Where(c => !byCode.ContainsKey(c!)).ToDictionary(c => c!, _ => Guid.CreateVersion7(), StringComparer.Ordinal);
        var policy = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.AttachmentPolicyKey, AttachmentPolicy.Default, p => p.IsValid(), cancellationToken);
        var roles = (await db.Roles.AsNoTracking().Select(r => r.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var versionIds = services.Select(s => s.WorkingVersionId ?? s.PublishedVersionId).Where(v => v is not null).Select(v => v!.Value).ToList();
        var versions = await db.CatalogServiceVersions.Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);

        var plans = new List<(int Row, string Code, CatalogService? Existing, ServiceCard Card)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var rowNumber = i + 2;
            var row = table.Rows[i];
            void Error(string? column, string code, string message) => errors.Add(new(rowNumber, column, code, message));

            var code = Cell(row, index, Code)?.ToUpperInvariant();
            if (!ServiceCards.IsCode(code))
            {
                Error(Code, "code-invalid", "Use 2–30 capital letters, digits and hyphens.");
                continue;
            }

            if (!seen.Add(code!))
            {
                Error(Code, "code-duplicate", "The code is in the file twice.");
                continue;
            }

            var existing = byCode.GetValueOrDefault(code!);
            var baseId = existing?.WorkingVersionId ?? existing?.PublishedVersionId;
            var baseVersion = baseId is { } b ? versions.GetValueOrDefault(b) : null;
            if (baseVersion?.State == ServiceVersionState.UnderReview)
            {
                Error(Code, "service-in-review", "The service is in review; wait for the review first.");
                continue;
            }

            var card = ServiceCards.Read(baseVersion?.Content);
            LocalizedText Text(string en, string ar, LocalizedText current) =>
                index.ContainsKey(en) || index.ContainsKey(ar)
                    ? new LocalizedText(index.ContainsKey(en) ? Cell(row, index, en) : current.En, index.ContainsKey(ar) ? Cell(row, index, ar) : current.Ar)
                    : current;
            IReadOnlyList<string> List(string column) =>
                (Cell(row, index, column) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool Has(string column) => index.ContainsKey(column);

            card = card with
            {
                Name = Text(NameEn, NameAr, card.Name),
                Summary = Text(SummaryEn, SummaryAr, card.Summary),
                Description = Text(DescriptionEn, DescriptionAr, card.Description),
                Fee = card.Fee with { Notes = Text(FeeNotesEn, FeeNotesAr, card.Fee.Notes) },
            };
            if (Has(Catalogs))
            {
                var ids = List(Catalogs).Select(k => (Key: k, Id: refs.FindCatalog(k))).ToList();
                foreach (var missing in ids.Where(x => x.Id is null)) Error(Catalogs, "catalog-unknown", $"No catalog has the key '{missing.Key}'.");
                card = card with { CatalogIds = [.. ids.Where(x => x.Id is not null).Select(x => x.Id!.Value)] };
            }

            if (Has(Categories))
            {
                var ids = List(Categories).Select(p => (Path: p, Id: refs.FindCategory(p))).ToList();
                foreach (var missing in ids.Where(x => x.Id is null)) Error(Categories, "category-unknown", $"No category has the path '{missing.Path}'.");
                card = card with { CategoryIds = [.. ids.Where(x => x.Id is not null).Select(x => x.Id!.Value)] };
            }

            Guid? ServiceId(string c) => byCode.TryGetValue(c, out var s) ? s.Id : newIds.TryGetValue(c, out var n) ? n : null;
            if (Has(Parent))
            {
                var parent = Cell(row, index, Parent)?.ToUpperInvariant();
                var id = string.IsNullOrEmpty(parent) ? null : ServiceId(parent);
                if (!string.IsNullOrEmpty(parent) && id is null) Error(Parent, "service-unknown", $"No service has the code '{parent}'.");
                card = card with { ParentId = id };
            }

            if (Has(Department))
            {
                var name = Cell(row, index, Department);
                var id = string.IsNullOrEmpty(name) ? null : refs.FindDepartment(name);
                if (!string.IsNullOrEmpty(name) && id is null) Error(Department, "department-unknown", $"No department is called '{name}'.");
                card = card with { DepartmentId = id };
            }

            if (Has(Owner))
            {
                var email = Cell(row, index, Owner);
                var id = string.IsNullOrEmpty(email) ? null : refs.FindUser(email);
                if (!string.IsNullOrEmpty(email) && id is null) Error(Owner, "user-unknown", $"No user has the email address '{email}'.");
                card = card with { OwnerId = id };
            }

            if (Has(Type))
            {
                if (Enum.TryParse<ServiceType>(Cell(row, index, Type), true, out var type)) card = card with { Type = type };
                else if (!string.IsNullOrEmpty(Cell(row, index, Type))) Error(Type, "type-invalid", "Use Internal or External.");
            }

            if (Has(ExternalUrl)) card = card with { ExternalUrl = Cell(row, index, ExternalUrl) };
            if (Has(Form))
            {
                var key = Cell(row, index, Form);
                var id = string.IsNullOrEmpty(key) ? null : refs.FindForm(key);
                if (!string.IsNullOrEmpty(key) && id is null) Error(Form, "form-unknown", $"No form has the key '{key}'.");
                card = card with { FormId = id };
            }

            if (Has(Paid))
            {
                var paid = Cell(row, index, Paid);
                if (bool.TryParse(paid, out var p)) card = card with { Fee = card.Fee with { Paid = p } };
                else if (!string.IsNullOrEmpty(paid)) Error(Paid, "paid-invalid", "Use true or false.");
            }

            if (Has(Amount))
            {
                var amount = Cell(row, index, Amount);
                if (string.IsNullOrEmpty(amount)) card = card with { Fee = card.Fee with { AmountAed = null } };
                else if (decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var a)) card = card with { Fee = card.Fee with { AmountAed = a } };
                else Error(Amount, "amount-invalid", "Write the amount like 150.00.");
            }

            if (Has(Days))
            {
                var days = Cell(row, index, Days);
                if (string.IsNullOrEmpty(days)) card = card with { Days = null };
                else if (int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var d)) card = card with { Days = d };
                else Error(Days, "days-invalid", "Write a whole number of working days.");
            }

            if (Has(Channels)) card = card with { Channels = Enums<DeliveryChannel>(List(Channels), Channels, Error) };
            if (Has(Audiences)) card = card with { Audiences = Enums<TargetAudience>(List(Audiences), Audiences, Error) };
            if (Has(Keywords)) card = card with { Keywords = List(Keywords) };
            if (Has(Icon)) card = card with { Icon = Cell(row, index, Icon) };
            if (Has(Access))
            {
                if (Enum.TryParse<ServiceAccessMode>(Cell(row, index, Access), true, out var mode)) card = card with { Access = card.Access with { Mode = mode } };
                else if (!string.IsNullOrEmpty(Cell(row, index, Access))) Error(Access, "access-invalid", "Use Everyone, Public or Restricted.");
            }

            if (Has(AccessRoles))
            {
                var keys = List(AccessRoles);
                foreach (var unknown in keys.Where(k => !roles.Contains(k))) Error(AccessRoles, "role-unknown", $"No role has the key '{unknown}'.");
                card = card with { Access = card.Access with { RoleKeys = keys } };
            }

            if (Has(OnBehalfRoles))
            {
                var keys = List(OnBehalfRoles);
                foreach (var unknown in keys.Where(k => !roles.Contains(k))) Error(OnBehalfRoles, "role-unknown", $"No role has the key '{unknown}'.");
                card = card with { OnBehalfRoleKeys = keys };
            }

            if (Has(Related)) card = card with { RelatedServiceIds = Codes(List(Related), Related) };
            if (Has(Prerequisites)) card = card with { PrerequisiteServiceIds = Codes(List(Prerequisites), Prerequisites) };
            if (Has(Documents))
            {
                var parsed = Json<List<ServiceDocument>>(Documents);
                // Templates uploaded to the service are kept by document key.
                card = card with { Documents = parsed is null ? card.Documents : [.. parsed.Select(d => d with { TemplateFileId = card.Documents.FirstOrDefault(x => x.Key == d.Key)?.TemplateFileId })] };
            }

            if (Has(Steps)) card = card with { Steps = Json<List<ServiceStep>>(Steps) ?? card.Steps };
            if (Has(Faq)) card = card with { Faq = Json<List<ServiceFaq>>(Faq) ?? card.Faq };
            if (Has(Partners)) card = card with { Partners = Json<List<ServicePartner>>(Partners) ?? card.Partners };

            card = ServiceCards.Normalise(card);
            foreach (var problem in ServiceCards.CheckShape(card, policy.AllowedExtensions))
            {
                Error(problem.Field, problem.Code, problem.Message);
            }

            plans.Add((rowNumber, code!, existing, card));

            IReadOnlyList<Guid> Codes(IReadOnlyList<string> list, string column)
            {
                var ids = new List<Guid>();
                foreach (var c in list.Select(c => c.ToUpperInvariant()))
                {
                    if (ServiceId(c) is { } id) ids.Add(id);
                    else Error(column, "service-unknown", $"No service has the code '{c}'.");
                }

                return ids;
            }

            T? Json<T>(string column)
                where T : class
            {
                var raw = Cell(row, index, column);
                if (string.IsNullOrEmpty(raw))
                {
                    return Activator.CreateInstance<T>();
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(raw, ServiceCards.Json);
                }
                catch (JsonException)
                {
                    Error(column, "json-invalid", "The cell is not JSON in the exported shape.");
                    return null;
                }
            }
        }

        var created = plans.Count(p => p.Existing is null);
        var updated = plans.Count - created;
        if (errors.Count > 0 || dryRun)
        {
            return new BulkImportResult(dryRun, false, table.Rows.Count, created, updated, 0, errors);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (_, code, existing, card) in plans)
        {
            if (existing is null)
            {
                var service = new CatalogService
                {
                    Id = newIds[code],
                    Code = code,
                    Name = card.Name.HasAnyValue ? card.Name : new LocalizedText(code, null),
                    ParentId = card.ParentId,
                    CatalogIds = [.. card.CatalogIds],
                    CategoryIds = [.. card.CategoryIds],
                    EditorCatalogIds = [.. card.CatalogIds],
                    CreatedById = user.Account?.Id,
                };
                var version = new CatalogServiceVersion { ServiceId = service.Id, Number = 1, Content = ServiceCards.Write(card), CreatedById = user.Account?.Id, LastEditedById = user.Account?.Id, ChangeNote = "Imported" };
                service.WorkingVersionId = version.Id;
                db.CatalogServices.Add(service);
                db.CatalogServiceVersions.Add(version);
            }
            else if (existing.WorkingVersionId is { } workingId && versions.TryGetValue(workingId, out var working))
            {
                working.Content = ServiceCards.Write(card);
                working.LastEditedById = user.Account?.Id;
                working.UpdatedAt = now;
                existing.EditorCatalogIds = [.. card.CatalogIds.Union(existing.PublishedVersionId is null ? [] : existing.CatalogIds)];
                existing.UpdatedAt = now;
                if (existing.PublishedVersionId is null)
                {
                    ServiceEndpoints.CopyPublished(existing, card);
                }
            }
            else
            {
                var number = (await db.CatalogServiceVersions.Where(v => v.ServiceId == existing.Id).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
                var version = new CatalogServiceVersion { ServiceId = existing.Id, Number = number, Content = ServiceCards.Write(card), CreatedById = user.Account?.Id, LastEditedById = user.Account?.Id, ChangeNote = "Imported" };
                db.CatalogServiceVersions.Add(version);
                existing.WorkingVersionId = version.Id;
                existing.EditorCatalogIds = [.. card.CatalogIds.Union(existing.CatalogIds)];
                existing.UpdatedAt = now;
            }
        }

        audit.Add(AuditCategory.Admin, "bulk.services.imported", "catalog_service", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["created"] = created, ["updated"] = updated });
        await db.SaveChangesAsync(cancellationToken);
        return new BulkImportResult(false, true, table.Rows.Count, created, updated, 0, []);
    }

    private static List<T> Enums<T>(IReadOnlyList<string> values, string column, Action<string?, string, string> error)
        where T : struct, Enum
    {
        var list = new List<T>();
        foreach (var value in values)
        {
            if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) list.Add(parsed);
            else error(column, "value-invalid", $"'{value}' is not one of {string.Join(", ", Enum.GetNames<T>())}.");
        }

        return list;
    }

    private Dictionary<string, int> MapHeader(IReadOnlyList<string> header, List<BulkImportError> errors)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var column = Columns.FirstOrDefault(c => string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase));
            if (column is null)
            {
                errors.Add(new(1, name, "column-unknown", $"Unknown column '{name}'."));
            }
            else if (!index.TryAdd(column.Key, i))
            {
                errors.Add(new(1, column.Key, "column-duplicate", $"The column '{column.Key}' is there twice."));
            }
        }

        if (!index.ContainsKey(Code))
        {
            errors.Add(new(1, Code, "column-required", "The column 'code' is required."));
        }

        // The state is shown in exports only.
        index.Remove(State);
        return index;
    }

    private static string? Cell(IReadOnlyList<string?> row, Dictionary<string, int> index, string column)
    {
        if (!index.TryGetValue(column, out var i) || i >= row.Count || row[i] is not { } raw)
        {
            return null;
        }

        var value = raw.Trim();
        value = value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;
        return value.Length == 0 ? null : value;
    }

    /// <summary>Names and keys of what cards refer to.</summary>
    private sealed class Refs
    {
        private Dictionary<Guid, string> _catalogs = [];
        private TreeIndex<Category> _categories = null!;
        private List<Department> _departments = [];
        private Dictionary<Guid, string?> _emails = [];
        private Dictionary<Guid, string> _forms = [];

        public static async Task<Refs> LoadAsync(ZelvoDbContext db, CancellationToken cancellationToken) => new()
        {
            _catalogs = await db.ServiceCatalogs.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Key, cancellationToken),
            _categories = new TreeIndex<Category>(await db.Categories.AsNoTracking().ToListAsync(cancellationToken)),
            _departments = await db.Departments.AsNoTracking().ToListAsync(cancellationToken),
            _emails = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken),
            _forms = await db.Forms.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Key, cancellationToken),
        };

        public string? CatalogKey(Guid id) => _catalogs.GetValueOrDefault(id);

        public Guid? FindCatalog(string key) => _catalogs.FirstOrDefault(c => c.Value == key) is { Value: not null } c ? c.Key : null;

        public string? CategoryPath(Guid id) => _categories.Find(id) is { } node ? _categories.PathOf(node).En ?? _categories.PathOf(node).Ar : null;

        public Guid? FindCategory(string path)
        {
            var wanted = Normalise(path);
            return _categories.Nodes.FirstOrDefault(n => Normalise(_categories.PathOf(n).En) == wanted || Normalise(_categories.PathOf(n).Ar) == wanted)?.Id;
        }

        public string? DepartmentName(Guid id) => _departments.FirstOrDefault(d => d.Id == id) is { } d ? d.Name.En ?? d.Name.Ar : null;

        public Guid? FindDepartment(string name) =>
            _departments.FirstOrDefault(d => string.Equals(d.Name.En, name, StringComparison.OrdinalIgnoreCase) || d.Name.Ar == name)?.Id;

        public string? Email(Guid id) => _emails.GetValueOrDefault(id);

        public Guid? FindUser(string email) => _emails.FirstOrDefault(u => string.Equals(u.Value, email, StringComparison.OrdinalIgnoreCase)) is { Value: not null } u ? u.Key : null;

        public string? FormKey(Guid id) => _forms.GetValueOrDefault(id);

        public Guid? FindForm(string key) => _forms.FirstOrDefault(f => f.Value == key) is { Value: not null } f ? f.Key : null;

        private static string? Normalise(string? path) =>
            path is null ? null : string.Join('>', path.Split(['>', '›'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}
