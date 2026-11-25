using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Connectors;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Connectors;

/// <summary>What a sync did.</summary>
public sealed record SyncStats
{
    /// <summary>Records read from the source.</summary>
    public int Read { get; set; }

    /// <summary>People created in Zelvo.</summary>
    public int Created { get; set; }

    /// <summary>People found in Zelvo and linked.</summary>
    public int Linked { get; set; }

    /// <summary>People whose details changed.</summary>
    public int Updated { get; set; }

    /// <summary>People disabled because the source no longer has them or disabled them.</summary>
    public int Disabled { get; set; }

    /// <summary>People enabled again.</summary>
    public int Enabled { get; set; }

    /// <summary>Group memberships added.</summary>
    public int MembershipsAdded { get; set; }

    /// <summary>Group memberships removed.</summary>
    public int MembershipsRemoved { get; set; }

    /// <summary>Records left out (no email, an email used twice).</summary>
    public int Skipped { get; set; }

    /// <summary>As JSON.</summary>
    public string Write() => JsonSerializer.Serialize(this, ConnectorSettings.JsonOptions);
}

/// <summary>Accounts that connectors and monitoring sources act as: disabled, never signing in, named after their source.</summary>
public static class SystemAccounts
{
    /// <summary>Prefix of their <see cref="UserAccount.Subject"/>.</summary>
    public const string SubjectPrefix = "system:";

    /// <summary>Stages a disabled account named <paramref name="name"/> (saved by the caller).</summary>
    public static async Task<UserAccount> CreateAsync(ZelvoDbContext db, string kind, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var requester = await db.Roles.SingleAsync(r => r.Key == BuiltInRoles.Requester, cancellationToken).ConfigureAwait(false);
        var account = new UserAccount
        {
            Subject = $"{SubjectPrefix}{kind}:{Guid.NewGuid():N}",
            DisplayName = IncidentIntake.Clean(name, UserAccount.DisplayNameMaxLength),
            Status = UserStatus.Disabled,
            AvailableForAssignment = false,
            NotifyTeams = false,
        };
        account.Roles.Add(new UserRole { UserId = account.Id, RoleId = requester.Id });
        db.Users.Add(account);
        return account;
    }
}

