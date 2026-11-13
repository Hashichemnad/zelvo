using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Security;
using Zelvo.Data;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tenancy;

/// <summary>The current tenant.</summary>
internal static class TenantEndpoints
{
    public static RouteGroupBuilder MapTenantEndpoints(this RouteGroupBuilder group)
    {
        var tenant = group.MapGroup("/tenant").WithTags("Tenant").RequireTenant();

        tenant.MapGet("/", GetAsync)
            .WithName("GetCurrentTenant")
            .WithSummary("The tenant of this request: id, slug and bilingual name.")
            .AllowAnonymous();

        tenant.MapPatch("/", UpdateAsync)
            .WithName("UpdateCurrentTenant")
            .WithSummary("Change the tenant's name. Tenant administrators only; audited.")
            .RequireAuthorization(ZelvoPolicies.TenantAdmin)
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<Results<Ok<TenantResponse>, ProblemHttpResult>> GetAsync(
        ZelvoDbContext db, CancellationToken cancellationToken)
    {
        // Row-level security returns the current tenant only.
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return tenant is null
            ? (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "tenant-not-found", "The tenant does not exist.")
            : TypedResults.Ok(TenantResponse.From(tenant));
    }

    internal static async Task<Results<Ok<TenantResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        UpdateTenantRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var name = request.Name!.ToValue();
        if (name.En?.Length > Tenant.NameMaxLength || name.Ar?.Length > Tenant.NameMaxLength)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["name"] = [$"Each language of the name is at most {Tenant.NameMaxLength} characters."],
                },
                type: ApiProblem.TypeFor("validation-failed"),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed" });
        }

        var tenant = await db.Tenants.SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "tenant-not-found", "The tenant does not exist.");
        }

        tenant.Name = name;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(TenantResponse.From(tenant));
    }
}

/// <summary>A tenant.</summary>
internal sealed record TenantResponse(
    Guid Id, string Slug, LocalizedTextDto Name, TenantStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TenantResponse From(Tenant tenant) =>
        new(tenant.Id, tenant.Slug, LocalizedTextDto.From(tenant.Name), tenant.Status, tenant.CreatedAt, tenant.UpdatedAt);
}

/// <summary>Changes to the current tenant.</summary>
public sealed class UpdateTenantRequest
{
    /// <summary>Display name; at least one language, each at most 200 characters.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }
}
