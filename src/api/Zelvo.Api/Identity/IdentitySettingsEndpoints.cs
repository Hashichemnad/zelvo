using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>Sign-in settings of the tenant.</summary>
internal static partial class IdentitySettingsEndpoints
{
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not apply sign-in settings to Keycloak.")]
    private static partial void LogKeycloakRefused(ILogger logger, Exception exception);

    public static RouteGroupBuilder MapIdentitySettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/identity/settings").WithTags("Identity").RequireTenant()
            .RequirePermission(Permissions.IdentityManage);

        settings.MapGet("/", GetAsync).WithName("GetIdentitySettings").WithSummary("Who may sign in with a one-time code sent by email.");
        settings.MapPut("/", UpdateAsync)
            .WithName("UpdateIdentitySettings")
            .WithSummary("Change who may sign in with an email code: KnownUsers (default), AllowedDomains (those domains get a requester account automatically) or Anyone. Applied to the tenant's Keycloak realm; audited.")
            .ProducesValidationProblem();
        return group;
    }

    internal static async Task<Ok<IdentitySettingsResponse>> GetAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.IdentitySettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new IdentitySettings();
        return TypedResults.Ok(IdentitySettingsResponse.From(settings));
    }

    internal static async Task<Results<Ok<IdentitySettingsResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        UpdateIdentitySettingsRequest request, ZelvoDbContext db, ITenantContext tenant, IKeycloakAdmin keycloak,
        IOptions<IdentityOptions> options, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var domains = (request.AllowedDomains ?? [])
            .Select(d => d.Trim().TrimStart('@').ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var invalid = domains.Where(d => d.Length > 253 || !DomainPattern().IsMatch(d)).ToList();
        if (invalid.Count > 0)
        {
            return UserEndpoints.Validation("allowedDomains", $"Not valid domains: {string.Join(", ", invalid)}.");
        }

        if (request.EmailCodeSignIn == EmailCodeSignIn.AllowedDomains && domains.Count == 0)
        {
            return UserEndpoints.Validation("allowedDomains", "List at least one domain for AllowedDomains.");
        }

        var settings = await db.IdentitySettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new IdentitySettings();
            db.IdentitySettings.Add(settings);
        }

        settings.EmailCodeSignIn = request.EmailCodeSignIn!.Value;
        settings.AllowedDomains = domains;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        // Saved and applied together: if Keycloak refuses, nothing changes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await keycloak.SetEmailCodeSignInAsync(options.Value.RealmFor(tenant.Slug!), settings.EmailCodeSignIn, domains, cancellationToken);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
        {
            LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable",
                "The sign-in service did not accept the change. Nothing was changed.");
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(IdentitySettingsResponse.From(settings));
    }
}

/// <summary>Sign-in settings.</summary>
/// <param name="EmailCodeSignIn">KnownUsers, AllowedDomains or Anyone.</param>
/// <param name="AllowedDomains">With AllowedDomains: domains whose people get a requester account.</param>
/// <param name="UpdatedAt">Last change.</param>
internal sealed record IdentitySettingsResponse(EmailCodeSignIn EmailCodeSignIn, IReadOnlyList<string> AllowedDomains, DateTimeOffset UpdatedAt)
{
    public static IdentitySettingsResponse From(IdentitySettings settings) =>
        new(settings.EmailCodeSignIn, settings.AllowedDomains, settings.UpdatedAt);
}

/// <summary>New sign-in settings.</summary>
public sealed class UpdateIdentitySettingsRequest
{
    /// <summary>KnownUsers (default), AllowedDomains or Anyone.</summary>
    [Required]
    public EmailCodeSignIn? EmailCodeSignIn { get; init; }

    /// <summary>Domains such as <c>ajman.ae</c>; required for AllowedDomains.</summary>
    [MaxLength(IdentitySettings.MaxAllowedDomains)]
    public IReadOnlyList<string>? AllowedDomains { get; init; }
}
