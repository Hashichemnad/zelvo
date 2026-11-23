namespace Zelvo.Api.Integrations.Bulk;

/// <summary>
/// A record type that can be exported and imported in bulk (module 14: "bulk import and export
/// through the API, CSV and Excel"). Users come first (decided 2026-11-22); later modules register
/// theirs (catalog in C2, knowledge in D1…).
/// </summary>
internal interface IBulkType
{
    /// <summary>Key in the URL, for example <c>users</c>.</summary>
    string Key { get; }

    /// <summary>Permission needed to export.</summary>
    string ExportPermission { get; }

    /// <summary>Permission needed to import.</summary>
    string ImportPermission { get; }

    /// <summary>Columns, in file order.</summary>
    IReadOnlyList<BulkColumn> Columns { get; }

    /// <summary>The records the caller may see, as rows (hidden fields left out of both header and rows). Audited.</summary>
    Task<BulkTable> ExportAsync(CancellationToken cancellationToken);

    /// <summary>Checks every row, and when nothing is wrong and <paramref name="dryRun"/> is false, applies them all together.</summary>
    Task<BulkImportResult> ImportAsync(BulkTable table, bool dryRun, CancellationToken cancellationToken);
}

/// <summary>A column of a bulk type.</summary>
/// <param name="Key">Header in files (case does not matter when reading).</param>
/// <param name="Required">Every row must have a value.</param>
/// <param name="Description">How to fill it, for the template and the API description.</param>
internal sealed record BulkColumn(string Key, bool Required, string Description);

/// <summary>A table: header and rows.</summary>
internal sealed record BulkTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>A problem in one cell or row of an import.</summary>
/// <param name="Row">Row number as in the file (the header is row 1).</param>
/// <param name="Column">Column key, if one cell is the problem.</param>
/// <param name="Code">Stable code the web app translates, for example <c>email-invalid</c>.</param>
/// <param name="Message">English explanation for developers.</param>
internal sealed record BulkImportError(int Row, string? Column, string Code, string Message);

/// <summary>Outcome of an import or a check.</summary>
/// <param name="DryRun">Only checked; nothing was changed.</param>
/// <param name="Applied">The changes were saved (false when there were errors or on a check).</param>
/// <param name="Rows">Rows read.</param>
/// <param name="Created">Records that are (or would be) created.</param>
/// <param name="Updated">Records that are (or would be) changed.</param>
/// <param name="Unchanged">Rows that change nothing.</param>
/// <param name="Errors">Problems found; nothing is changed while there are any.</param>
internal sealed record BulkImportResult(bool DryRun, bool Applied, int Rows, int Created, int Updated, int Unchanged, IReadOnlyList<BulkImportError> Errors);
