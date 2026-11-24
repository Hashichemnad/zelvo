using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Zelvo.Api.Conventions;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

using Zelvo.Tickets;

namespace Zelvo.Api.Identity;

/// <summary>
/// For signed-in requests: finds the user's Zelvo record (creating it, as a requester, at the
/// first sign-in), keeps email and name in step with Keycloak, refuses disabled users and works
/// out the permissions that apply. After a UAE PASS sign-in it also applies the GOV-1 rules:
/// staff need a verified account (SOP2+), requesters the tenant's minimum level, and a verified
/// UAE PASS sign-in counts as the second factor for staff when the tenant allows it.
/// </summary>
internal sealed class CurrentUserMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromHours(1);

    public async Task InvokeAsync(
        HttpContext context, ZelvoDbContext db, CurrentUser currentUser, AuditContext auditContext,
        IOptions<IdentityOptions> options, Settings.TenantSettingsStore settings, Integrations.ApiClientDirectory clients)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated == true && principal.HasClaim(c => c.Type == Integrations.ApiClientClaims.ClientId))
        {
            // An integration (module 14): its role narrowed by its scopes. Revoked or expired
            // integrations are refused at once, even with a token that has not expired yet.
            var client = await clients.ForPrincipalAsync(principal, context.RequestAborted).ConfigureAwait(false);
            if (client is null || !client.IsUsable(DateTimeOffset.UtcNow))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                await ApiProblem.WriteAsync(context, StatusCodes.Status401Unauthorized, "credential-revoked",
                    "This API key or OAuth client is revoked, expired or unknown.").ConfigureAwait(false);
                return;
            }

            var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).Include(r => r.FieldRules)
                .AsSplitQuery().SingleAsync(r => r.Id == client.RoleId, context.RequestAborted).ConfigureAwait(false);
            auditContext.SetActor(AuditActorType.ApiClient, client.Id.ToString(), client.Name);
            currentUser.SetClient(client, role);
            await next(context).ConfigureAwait(false);
            return;
        }

        if (principal.Identity?.IsAuthenticated == true
            && principal.FindFirstValue(ZelvoClaimTypes.Subject) is { Length: > 0 } subject)
        {
            var account = await LoadOrCreateAsync(db, auditContext, principal, subject, context.RequestAborted).ConfigureAwait(false);
            auditContext.SetActor(AuditActorType.User, account.Id.ToString(), account.DisplayName ?? account.Email);
            if (account.Status != UserStatus.Active)
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "user-disabled", "This user is disabled.").ConfigureAwait(false);
                return;
            }

            var roles = account.Roles.Select(r => r.Role!).ToList();
            var hasMfa = principal.FindFirstValue(ZelvoClaimTypes.Acr) is { } acr
                && options.Value.MfaAcrValues.Contains(acr, StringComparer.Ordinal);
            if (principal.FindFirstValue(ZelvoClaimTypes.IdentityProvider) == UaePassProvider.Alias)
            {
                var level = UaePassSettings.ParseLevel(principal.FindFirstValue(ZelvoClaimTypes.UaePassLevel));
                var staff = roles.Any(r => r.RequiresMfa);
                var minimum = staff
                    ? UaePassSettings.StaffMinimumLevel
                    : await settings.GetAsync(UaePassSettings.RequesterMinimumLevel, context.RequestAborted).ConfigureAwait(false);
                if (level < minimum)
                {
                    await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "uae-pass-level-too-low",
                        $"This account needs a UAE PASS account of level {minimum} or higher; this sign-in was {level}.").ConfigureAwait(false);
                    return;
                }

                if (!hasMfa && level >= UaePassLevel.SOP2
                    && await settings.GetAsync(UaePassSettings.AcceptAsSecondFactor, context.RequestAborted).ConfigureAwait(false))
                {
                    hasMfa = true;
                }
            }

            currentUser.Set(account, roles, hasMfa);
        }

        await next(context).ConfigureAwait(false);
    }

    private static async Task<UserAccount> LoadOrCreateAsync(
        ZelvoDbContext db, AuditContext auditContext, ClaimsPrincipal principal, string subject, CancellationToken cancellationToken)
    {
        var email = Truncate(principal.FindFirstValue("email"), UserAccount.EmailMaxLength);
        var name = Truncate(principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username"), UserAccount.DisplayNameMaxLength);
        var now = DateTimeOffset.UtcNow;

        var account = await Query(db).SingleOrDefaultAsync(u => u.Subject == subject, cancellationToken).ConfigureAwait(false);
        if (account is null && email is not null && string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase)
            && await ClaimContactAsync(db, auditContext, email, subject, cancellationToken).ConfigureAwait(false) is { } claimed)
        {
            return claimed;
        }

        if (account is null)
        {
            var requester = await db.Roles.SingleAsync(r => r.Key == BuiltInRoles.Requester, cancellationToken).ConfigureAwait(false);
            account = new UserAccount { Subject = subject, Email = email, DisplayName = name, LastSeenAt = now };
            account.Roles.Add(new UserRole { UserId = account.Id, RoleId = requester.Id });
            db.Users.Add(account);

            // The new user is the actor of its own creation.
            auditContext.SetActor(AuditActorType.User, account.Id.ToString(), name ?? email);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another request created it first.
                db.ChangeTracker.Clear();
            }

            account = await Query(db).SingleAsync(u => u.Subject == subject, cancellationToken).ConfigureAwait(false);
            return account;
        }

        auditContext.SetActor(AuditActorType.User, account.Id.ToString(), account.DisplayName ?? account.Email);
        var changed = false;
        if (email is not null && email != account.Email)
        {
            account.Email = email;
            changed = true;
        }

        if (name is not null && name != account.DisplayName && account.DisplayName is null)
        {
            account.DisplayName = name;
            changed = true;
        }

        if (account.LastSeenAt is null || now - account.LastSeenAt > LastSeenResolution)
        {
            account.LastSeenAt = now;
            changed = true;
        }

        if (changed)
        {
            account.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A parallel request updated it; the values are the same.
                db.ChangeTracker.Clear();
                account = await Query(db).SingleAsync(u => u.Subject == subject, cancellationToken).ConfigureAwait(false);
            }
        }

        return account;
    }

    /// <summary>
    /// Task E2: people who wrote by email or chat before they ever signed in have an account made
    /// for their address. Their first sign-in with that address verified takes it over, with their
    /// tickets.
    /// </summary>
    private static async Task<UserAccount?> ClaimContactAsync(
        ZelvoDbContext db, AuditContext auditContext, string email, string subject, CancellationToken cancellationToken)
    {
        var contact = await ContactAccounts.FindByEmailAsync(db, email, cancellationToken).ConfigureAwait(false);
        if (contact is null || !ContactAccounts.IsContact(contact))
        {
            return null;
        }

        contact.Subject = subject;
        contact.LastSeenAt = DateTimeOffset.UtcNow;
        contact.UpdatedAt = DateTimeOffset.UtcNow;
        auditContext.SetActor(AuditActorType.User, contact.Id.ToString(), contact.DisplayName ?? contact.Email);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another request claimed it or made the account first.
            db.ChangeTracker.Clear();
        }

        return await Query(db).SingleOrDefaultAsync(u => u.Subject == subject, cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<UserAccount> Query(ZelvoDbContext db) => db.Users
        .Include(u => u.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
        .Include(u => u.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.FieldRules)
        .AsSplitQuery();

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
