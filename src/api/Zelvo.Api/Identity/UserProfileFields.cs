using Microsoft.EntityFrameworkCore;
using Zelvo.Api.ServiceDesk;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Identity;

/// <summary>A user with a name, for responses.</summary>
internal sealed record PersonRef(Guid Id, string? DisplayName);

/// <summary>A refused profile value.</summary>
internal sealed record ProfileError(string Field, string Message);

/// <summary>Names of departments, managers and locations for user responses (task C1).</summary>
internal sealed class ProfileRefs
{
    private readonly Dictionary<Guid, LocalizedText> _departments;
    private readonly Dictionary<Guid, string?> _managers;
    private readonly TreeIndex<Location> _locations;

    private ProfileRefs(Dictionary<Guid, LocalizedText> departments, Dictionary<Guid, string?> managers, TreeIndex<Location> locations)
    {
        _departments = departments;
        _managers = managers;
        _locations = locations;
    }

    public static async Task<ProfileRefs> LoadAsync(ZelvoDbContext db, IReadOnlyCollection<UserAccount> users, CancellationToken cancellationToken)
    {
        var departmentIds = users.Where(u => u.DepartmentId is not null).Select(u => u.DepartmentId!.Value).Distinct().ToList();
        var managerIds = users.Where(u => u.ManagerId is not null).Select(u => u.ManagerId!.Value).Distinct().ToList();
        var anyLocation = users.Any(u => u.LocationId is not null);
        return new ProfileRefs(
            departmentIds.Count == 0 ? [] : await db.Departments.AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken),
            managerIds.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => managerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email, cancellationToken),
            new TreeIndex<Location>(anyLocation ? await db.Locations.AsNoTracking().ToListAsync(cancellationToken) : []));
    }

    public NamedRef? Department(Guid? id) => id is { } d && _departments.TryGetValue(d, out var name) ? NamedRef.From(d, name) : null;

    public PersonRef? Manager(Guid? id) => id is { } m && _managers.TryGetValue(m, out var name) ? new PersonRef(m, name) : null;

    public NodeRef? Location(Guid? id) => _locations.RefOf(id);
}

/// <summary>
/// Changes to a user's profile fields (task C1, product owner decision 2026-11-24): department,
/// manager, location, employee number and phone. Empty text removes a value. Shared by the user
/// screens and bulk import.
/// </summary>
internal sealed class ProfileChange
{
    private ProfileChange()
    {
    }

    /// <summary>Field keys (as in the users resource) being changed.</summary>
    public List<string> Fields { get; } = [];

    /// <summary>A value that is not valid on its own.</summary>
    public ProfileError? Error { get; private set; }

    /// <summary>Set: <see cref="Guid.Empty"/> removes.</summary>
    public Guid? DepartmentId { get; private set; }

    /// <summary>Set: <see cref="Guid.Empty"/> removes.</summary>
    public Guid? ManagerId { get; private set; }

    /// <summary>Set: <see cref="Guid.Empty"/> removes.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Set: empty removes.</summary>
    public string? EmployeeId { get; private set; }

    /// <summary>Set (normalised): empty removes.</summary>
    public string? Phone { get; private set; }

    public static ProfileChange Parse(UpdateUserRequest request) =>
        Parse(request.DepartmentId, request.ManagerId, request.LocationId, request.EmployeeId, request.Phone);

    public static ProfileChange Parse(string? departmentId, string? managerId, string? locationId, string? employeeId, string? phone)
    {
        var change = new ProfileChange();
        Guid? Id(string? value, string field, string json)
        {
            if (value is null)
            {
                return null;
            }

            change.Fields.Add(field);
            if (value.Trim().Length == 0)
            {
                return Guid.Empty;
            }

            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                change.Error ??= new ProfileError(json, "Use an id, or an empty string to remove it.");
            }

            return id;
        }

        change.DepartmentId = Id(departmentId, "department", "departmentId");
        change.ManagerId = Id(managerId, "manager", "managerId");
        change.LocationId = Id(locationId, "location", "locationId");
        if (employeeId is not null)
        {
            change.Fields.Add("employeeId");
            change.EmployeeId = employeeId.Trim();
            if (change.EmployeeId.Length > UserAccount.EmployeeIdMaxLength || change.EmployeeId.Any(char.IsControl))
            {
                change.Error ??= new ProfileError("employeeId", $"Use at most {UserAccount.EmployeeIdMaxLength} characters.");
            }
        }

