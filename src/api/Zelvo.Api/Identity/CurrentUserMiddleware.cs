using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Zelvo.Api.Conventions;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Identity;

/// <summary>
/// For signed-in requests: finds the user's Zelvo record (creating it, as a requester, at the
/// first sign-in), keeps email and name in step with Keycloak, refuses disabled users and works
/// out the permissions that apply.
/// </summary>
internal sealed class CurrentUserMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromHours(1);

    public async Task InvokeAsync(
        HttpContext context, ZelvoDbContext db, CurrentUser currentUser, AuditContext auditContext,
        IOptions<IdentityOptions> options)
    {
        var principal = context.User;
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

            var hasMfa = principal.FindFirstValue(ZelvoClaimTypes.Acr) is { } acr
                && options.Value.MfaAcrValues.Contains(acr, StringComparer.Ordinal);
            currentUser.Set(account, [.. account.Roles.Select(r => r.Role!)], hasMfa);
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

    private static IQueryable<UserAccount> Query(ZelvoDbContext db) => db.Users
        .Include(u => u.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
        .Include(u => u.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.FieldRules)
        .AsSplitQuery();

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
