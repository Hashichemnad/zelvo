using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Directory;

namespace Zelvo.Api.ServiceDesk;

/// <summary>
/// Departments (task C1, product owner decision 2026-11-24): a bilingual list kept in Settings →
/// Departments. People belong to one; forms fill it in from the requester's profile. Every signed-in
/// user sees the active ones; administrators change them. Every change is audited.
/// </summary>
internal static class DepartmentEndpoints
{
    public static RouteGroupBuilder MapDepartmentEndpoints(this RouteGroupBuilder group)
    {
        var departments = group.MapGroup("/departments").WithTags("Directory").RequireTenant();
        departments.MapGet("/", ListAsync).WithName("ListDepartments")
            .WithSummary("Departments by English name, with how many people belong to each. Every signed-in user sees the active ones; includeInactive=true needs directory.read.")
            .RequireAuthorization().Produces<IReadOnlyList<DepartmentResponse>>();
        departments.MapPost("/", CreateAsync).WithName("CreateDepartment")
            .WithSummary("Add a department. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<DepartmentResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        departments.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateDepartment")
            .WithSummary("Rename a department or make it active or inactive (it stays on people). Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces<DepartmentResponse>().ProducesValidationProblem();
        departments.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteDepartment")
            .WithSummary("Delete a department nobody belongs to; otherwise make it inactive. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage).Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<Results<Ok<List<DepartmentResponse>>, ProblemHttpResult>> ListAsync(
        bool? includeInactive, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (includeInactive == true && !user.Has(Permissions.DirectoryRead))
        {
            return DirectoryProblems.Forbidden("Inactive entries need directory.read.");
        }

        var rows = await db.Departments.AsNoTracking().Where(d => includeInactive == true || d.Active)
            .Select(d => new { Department = d, Users = db.Users.Count(u => u.DepartmentId == d.Id) })
            .ToListAsync(cancellationToken);
        var counts = user.Has(Permissions.DirectoryRead);
        return TypedResults.Ok(rows.OrderBy(r => r.Department.Name.En ?? r.Department.Name.Ar, StringComparer.OrdinalIgnoreCase)
            .Select(r => DepartmentResponse.From(r.Department, counts ? r.Users : null)).ToList());
    }

    internal static async Task<Results<Created<DepartmentResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SkillRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, Department.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {Department.NameMaxLength} characters in each language.");
        }

        if ((await db.Departments.ToListAsync(cancellationToken)).Any(d => DirectoryProblems.SameName(d.Name, name)))
        {
            return DirectoryProblems.NameTaken();
        }

        var department = new Department { Name = name, Active = request.Active ?? true };
        db.Departments.Add(department);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/departments/{department.Id}", DepartmentResponse.From(department, 0));
    }

    internal static async Task<Results<Ok<DepartmentResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateSkillRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var department = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (department is null)
        {
            return DirectoryProblems.NotFound("department");
        }

        if (request.Name is not null)
        {
            var name = DirectoryProblems.Clean(request.Name);
            if (!DirectoryProblems.Fits(name, Department.NameMaxLength))
            {
                return UserEndpoints.Validation("name", $"Use at most {Department.NameMaxLength} characters in each language.");
            }

            if ((await db.Departments.Where(d => d.Id != id).ToListAsync(cancellationToken)).Any(d => DirectoryProblems.SameName(d.Name, name)))
            {
                return DirectoryProblems.NameTaken();
            }

            department.Name = name;
        }

        department.Active = request.Active ?? department.Active;
        department.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(DepartmentResponse.From(department, await db.Users.CountAsync(u => u.DepartmentId == id, cancellationToken)));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var department = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (department is null)
        {
            return DirectoryProblems.NotFound("department");
        }

        if (await db.Users.AnyAsync(u => u.DepartmentId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("People belong to this department; move them first or make it inactive.");
        }

        db.Departments.Remove(department);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

/// <summary>A department.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name.</param>
/// <param name="Active">Whether people can be put in it.</param>
/// <param name="UserCount">People in it (callers with directory.read only).</param>
internal sealed record DepartmentResponse(Guid Id, LocalizedTextDto Name, bool Active, int? UserCount)
{
    public static DepartmentResponse From(Department department, int? users) => new(department.Id, LocalizedTextDto.From(department.Name), department.Active, users);
}
