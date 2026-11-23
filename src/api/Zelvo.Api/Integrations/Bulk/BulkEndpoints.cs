using Microsoft.AspNetCore.Mvc;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;

namespace Zelvo.Api.Integrations.Bulk;

/// <summary>Bulk export and import in CSV and Excel (module 14), for every registered record type.</summary>
internal static class BulkEndpoints
{
    /// <summary>Largest file accepted for import.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Most rows in one import.</summary>
    public const int MaxRows = 5000;

    public static RouteGroupBuilder MapBulkEndpoints(this RouteGroupBuilder group)
    {
        var bulk = group.MapGroup("/bulk").WithTags("Bulk import and export").RequireTenant();

        bulk.MapGet("/", Types)
            .WithName("ListBulkTypes")
            .WithSummary("Record types that can be exported or imported, with their columns, and whether the caller may export or import each.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<BulkTypeResponse>>();

        bulk.MapGet("/{type}/template", Template)
            .WithName("GetBulkTemplate")
            .WithSummary("An empty file with the type's columns, to fill in for an import. format=csv (default) or xlsx.")
            .RequireAuthorization()
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        bulk.MapGet("/{type}/export", ExportAsync)
            .WithName("ExportBulk")
            .WithSummary("Exports the records the caller may see. Fields hidden from the caller are left out. Cells that a spreadsheet would run as formulas start with an apostrophe. format=csv (default) or xlsx. Audited.")
            .RequireAuthorization()
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        bulk.MapPost("/{type}/import", ImportAsync)
            .WithName("ImportBulk")
            .WithSummary("Imports a CSV or Excel file (multipart field 'file', at most 5 MB and 5,000 rows). Every row is checked first; with errors nothing is changed and the errors are listed per row and column. dryRun=true only checks. Audited.")
            .RequireAuthorization()
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxFileBytes + (64 * 1024)))
            .Produces<BulkImportResult>()
            .Produces<BulkImportResult>(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    internal static IReadOnlyList<BulkTypeResponse> Types(IEnumerable<IBulkType> types, CurrentUser user) =>
        [.. types.Select(t => new BulkTypeResponse(t.Key, [.. t.Columns.Select(c => new BulkColumnResponse(c.Key, c.Required, c.Description))],
            user.Has(t.ExportPermission), user.Has(t.ImportPermission)))];

    internal static IResult Template(string type, [FromQuery] string? format, IEnumerable<IBulkType> types)
    {
        if (Find(types, type) is not { } bulkType)
        {
            return UnknownType();
        }

        if (Format(format) is not { } fileFormat)
        {
            return BadFormat();
        }

        var bytes = TabularFile.Write(fileFormat, [.. bulkType.Columns.Select(c => c.Key)], []);
        return TypedResults.File(bytes, TabularFile.ContentType(fileFormat), $"zelvo-{bulkType.Key}-template.{TabularFile.Extension(fileFormat)}");
    }

    internal static async Task<IResult> ExportAsync(
        string type, [FromQuery] string? format, IEnumerable<IBulkType> types, CurrentUser user, CancellationToken cancellationToken)
    {
        if (Find(types, type) is not { } bulkType)
        {
            return UnknownType();
        }

        if (Refuse(user, bulkType.ExportPermission) is { } refused)
        {
            return refused;
        }

        if (Format(format) is not { } fileFormat)
        {
            return BadFormat();
        }

        var table = await bulkType.ExportAsync(cancellationToken);
        var bytes = TabularFile.Write(fileFormat, table.Header, table.Rows);
        var name = $"zelvo-{bulkType.Key}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.{TabularFile.Extension(fileFormat)}";
        return TypedResults.File(bytes, TabularFile.ContentType(fileFormat), name);
    }

    internal static async Task<IResult> ImportAsync(
        string type, [FromQuery] bool? dryRun, IFormFile? file, IEnumerable<IBulkType> types, CurrentUser user, CancellationToken cancellationToken)
    {
        if (Find(types, type) is not { } bulkType)
        {
            return UnknownType();
        }

        if (Refuse(user, bulkType.ImportPermission) is { } refused)
        {
            return refused;
        }

        if (file is null || file.Length == 0)
        {
            return UserEndpoints.Validation("file", "Send the file in the multipart field 'file'.");
        }

        if (file.Length > MaxFileBytes)
        {
            return UserEndpoints.Validation("file", "The file is larger than 5 MB; split it into smaller files.");
        }

        if (TabularFile.FormatOf(file.FileName) is not { } fileFormat)
        {
            return UserEndpoints.Validation("file", "Use a .csv or .xlsx file.");
        }

        List<string[]> rows;
        try
        {
            await using var stream = file.OpenReadStream();
            rows = TabularFile.Read(fileFormat, stream, MaxRows);
        }
        catch (TabularFileException ex)
        {
            return UserEndpoints.Validation("file", ex.Message);
        }

        if (rows.Count == 0)
        {
            return UserEndpoints.Validation("file", "The file is empty; the first row must be the column names.");
        }

        var table = new BulkTable(rows[0], [.. rows.Skip(1)]);
        BulkImportResult result;
        try
        {
            result = await bulkType.ImportAsync(table, dryRun == true, cancellationToken);
        }
        catch (BulkImportRefusedException ex)
        {
            return ApiProblem.Create(ex.Status, ex.Code, ex.Message);
        }

        return result.Errors.Count > 0 && !result.DryRun
            ? TypedResults.UnprocessableEntity(result)
            : TypedResults.Ok(result);
    }

    private static IBulkType? Find(IEnumerable<IBulkType> types, string key) =>
        types.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));

    private static TabularFormat? Format(string? format) => format?.ToUpperInvariant() switch
    {
        null or "" or "CSV" => TabularFormat.Csv,
        "XLSX" => TabularFormat.Xlsx,
        _ => null,
    };

    /// <summary>The same answers as permission checks on fixed endpoints, including the step-up for a second factor.</summary>
    private static IResult? Refuse(CurrentUser user, string permission)
    {
        if (user.Has(permission))
        {
            return null;
        }

        return user.PendingMfaPermissions.Contains(permission)
            ? ApiProblem.Create(StatusCodes.Status401Unauthorized, "mfa-required", "Sign in with a second factor to do this.")
            : ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You do not have permission to do this.");
    }

    private static IResult UnknownType() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "bulk-type-not-found", "This record type cannot be imported or exported.");

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem BadFormat() => UserEndpoints.Validation("format", "Use csv or xlsx.");
}

/// <summary>A record type for bulk import and export.</summary>
/// <param name="Key">Key in the URL.</param>
/// <param name="Columns">Columns in file order.</param>
/// <param name="CanExport">The caller may export it.</param>
/// <param name="CanImport">The caller may import it.</param>
internal sealed record BulkTypeResponse(string Key, IReadOnlyList<BulkColumnResponse> Columns, bool CanExport, bool CanImport);

/// <summary>A column.</summary>
/// <param name="Key">Header in files.</param>
/// <param name="Required">Every row needs a value.</param>
/// <param name="Description">How to fill it (English, for developers; the web app translates by key).</param>
internal sealed record BulkColumnResponse(string Key, bool Required, string Description);
