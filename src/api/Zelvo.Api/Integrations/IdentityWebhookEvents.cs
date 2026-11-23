using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Integrations;

namespace Zelvo.Api.Integrations;

/// <summary>
/// Webhook events of users and roles (the first events, decided 2026-11-22): <c>user.created</c>,
/// <c>user.updated</c>, <c>user.disabled</c>, <c>role.created</c>, <c>role.updated</c> and
/// <c>role.deleted</c>. Payloads hold ids, status and the names of the changed fields only, no
/// personal data; receivers read the details through the API with their own key, which applies
/// their field rules.
/// </summary>
internal sealed class IdentityWebhookEvents : IWebhookEventSource
{
    // Bookkeeping that is not a change anyone needs to hear about.
    private static readonly HashSet<string> IgnoredUserFields = new(StringComparer.Ordinal)
    {
        nameof(UserAccount.UpdatedAt), nameof(UserAccount.LastSeenAt), nameof(UserAccount.Version),
        nameof(UserAccount.PreferredLanguage), nameof(UserAccount.Calendar), nameof(UserAccount.Digits),
    };

    private static readonly HashSet<string> IgnoredRoleFields = new(StringComparer.Ordinal) { nameof(Role.UpdatedAt), nameof(Role.Version) };

    public async Task<IReadOnlyList<WebhookEvent>> CollectAsync(ZelvoDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var events = new List<WebhookEvent>();
        var tracker = context.ChangeTracker;

        var createdUsers = new HashSet<Guid>();
        var changedUsers = new Dictionary<Guid, (UserStatus Status, SortedSet<string> Fields)>();
        foreach (var entry in tracker.Entries<UserAccount>())
        {
            var user = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                createdUsers.Add(user.Id);
                events.Add(new WebhookEvent(WebhookEventTypes.UserCreated, new UserEventData(user.Id, user.Status, null)));
            }
            else if (entry.State == EntityState.Modified)
            {
                var fields = entry.Properties
                    .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && !IgnoredUserFields.Contains(p.Metadata.Name))
                    .Select(p => char.ToLowerInvariant(p.Metadata.Name[0]) + p.Metadata.Name[1..]);
                var set = new SortedSet<string>(fields, StringComparer.Ordinal);
                if (set.Count > 0)
                {
                    var disabled = set.Contains("status") && user.Status == UserStatus.Disabled
                        && (UserStatus?)entry.Property(u => u.Status).OriginalValue != UserStatus.Disabled;
                    if (disabled)
                    {
                        events.Add(new WebhookEvent(WebhookEventTypes.UserDisabled, new UserEventData(user.Id, user.Status, null)));
                        set.Remove("status");
                    }

                    if (set.Count > 0)
                    {
                        changedUsers[user.Id] = (user.Status, set);
                    }
                }
            }
        }

        foreach (var entry in tracker.Entries<UserRole>().Where(e => e.State is EntityState.Added or EntityState.Deleted))
        {
            var userId = entry.Entity.UserId;
            if (createdUsers.Contains(userId))
            {
                continue;
            }

            if (!changedUsers.TryGetValue(userId, out var change))
            {
                var status = tracker.Entries<UserAccount>().FirstOrDefault(u => u.Entity.Id == userId)?.Entity.Status
                    ?? await context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Status).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                change = (status, new SortedSet<string>(StringComparer.Ordinal));
                changedUsers[userId] = change;
            }

            change.Fields.Add("roles");
        }

        events.AddRange(changedUsers.Select(c =>
            new WebhookEvent(WebhookEventTypes.UserUpdated, new UserEventData(c.Key, c.Value.Status, [.. c.Value.Fields]))));

        // Roles: the role itself, and its permissions and field rules.
        var createdOrDeleted = new HashSet<Guid>();
        var changedRoles = new Dictionary<Guid, SortedSet<string>>();
        foreach (var entry in tracker.Entries<Role>())
        {
            var role = entry.Entity;
            switch (entry.State)
            {
                case EntityState.Added:
                    createdOrDeleted.Add(role.Id);
                    events.Add(new WebhookEvent(WebhookEventTypes.RoleCreated, new RoleEventData(role.Id, role.Key, null)));
                    break;
                case EntityState.Deleted:
                    createdOrDeleted.Add(role.Id);
                    events.Add(new WebhookEvent(WebhookEventTypes.RoleDeleted, new RoleEventData(role.Id, role.Key, null)));
                    break;
                case EntityState.Modified:
                    var fields = entry.Properties
                        .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && !IgnoredRoleFields.Contains(p.Metadata.Name))
                        .Select(p => char.ToLowerInvariant(p.Metadata.Name[0]) + p.Metadata.Name[1..])
                        .ToList();
                    if (entry.ComplexProperties.Any(c => c.IsModified))
                    {
                        fields.Add("name");
                    }

                    if (fields.Count > 0)
                    {
                        changedRoles[role.Id] = new SortedSet<string>(fields, StringComparer.Ordinal);
                    }

                    break;
                default:
                    break;
            }
        }

        void Touch(Guid roleId, string field)
        {
            if (createdOrDeleted.Contains(roleId))
            {
                return;
            }

            if (!changedRoles.TryGetValue(roleId, out var set))
            {
                changedRoles[roleId] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(field);
        }

        foreach (var entry in tracker.Entries<RoleGrant>().Where(e => e.State is EntityState.Added or EntityState.Deleted))
        {
            Touch(entry.Entity.RoleId, "permissions");
        }

        foreach (var entry in tracker.Entries<RoleFieldRule>().Where(e => e.State is EntityState.Added or EntityState.Deleted or EntityState.Modified))
        {
            Touch(entry.Entity.RoleId, "fieldRules");
        }

        if (changedRoles.Count > 0)
        {
            var ids = changedRoles.Keys.ToList();
            var keys = tracker.Entries<Role>().Where(r => ids.Contains(r.Entity.Id)).ToDictionary(r => r.Entity.Id, r => r.Entity.Key);
            var missing = ids.Where(id => !keys.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                foreach (var role in await context.Roles.AsNoTracking().Where(r => missing.Contains(r.Id))
                             .Select(r => new { r.Id, r.Key }).ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    keys[role.Id] = role.Key;
                }
            }

            events.AddRange(changedRoles.Select(c =>
                new WebhookEvent(WebhookEventTypes.RoleUpdated, new RoleEventData(c.Key, keys.GetValueOrDefault(c.Key), [.. c.Value]))));
        }

        return events;
    }
}

/// <summary><c>data</c> of user events.</summary>
/// <param name="Id">Zelvo user id (<c>GET /api/v1/users/{id}</c>).</param>
/// <param name="Status">Active or Disabled.</param>
/// <param name="Changed">For <c>user.updated</c>: the fields that changed.</param>
internal sealed record UserEventData(Guid Id, UserStatus Status, IReadOnlyList<string>? Changed);

/// <summary><c>data</c> of role events.</summary>
/// <param name="Id">Role id.</param>
/// <param name="Key">Role key.</param>
/// <param name="Changed">For <c>role.updated</c>: what changed (fields, <c>permissions</c>, <c>fieldRules</c>).</param>
internal sealed record RoleEventData(Guid Id, string? Key, IReadOnlyList<string>? Changed);
