using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Identity;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Integrations.Bulk;

/// <summary>The import could not be applied because of a guard or the sign-in service.</summary>
internal sealed class BulkImportRefusedException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;
}

/// <summary>
/// Users in bulk. Rows are matched to users by email. Empty cells leave the value as it is; new
/// users start as active requesters. New people are also created in the tenant's Keycloak realm
/// (no password: they sign in with an email code, UAE PASS or "Forgot password"). The same rules
/// as the user screens apply: field rules, unique Emirates IDs, and the tenant always keeps an
/// active tenant administrator; nobody disables themselves.
/// </summary>
internal sealed partial class UsersBulkType(
    ZelvoDbContext db, CurrentUser user, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity,
    IAuditLog audit, ILogger<UsersBulkType> logger) : IBulkType
{
    private const string Email = "email";
    private const string DisplayName = "displayName";
    private const string EmiratesId = "emiratesId";
    private const string Roles = "roles";
    private const string Status = "status";
    private const string Department = "department";
    private const string Manager = "manager";
    private const string Location = "location";
    private const string EmployeeId = "employeeId";
    private const string Phone = "phone";
    private const string Clear = "-";

    public string Key => "users";

    public string ExportPermission => Permissions.UsersRead;

    public string ImportPermission => Permissions.UsersManage;

    public IReadOnlyList<BulkColumn> Columns { get; } =
    [
        new(Email, true, "Email address; matches the row to an existing user."),
        new(DisplayName, false, "Name shown to other users (up to 200 characters)."),
        new(EmiratesId, false, "Emirates ID number, 15 digits starting with 784 (dashes allowed), to match UAE PASS sign-ins."),
        new(Roles, false, "Role keys separated by semicolons, for example agent;approver. Replaces the user's roles."),
        new(Status, false, "Active or Disabled."),
        new(Department, false, "Task C1: department name in English or Arabic; '-' removes it."),
        new(Manager, false, "Task C1: the manager's email address; '-' removes it."),
        new(Location, false, "Task C1: location path in English or Arabic, levels separated by ' › ' or ' > ', for example Ajman > Main building; '-' removes it."),
        new(EmployeeId, false, "Task C1: employee number (up to 50 characters); '-' removes it."),
        new(Phone, false, "Task C1: phone number, for example +971501234567; '-' removes it."),
    ];

    public async Task<BulkTable> ExportAsync(CancellationToken cancellationToken)
    {
        var columns = Columns.Select(c => c.Key)
            .Where(c => c == Roles || FieldFilter.IsVisible(user, PermissionCatalog.Users, c))
            .ToList();
        var users = await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(r => r.Role)
            .OrderBy(u => u.Email).ThenBy(u => u.Id).ToListAsync(cancellationToken);
        var refs = await ProfileNames.LoadAsync(db, cancellationToken);
        var emails = users.ToDictionary(u => u.Id, u => u.Email);
        var rows = users.Select(u => (IReadOnlyList<string?>)[.. columns.Select(c => c switch
        {
            Email => u.Email,
            DisplayName => u.DisplayName,
            EmiratesId => u.EmiratesId,
            Roles => string.Join(';', u.Roles.Select(r => r.Role!.Key).Order(StringComparer.Ordinal)),
            Department => u.DepartmentId is { } d ? refs.DepartmentName(d) : null,
            Manager => u.ManagerId is { } m ? emails.GetValueOrDefault(m) : null,
            Location => u.LocationId is { } l ? refs.LocationPath(l) : null,
            EmployeeId => u.EmployeeId,
            Phone => u.Phone,
            _ => u.Status.ToString(),
        })]).ToList();

        var personal = columns.Where(c => c is Email or EmiratesId or Phone).ToArray();
        await audit.RecordAsync(personal.Length > 0 ? AuditCategory.View : AuditCategory.Admin, "bulk.users.exported", "user_account", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = users.Count, ["fields"] = columns.ToArray(), ["personalData"] = personal },
            cancellationToken);
        return new BulkTable(columns, rows);
    }

    public async Task<BulkImportResult> ImportAsync(BulkTable table, bool dryRun, CancellationToken cancellationToken)
    {
        var errors = new List<BulkImportError>();
        var index = MapHeader(table.Header, errors);
        if (errors.Count > 0)
        {
            return new BulkImportResult(dryRun, false, table.Rows.Count, 0, 0, 0, errors);
        }

        // Field rules apply to imports as to single changes.
        var used = new[] { DisplayName, EmiratesId, Status, Department, Manager, Location, EmployeeId, Phone }
            .Where(c => index.ContainsKey(c) && table.Rows.Any(r => !string.IsNullOrWhiteSpace(Cell(r, index, c))));
        var refused = FieldFilter.NotEditable(user, PermissionCatalog.Users, used);
        if (refused.Count > 0)
        {
            return new BulkImportResult(dryRun, false, table.Rows.Count, 0, 0, 0,
                [.. refused.Select(f => new BulkImportError(1, f, "field-read-only", $"You may not change {f}."))]);
        }

        var roles = await db.Roles.ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);
        var names = await ProfileNames.LoadAsync(db, cancellationToken);
        var existing = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).ToListAsync(cancellationToken);
        var byEmail = existing.Where(u => u.Email is not null).GroupBy(u => u.Email!.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.ToList());
        var emiratesIds = existing.Where(u => u.EmiratesId is not null).ToDictionary(u => u.EmiratesId!, u => u.Id);

        var plans = new List<RowPlan>();
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmiratesIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var rowNumber = i + 2;
            var row = table.Rows[i];
            var plan = new RowPlan(rowNumber);

            var email = Cell(row, index, Email);
            if (string.IsNullOrEmpty(email))
            {
                errors.Add(new(rowNumber, Email, "email-required", "Every row needs an email address."));
                continue;
            }

            if (email.Length > UserAccount.EmailMaxLength || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            {
                errors.Add(new(rowNumber, Email, "email-invalid", "Not a valid email address."));
                continue;
            }

            if (!seenEmails.Add(email))
            {
                errors.Add(new(rowNumber, Email, "email-duplicate", "This email address is in the file more than once."));
                continue;
            }

            plan.Email = email;
            if (byEmail.TryGetValue(email.ToUpperInvariant(), out var matches))
            {
                if (matches.Count > 1)
                {
                    errors.Add(new(rowNumber, Email, "email-ambiguous", "More than one user has this email address; change them one by one."));
                    continue;
                }

                plan.Existing = matches[0];
            }

            if (Cell(row, index, DisplayName) is { Length: > 0 } name)
            {
                if (name.Length > UserAccount.DisplayNameMaxLength)
                {
                    errors.Add(new(rowNumber, DisplayName, "display-name-too-long", "Use at most 200 characters."));
                }

                plan.DisplayName = name;
            }

            if (Cell(row, index, EmiratesId) is { Length: > 0 } eid)
            {
                var normalised = eid.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
                if (normalised.Length != UserAccount.EmiratesIdLength || !normalised.StartsWith("784", StringComparison.Ordinal) || !normalised.All(char.IsAsciiDigit))
                {
                    errors.Add(new(rowNumber, EmiratesId, "emirates-id-invalid", "Use the 15-digit Emirates ID number, starting with 784."));
                }
                else if (emiratesIds.TryGetValue(normalised, out var owner) && owner != plan.Existing?.Id)
                {
                    errors.Add(new(rowNumber, EmiratesId, "emirates-id-in-use", "Another user of this tenant has this Emirates ID."));
                }
                else if (seenEmiratesIds.TryGetValue(normalised, out var other))
                {
                    errors.Add(new(rowNumber, EmiratesId, "emirates-id-duplicate", $"Row {other} has the same Emirates ID."));
                }
                else
                {
                    seenEmiratesIds[normalised] = rowNumber;
                    plan.EmiratesId = normalised;
                }
            }

            if (Cell(row, index, Roles) is { Length: > 0 } roleCell)
            {
                var keys = roleCell.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal).ToList();
                var unknown = keys.Where(k => !roles.ContainsKey(k)).ToList();
                if (unknown.Count > 0)
                {
                    errors.Add(new(rowNumber, Roles, "role-unknown", $"Unknown roles: {string.Join(", ", unknown)}."));
                }
                else if (keys.Count == 0)
                {
                    errors.Add(new(rowNumber, Roles, "role-unknown", "List at least one role, or leave the cell empty."));
                }
                else
                {
                    plan.Roles = keys;
                }
            }

            if (Cell(row, index, Status) is { Length: > 0 } statusCell)
            {
                if (Enum.TryParse<UserStatus>(statusCell, ignoreCase: true, out var status) && Enum.IsDefined(status) && !int.TryParse(statusCell, out _))
                {
                    plan.Status = status;
                    if (status == UserStatus.Disabled && plan.Existing is { } self && self.Id == user.Account?.Id)
                    {
                        errors.Add(new(rowNumber, Status, "cannot-disable-self", "You cannot disable your own account."));
                    }
                }
                else
                {
                    errors.Add(new(rowNumber, Status, "status-invalid", "Use Active or Disabled."));
                }
            }

            // Task C1: profile fields, matched by name, path or email.
            string? Ref(string column, Func<string, Guid?> find, string error)
            {
                if (Cell(row, index, column) is not { Length: > 0 } cell)
                {
                    return null;
                }

                if (cell == Clear)
                {
                    return string.Empty;
                }

                if (find(cell) is { } found)
                {
                    return found.ToString();
                }

                errors.Add(new(rowNumber, column, column + "-unknown", error));
                return null;
            }

            plan.Profile = ProfileChange.Parse(
                Ref(Department, names.FindDepartment, "No active department has this name."),
                Ref(Manager, m => byEmail.TryGetValue(m.ToUpperInvariant(), out var found) && found.Count == 1 && found[0].Status == UserStatus.Active ? found[0].Id : null,
                    "No active user has this email address."),
                Ref(Location, names.FindLocation, "No active location has this path."),
                Cell(row, index, EmployeeId) is { Length: > 0 } employee ? (employee == Clear ? string.Empty : employee) : null,
                Cell(row, index, Phone) is { Length: > 0 } phone ? (phone == Clear ? string.Empty : phone) : null);
            if (plan.Profile.Error is { } profileError)
            {
                var column = profileError.Field switch { "departmentId" => Department, "managerId" => Manager, "locationId" => Location, var f => f };
                errors.Add(new(rowNumber, column, column + "-invalid", profileError.Message));
            }

            plans.Add(plan);
        }

        var changes = plans.Select(p => (Plan: p, Changes: p.Existing is null ? 1 : CountChanges(p))).ToList();
        var created = changes.Count(c => c.Plan.Existing is null);
        var updated = changes.Count(c => c.Plan.Existing is not null && c.Changes > 0);
        var unchanged = changes.Count(c => c.Plan.Existing is not null && c.Changes == 0);

        if (errors.Count == 0 && !KeepsActiveAdmin(existing, plans))
        {
            errors.Add(new(1, Roles, "last-tenant-admin", "The tenant needs at least one active tenant administrator."));
        }

        if (dryRun || errors.Count > 0)
        {
            return new BulkImportResult(dryRun, false, table.Rows.Count, created, updated, unchanged, [.. errors.OrderBy(e => e.Row)]);
        }

        // Saved with the changes, in the same transaction.
        audit.Add(AuditCategory.Admin, "bulk.users.imported", "user_account", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["rows"] = table.Rows.Count, ["created"] = created, ["updated"] = updated });
        await ApplyAsync(plans, roles, cancellationToken);
        return new BulkImportResult(false, true, table.Rows.Count, created, updated, unchanged, []);
    }

    private async Task ApplyAsync(List<RowPlan> plans, Dictionary<string, Role> roles, CancellationToken cancellationToken)
    {
        var realm = identity.Value.RealmFor(tenant.Slug!);

        // New people are created in Keycloak first: a person without a Zelvo record is only a
        // requester at their first sign-in, so a failure halfway leaves nothing harmful behind.
        foreach (var plan in plans.Where(p => p.Existing is null))
        {
            var name = plan.DisplayName ?? plan.Email!.Split('@')[0];
            var space = name.IndexOf(' ', StringComparison.Ordinal);
            plan.Subject = await CallKeycloakAsync(() => keycloak.FindOrCreateUserAsync(realm, plan.Email!,
                space > 0 ? name[..space] : name, space > 0 ? name[(space + 1)..] : string.Empty, cancellationToken));
        }

        var subjects = plans.Where(p => p.Subject is not null).Select(p => p.Subject!).ToList();
        var bySubject = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role)
            .Where(u => subjects.Contains(u.Subject)).ToDictionaryAsync(u => u.Subject, StringComparer.Ordinal, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var emiratesIdChanges = new List<(string Subject, string EmiratesId)>();
        var statusChanges = new List<(string Subject, bool Enabled)>();
        foreach (var plan in plans)
        {
            var account = plan.Existing ?? (plan.Subject is { } s && bySubject.TryGetValue(s, out var known) ? known : null);
            if (account is null)
            {
                account = new UserAccount { Subject = plan.Subject!, Email = plan.Email, Status = UserStatus.Active };
                var keys = plan.Roles ?? [BuiltInRoles.Requester];
                foreach (var key in keys)
                {
                    account.Roles.Add(new UserRole { UserId = account.Id, RoleId = roles[key].Id, Role = roles[key] });
                }

                db.Users.Add(account);
            }
            else if (plan.Roles is { } keys)
            {
                account.Roles.RemoveAll(r => !keys.Contains(r.Role!.Key));
                foreach (var key in keys.Where(k => account.Roles.All(r => r.Role!.Key != k)))
                {
                    account.Roles.Add(new UserRole { UserId = account.Id, RoleId = roles[key].Id, Role = roles[key] });
                }
            }

            if (plan.DisplayName is not null)
            {
                account.DisplayName = plan.DisplayName;
            }

            if (plan.Status is { } status)
            {
                // Task F7 (ASVS V7.4.2): existing people turned off or on in Keycloak too.
                if (status != account.Status && db.Entry(account).State != EntityState.Added && Identity.UserEndpoints.HasKeycloakUser(account))
                {
                    statusChanges.Add((account.Subject, status == UserStatus.Active));
                }

                account.Status = status;
            }

            if (plan.Profile is { Fields.Count: > 0 } profile && await profile.ApplyAsync(db, account, cancellationToken) is { } refused)
            {
                throw new BulkImportRefusedException(StatusCodes.Status422UnprocessableEntity, "profile-invalid", $"Row {plan.Row}: {refused.Message}");
            }

            if (plan.EmiratesId is not null && plan.EmiratesId != account.EmiratesId)
            {
                account.EmiratesId = plan.EmiratesId;
                emiratesIdChanges.Add((account.Subject, plan.EmiratesId));
            }

            account.UpdatedAt = now;
        }

        // Saved and copied to Keycloak together: if Keycloak refuses an Emirates ID or a status, nothing changes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var (subject, emiratesId) in emiratesIdChanges)
        {
            await CallKeycloakAsync(async () =>
            {
                await keycloak.SetEmiratesIdAsync(realm, subject, emiratesId, cancellationToken);
                return subject;
            });
        }

        foreach (var (subject, enabled) in statusChanges)
        {
            await CallKeycloakAsync(async () =>
            {
                await keycloak.SetUserEnabledAsync(realm, subject, enabled, cancellationToken);
                return subject;
            });
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<string> CallKeycloakAsync(Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
        {
            LogKeycloakRefused(logger, ex);
            throw new BulkImportRefusedException(StatusCodes.Status502BadGateway, "identity-provider-unavailable",
                "The sign-in service did not accept the import. No users were changed.");
        }
    }

    private static bool KeepsActiveAdmin(List<UserAccount> existing, List<RowPlan> plans)
    {
        var byId = plans.Where(p => p.Existing is not null).ToDictionary(p => p.Existing!.Id);
        var admins = existing.Count(u =>
        {
            var plan = byId.GetValueOrDefault(u.Id);
            var status = plan?.Status ?? u.Status;
            var isAdmin = plan?.Roles is { } keys ? keys.Contains(BuiltInRoles.TenantAdmin) : u.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin);
            return status == UserStatus.Active && isAdmin;
        });
        admins += plans.Count(p => p.Existing is null && p.Status != UserStatus.Disabled && p.Roles?.Contains(BuiltInRoles.TenantAdmin) == true);
        var hadAdmin = existing.Any(u => u.Status == UserStatus.Active && u.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin));
        return admins > 0 || !hadAdmin;
    }

    private static int CountChanges(RowPlan plan)
    {
        var account = plan.Existing!;
        var count = 0;
        count += plan.DisplayName is not null && plan.DisplayName != account.DisplayName ? 1 : 0;
        count += plan.EmiratesId is not null && plan.EmiratesId != account.EmiratesId ? 1 : 0;
        count += plan.Status is { } status && status != account.Status ? 1 : 0;
        count += plan.Profile?.Fields.Count > 0 ? 1 : 0;
        if (plan.Roles is { } keys)
        {
            var current = account.Roles.Select(r => r.Role!.Key).ToHashSet(StringComparer.Ordinal);
            count += current.SetEquals(keys) ? 0 : 1;
        }

        return count;
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
                errors.Add(new(1, name, "column-unknown", $"Unknown column '{name}'. Columns: {string.Join(", ", Columns.Select(c => c.Key))}."));
            }
            else if (!index.TryAdd(column.Key, i))
            {
                errors.Add(new(1, column.Key, "column-duplicate", $"The column '{column.Key}' is there twice."));
            }
        }

        foreach (var required in Columns.Where(c => c.Required && !index.ContainsKey(c.Key)))
        {
            errors.Add(new(1, required.Key, "column-missing", $"The file needs a '{required.Key}' column."));
        }

        return index;
    }

    /// <summary>A cell, trimmed; the apostrophe Zelvo adds against formulas on export is removed.</summary>
    private static string? Cell(IReadOnlyList<string?> row, Dictionary<string, int> index, string column)
    {
        if (!index.TryGetValue(column, out var i) || i >= row.Count || row[i] is not { } raw)
        {
            return null;
        }

        var value = raw.Trim();
        return value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak refused a bulk user import.")]
    private static partial void LogKeycloakRefused(ILogger logger, Exception exception);

    private sealed class RowPlan(int row)
    {
        public int Row { get; } = row;

        public string? Email { get; set; }

        public UserAccount? Existing { get; set; }

        public string? DisplayName { get; set; }

        public string? EmiratesId { get; set; }

        public List<string>? Roles { get; set; }

        public UserStatus? Status { get; set; }

        public ProfileChange? Profile { get; set; }

        public string? Subject { get; set; }
    }
}