        if (phone is not null)
        {
            change.Fields.Add("phone");
            if (phone.Trim().Length == 0)
            {
                change.Phone = string.Empty;
            }
            else if (FieldFormats.Phone(phone) is { } normal)
            {
                change.Phone = normal;
            }
            else
            {
                change.Error ??= new ProfileError("phone", "Use a phone number such as +971 50 123 4567.");
            }
        }

        return change;
    }

    /// <summary>Checks the references and applies the change to <paramref name="account"/>.</summary>
    public async Task<ProfileError?> ApplyAsync(ZelvoDbContext db, UserAccount account, CancellationToken cancellationToken)
    {
        if (DepartmentId is { } departmentId)
        {
            if (departmentId != Guid.Empty && departmentId != account.DepartmentId
                && !await db.Departments.AnyAsync(d => d.Id == departmentId && d.Active, cancellationToken))
            {
                return new ProfileError("departmentId", "Choose an active department.");
            }

            account.DepartmentId = departmentId == Guid.Empty ? null : departmentId;
        }

        if (ManagerId is { } managerId)
        {
            if (managerId != Guid.Empty && managerId != account.ManagerId)
            {
                if (managerId == account.Id)
                {
                    return new ProfileError("managerId", "A person cannot be their own manager.");
                }

                if (!await db.Users.AnyAsync(u => u.Id == managerId && u.Status == UserStatus.Active, cancellationToken))
                {
                    return new ProfileError("managerId", "Choose an active user of this organisation.");
                }

                // No circles: the new manager's chain of managers must not reach this person.
                var managers = await db.Users.AsNoTracking().Where(u => u.ManagerId != null).ToDictionaryAsync(u => u.Id, u => u.ManagerId!.Value, cancellationToken);
                Guid? current = managerId;
                for (var depth = 0; current is { } step && depth < 100; depth++)
                {
                    if (step == account.Id)
                    {
                        return new ProfileError("managerId", "This would make a circle of managers.");
                    }

                    current = managers.TryGetValue(step, out var next) ? next : null;
                }
            }

            account.ManagerId = managerId == Guid.Empty ? null : managerId;
        }

        if (LocationId is { } locationId)
        {
            if (locationId != Guid.Empty && locationId != account.LocationId && !await TreeEndpoints.IsSelectableAsync(db.Locations, locationId, cancellationToken))
            {
                return new ProfileError("locationId", "Choose an active location.");
            }

            account.LocationId = locationId == Guid.Empty ? null : locationId;
        }

        if (EmployeeId is { } employeeId)
        {
            account.EmployeeId = employeeId.Length == 0 ? null : employeeId;
        }

        if (Phone is { } phone)
        {
            account.Phone = phone.Length == 0 ? null : phone;
        }

        return null;
    }
}

/// <summary>Department names and location paths, for bulk import and export (task C1).</summary>
internal sealed class ProfileNames
{
    private readonly List<Department> _departments;
    private readonly TreeIndex<Location> _locations;

    private ProfileNames(List<Department> departments, TreeIndex<Location> locations)
    {
        _departments = departments;
        _locations = locations;
    }

    public static async Task<ProfileNames> LoadAsync(ZelvoDbContext db, CancellationToken cancellationToken) => new(
        await db.Departments.AsNoTracking().ToListAsync(cancellationToken),
        new TreeIndex<Location>(await db.Locations.AsNoTracking().ToListAsync(cancellationToken)));

    public string? DepartmentName(Guid id) => _departments.FirstOrDefault(d => d.Id == id) is { } d ? d.Name.En ?? d.Name.Ar : null;

    public string? LocationPath(Guid id) => _locations.Find(id) is { } node ? _locations.PathOf(node).En ?? _locations.PathOf(node).Ar : null;

    /// <summary>An active department with this English or Arabic name (ignoring case).</summary>
    public Guid? FindDepartment(string name) =>
        _departments.FirstOrDefault(d => d.Active && (string.Equals(d.Name.En, name.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.Name.Ar, name.Trim(), StringComparison.Ordinal)))?.Id;

    /// <summary>A selectable location with this English or Arabic path; levels separated by › or &gt;.</summary>
    public Guid? FindLocation(string path)
    {
        static string Normal(string value) =>
            string.Join('>', value.Split(['›', '>'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var wanted = Normal(path);
        return _locations.Nodes.FirstOrDefault(n => _locations.Selectable(n) && _locations.PathOf(n) is var p
            && (string.Equals(Normal(p.En ?? string.Empty), wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(Normal(p.Ar ?? string.Empty), wanted, StringComparison.Ordinal)))?.Id;
    }
}