/// <summary>
/// Applies a directory (Entra ID or LDAP) to Zelvo (task E7; product owner decision 2026-11-25): one
/// way, from the directory. People are found by their link, else by email; new people become
/// requester accounts that their first sign-in with that email (verified) takes over; name, phone,
/// employee number, department and manager follow the directory; people the directory disabled or
/// no longer has are disabled (never deleted) and enabled again when they return; members of
/// mapped directory groups join the Zelvo group, and synced people who left it are removed.
/// Roles stay Zelvo's. Run with the tenant set; saving is done here.
/// </summary>
public sealed class DirectorySync(ZelvoDbContext db, IAuditLog audit, TimeProvider time)
{
    /// <summary>Applies <paramref name="people"/> for <paramref name="connector"/>.</summary>
    public async Task<SyncStats> ApplyAsync(Connector connector, ConnectorSettings settings, IReadOnlyList<DirectoryPerson> people, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(people);
        var now = time.GetUtcNow();
        var stats = new SyncStats { Read = people.Count };
        var links = await db.ConnectorLinks.Where(l => l.ConnectorId == connector.Id && l.Entity == ConnectorEntity.User).ToDictionaryAsync(l => l.ExternalId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        var byEmail = (await db.Users.Where(u => u.Email != null).Select(u => new { u.Id, u.Email }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.Email!.ToUpperInvariant(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var departments = await db.Departments.ToListAsync(cancellationToken).ConfigureAwait(false);
        var linkedUserIds = links.Values.Select(l => l.ZelvoId).ToHashSet();
        var users = await db.Users.Where(u => linkedUserIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken).ConfigureAwait(false);
        var requester = await db.Roles.SingleAsync(r => r.Key == BuiltInRoles.Requester, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var userOf = new Dictionary<string, UserAccount>(StringComparer.Ordinal);
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            if (!seen.Add(person.ExternalId))
            {
                continue;
            }

            UserAccount? user = null;
            if (links.TryGetValue(person.ExternalId, out var link) && users.TryGetValue(link.ZelvoId, out var linked))
            {
                // Task F5: an erased person's link stays so that the directory never fills their
                // account again or makes a new one for them; an administrator decides that.
                if (linked.ErasedAt is not null)
                {
                    link.SeenAt = now;
                    stats.Skipped++;
                    continue;
                }

                user = linked;
            }
            else if (person.Email is { Length: > 0 } email && email.Contains('@', StringComparison.Ordinal))
            {
                if (!emails.Add(email))
                {
                    stats.Skipped++;
                    continue;
                }

                if (byEmail.TryGetValue(email.ToUpperInvariant(), out var existing))
                {
                    user = await db.Users.FirstAsync(u => u.Id == existing, cancellationToken).ConfigureAwait(false);
                    stats.Linked++;
                }
                else
                {
                    user = new UserAccount { Subject = $"{ContactAccounts.SubjectPrefix}{Guid.NewGuid():N}", Email = email.Trim() };
                    user.Roles.Add(new UserRole { UserId = user.Id, RoleId = requester.Id });
                    db.Users.Add(user);
                    byEmail[email.ToUpperInvariant()] = user.Id;
                    stats.Created++;
                }

                link = new ConnectorLink { TenantId = connector.TenantId, ConnectorId = connector.Id, Entity = ConnectorEntity.User, ZelvoId = user.Id, ExternalId = Cut(person.ExternalId) };
                db.ConnectorLinks.Add(link);
                links[person.ExternalId] = link;
            }
            else
            {
                stats.Skipped++;
                continue;
            }

            userOf[person.ExternalId] = user;
            link!.SeenAt = now;
            var changed = false;
            void Set<T>(T current, T value, Action<T> apply)
            {
                if (!EqualityComparer<T>.Default.Equals(current, value))
                {
                    apply(value);
                    changed = true;
                }
            }

            if (person.Name is { Length: > 0 } name)
            {
                Set(user.DisplayName, IncidentIntake.Clean(name, UserAccount.DisplayNameMaxLength), v => user.DisplayName = v);
            }

            if (person.Phone is { Length: > 0 } phone && FieldFormats.Phone(phone) is { } normalised)
            {
                Set(user.Phone, normalised, v => user.Phone = v);
            }

            if (person.EmployeeId is { Length: > 0 } employee)
            {
                Set(user.EmployeeId, employee.Length <= UserAccount.EmployeeIdMaxLength ? employee : employee[..UserAccount.EmployeeIdMaxLength], v => user.EmployeeId = v);
            }

            if (person.Department is { Length: > 0 } departmentName)
            {
                var department = departments.FirstOrDefault(d => string.Equals(d.Name.En, departmentName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.Name.Ar, departmentName, StringComparison.Ordinal));
                if (department is null)
                {
                    // Named as the directory names it; the other language is listed as missing.
                    var cut = departmentName.Length <= Department.NameMaxLength ? departmentName : departmentName[..Department.NameMaxLength];
                    department = new Department { TenantId = connector.TenantId, Name = Arabic(cut) ? new LocalizedText(null, cut) : new LocalizedText(cut, null) };
                    db.Departments.Add(department);
                    departments.Add(department);
                }

                Set(user.DepartmentId, (Guid?)department.Id, v => user.DepartmentId = v);
            }

            if (!person.Enabled && user.Status == UserStatus.Active)
            {
                user.Status = UserStatus.Disabled;
                link.Gone = true;
                stats.Disabled++;
                changed = false;
            }
            else if (person.Enabled && link.Gone && user.Status == UserStatus.Disabled)
            {
                user.Status = UserStatus.Active;
                link.Gone = false;
                stats.Enabled++;
                changed = false;
            }

            if (changed && db.Entry(user).State != EntityState.Added)
            {
                user.UpdatedAt = now;
                stats.Updated++;
            }
        }

        // Managers, once everyone is known.
        foreach (var person in people)
        {
            if (userOf.TryGetValue(person.ExternalId, out var user) && person.ManagerExternalId is { } manager && userOf.TryGetValue(manager, out var boss) && user.ManagerId != boss.Id && boss.Id != user.Id)
            {
                user.ManagerId = boss.Id;
            }
        }

        // People the directory no longer has are disabled, not deleted.
        foreach (var (externalId, link) in links)
        {
            if (!seen.Contains(externalId) && !link.Gone && (users.TryGetValue(link.ZelvoId, out var user) || (user = await db.Users.FindAsync([link.ZelvoId], cancellationToken).ConfigureAwait(false)) is not null)
                && user.Status == UserStatus.Active)
            {
                user.Status = UserStatus.Disabled;
                link.Gone = true;
                stats.Disabled++;
            }
        }

        var mappings = settings.Entra?.GroupMappings ?? settings.Ldap?.GroupMappings ?? [];
        foreach (var mapping in mappings)
        {
            if (!await db.SupportGroups.AnyAsync(g => g.Id == mapping.GroupId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var wanted = people.Where(p => p.Groups.Contains(mapping.ExternalId, StringComparer.OrdinalIgnoreCase) && userOf.ContainsKey(p.ExternalId))
                .Select(p => userOf[p.ExternalId].Id).ToHashSet();
            var members = await db.SupportGroupMembers.Where(m => m.GroupId == mapping.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var synced = links.Values.Select(l => l.ZelvoId).ToHashSet();
            foreach (var member in members.Where(m => synced.Contains(m.UserId) && !wanted.Contains(m.UserId) && !m.IsLead))
            {
                db.SupportGroupMembers.Remove(member);
                stats.MembershipsRemoved++;
            }

            foreach (var id in wanted.Where(id => members.All(m => m.UserId != id)))
            {
                db.SupportGroupMembers.Add(new SupportGroupMember { TenantId = connector.TenantId, GroupId = mapping.GroupId, UserId = id });
                stats.MembershipsAdded++;
            }
        }

        audit.Add(AuditCategory.Admin, "connectors.synced", "connector", connector.Id.ToString(), Stats(stats));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return stats;
    }

    internal static Dictionary<string, object?> Stats(SyncStats stats) => new(StringComparer.Ordinal)
    {
        ["read"] = stats.Read, ["created"] = stats.Created, ["linked"] = stats.Linked, ["updated"] = stats.Updated, ["disabled"] = stats.Disabled,
        ["enabled"] = stats.Enabled, ["membershipsAdded"] = stats.MembershipsAdded, ["membershipsRemoved"] = stats.MembershipsRemoved, ["skipped"] = stats.Skipped,
    };

    internal static bool Arabic(string text)
    {
        var letters = text.Count(char.IsLetter);
        return letters > 0 && text.Count(c => c is >= '؀' and <= 'ۿ') * 2 >= letters;
    }

    private static string Cut(string value) => value.Length <= ConnectorLink.ExternalIdMaxLength ? value : value[..ConnectorLink.ExternalIdMaxLength];
}

/// <summary>
/// Applies a CRM's contacts to Zelvo (task E7): contacts become requesters (found by link, else
/// email, else made as accounts their first verified sign-in takes over), and their companies
/// become customer groups (B3) with them as members. Names and phones fill in only what Zelvo does
/// not have, since people may also be users of their own.
/// </summary>
public sealed class CrmSync(ZelvoDbContext db, IAuditLog audit, TimeProvider time)
{
    /// <summary>Applies <paramref name="contacts"/> for <paramref name="connector"/>.</summary>
    public async Task<SyncStats> ApplyAsync(Connector connector, ConnectorSettings settings, IReadOnlyList<CrmContact> contacts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(contacts);
        var now = time.GetUtcNow();
        var stats = new SyncStats { Read = contacts.Count };
        var links = await db.ConnectorLinks.Where(l => l.ConnectorId == connector.Id && (l.Entity == ConnectorEntity.User || l.Entity == ConnectorEntity.CustomerGroup))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var people = links.Where(l => l.Entity == ConnectorEntity.User).ToDictionary(l => l.ExternalId, StringComparer.Ordinal);
        var companies = links.Where(l => l.Entity == ConnectorEntity.CustomerGroup).ToDictionary(l => l.ExternalId, StringComparer.Ordinal);
        var requester = await db.Roles.SingleAsync(r => r.Key == BuiltInRoles.Requester, cancellationToken).ConfigureAwait(false);
        foreach (var contact in contacts.DistinctBy(c => c.ExternalId))
        {
            UserAccount? user;
            if (people.TryGetValue(contact.ExternalId, out var link))
            {
                user = await db.Users.FindAsync([link.ZelvoId], cancellationToken).ConfigureAwait(false);
                // Task F5: an erased person is never filled in again from the CRM.
                if (user?.ErasedAt is not null)
                {
                    stats.Skipped++;
                    continue;
                }
            }
            else
            {
                user = await ContactAccounts.FindByEmailAsync(db, contact.Email, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    user = new UserAccount { Subject = $"{ContactAccounts.SubjectPrefix}{Guid.NewGuid():N}", Email = contact.Email.Trim() };
                    user.Roles.Add(new UserRole { UserId = user.Id, RoleId = requester.Id });
                    db.Users.Add(user);
                    stats.Created++;
                }
                else
                {
                    stats.Linked++;
                }

                if (people.Values.Any(l => l.ZelvoId == user.Id))
                {
                    stats.Skipped++;
                    continue;
                }

                link = new ConnectorLink { TenantId = connector.TenantId, ConnectorId = connector.Id, Entity = ConnectorEntity.User, ZelvoId = user.Id, ExternalId = contact.ExternalId };
                db.ConnectorLinks.Add(link);
                people[contact.ExternalId] = link;
            }

            if (user is null)
            {
                stats.Skipped++;
                continue;
            }

            link.SeenAt = now;
            if (user.DisplayName is null && contact.Name is { Length: > 0 } name)
            {
                user.DisplayName = IncidentIntake.Clean(name, UserAccount.DisplayNameMaxLength);
                stats.Updated++;
            }

            if (user.Phone is null && contact.Phone is { Length: > 0 } phone && FieldFormats.Phone(phone) is { } normalised)
            {
                user.Phone = normalised;
            }

            if (settings.Crm?.CustomerGroups != false && contact.CompanyId is { Length: > 0 } companyId)
            {
                CustomerGroup? group = null;
                if (companies.TryGetValue(companyId, out var companyLink))
                {
                    group = await db.CustomerGroups.FindAsync([companyLink.ZelvoId], cancellationToken).ConfigureAwait(false);
                }
                else if (contact.CompanyName is { Length: > 0 } companyName)
                {
                    var cut = companyName.Length <= CustomerGroup.NameMaxLength ? companyName : companyName[..CustomerGroup.NameMaxLength];
                    group = new CustomerGroup { TenantId = connector.TenantId, Name = DirectorySync.Arabic(cut) ? new LocalizedText(null, cut) : new LocalizedText(cut, null) };
                    db.CustomerGroups.Add(group);
                    companyLink = new ConnectorLink { TenantId = connector.TenantId, ConnectorId = connector.Id, Entity = ConnectorEntity.CustomerGroup, ZelvoId = group.Id, ExternalId = companyId };
                    db.ConnectorLinks.Add(companyLink);
                    companies[companyId] = companyLink;
                }

                if (group is not null && !await db.CustomerGroupMembers.AnyAsync(m => m.GroupId == group.Id && m.UserId == user.Id, cancellationToken).ConfigureAwait(false)
                    && !db.ChangeTracker.Entries<CustomerGroupMember>().Any(e => e.Entity.GroupId == group.Id && e.Entity.UserId == user.Id))
                {
                    db.CustomerGroupMembers.Add(new CustomerGroupMember { TenantId = connector.TenantId, GroupId = group.Id, UserId = user.Id });
                    stats.MembershipsAdded++;
                }
            }
        }

        audit.Add(AuditCategory.Admin, "connectors.synced", "connector", connector.Id.ToString(), DirectorySync.Stats(stats));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return stats;
    }
}
