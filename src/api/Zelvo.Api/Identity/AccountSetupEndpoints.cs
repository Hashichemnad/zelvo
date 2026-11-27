using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>
/// Task M2: the one-time setup link of a hardened installation's first administrator. The page
/// <c>/&lt;lang&gt;/account-setup#&lt;token&gt;</c> checks the link and sets the person's password in
/// Keycloak (its password policy decides); afterwards the person signs in and sets up the
/// authenticator app. Open to visitors; the token is the proof, and only its hash is stored.
/// </summary>
internal static partial class AccountSetupEndpoints
{
    /// <summary>Longest password passed to Keycloak (its own default limit is 64; F7 set 128).</summary>
    public const int MaxPasswordLength = 128;

    public static RouteGroupBuilder MapAccountSetupEndpoints(this RouteGroupBuilder group)
    {
        var setup = group.MapGroup("/identity/account-setup").WithTags("Identity").RequireTenant();
        setup.MapPost("/check", CheckAsync)
            .WithName("CheckAccountSetupLink")
            .WithSummary("Whether a one-time setup link still works, and for which email. Anyone with the link; 404 setup-link-invalid when it is unknown, used or expired.")
            .AllowAnonymousAccess()
            .Produces<AccountSetupCheckResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        setup.MapPost("/", CompleteAsync)
            .WithName("CompleteAccountSetup")
            .WithSummary("Uses a one-time setup link: sets the person's password in Keycloak (the realm's password policy decides; 422 password-refused) and makes the link unusable. The person then signs in and sets up the authenticator app. Anyone with the link; audited.")
            .AllowAnonymousAccess()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return group;
    }

    internal static async Task<IResult> CheckAsync(AccountSetupCheckRequest request, ZelvoDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var found = await FindAsync(db, request.Token, time.GetUtcNow(), cancellationToken);
        return found is null ? Invalid() : TypedResults.Ok(new AccountSetupCheckResponse(found.Value.Email, found.Value.Link.ExpiresAt));
    }

    internal static async Task<IResult> CompleteAsync(
        AccountSetupRequest request, ZelvoDbContext db, IKeycloakAdmin keycloak, ITenantContext tenant, IOptions<IdentityOptions> identity,
        IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxPasswordLength)
        {
            return UserEndpoints.Validation("password", $"Choose a password of at most {MaxPasswordLength} characters.");
        }

        var now = time.GetUtcNow();
        var found = await FindAsync(db, request.Token, now, cancellationToken);
        if (found is null)
        {
            return Invalid();
        }

        var (link, subject, _) = found.Value;
        try
        {
            await keycloak.SetPasswordAsync(identity.Value.RealmFor(tenant.Slug!), subject, request.Password, cancellationToken);
        }
        catch (KeycloakAdminException exception) when (exception.Status == System.Net.HttpStatusCode.BadRequest)
        {
            // Keycloak names the policy that refused it, for example invalidPasswordMinLengthMessage.
            var policy = PolicyPattern().Match(exception.Message) is { Success: true } match ? match.Groups[1].Value : null;
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The password does not meet the password rules. Choose another one.", type: ApiProblem.TypeFor("password-refused"),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "password-refused", ["policy"] = policy });
        }

        link.UsedAt = now;
        audit.Add(AuditCategory.Security, "identity.account-setup.completed", "user", link.UserId.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<(Data.Identity.AccountSetupLink Link, string Subject, string? Email)?> FindAsync(
        ZelvoDbContext db, string? token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return null;
        }

        var hash = FirstAdministrator.Hash(token);
        var link = await db.AccountSetupLinks.SingleOrDefaultAsync(l => l.TokenHash == hash, cancellationToken);
        if (link is null || !link.WorksAt(now))
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == link.UserId && u.Status == Data.Identity.UserStatus.Active && u.ErasedAt == null)
            .Select(u => new { u.Subject, u.Email }).SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : (link, user.Subject, user.Email);
    }

    private static IResult Invalid() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "setup-link-invalid", "This setup link does not work: it was used already, it expired or it is not complete. Ask the installation's operator for a new one.");

    [GeneratedRegex("(invalidPassword[A-Za-z]+Message)")]
    private static partial Regex PolicyPattern();
}

/// <summary>A setup link's token (from the address's fragment).</summary>
internal sealed record AccountSetupCheckRequest(string? Token);

/// <summary>Who the link is for and until when it works.</summary>
internal sealed record AccountSetupCheckResponse(string? Email, DateTimeOffset ExpiresAt);

/// <summary>Uses a setup link.</summary>
/// <param name="Token">The link's token.</param>
/// <param name="Password">The password chosen; Keycloak's password policy decides.</param>
internal sealed record AccountSetupRequest(string? Token, string? Password);
