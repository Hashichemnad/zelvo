using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Zelvo.Api.Integrations.Bulk;

/// <summary>File formats of bulk import and export.</summary>
public enum TabularFormat
{
    /// <summary>Comma-separated values, UTF-8 with a byte order mark (so Excel shows Arabic correctly).</summary>
    Csv,

    /// <summary>Excel workbook (.xlsx), first sheet.</summary>
    Xlsx,
}

/// <summary>A file that could not be read.</summary>
internal sealed class TabularFileException(string message) : Exception(message);

/// <summary>
/// Reads and writes simple tables (a header row, then one row per record) as CSV (RFC 4180) or
/// Excel. Cells that a spreadsheet would run as a formula (starting with <c>=</c>, <c>+</c>,
/// <c>-</c>, <c>@</c>, tab or carriage return) are written with a leading apostrophe, so an
/// exported file cannot run formulas when opened (CSV injection).
/// </summary>
internal static class TabularFile
{
    public static string ContentType(TabularFormat format) => format == TabularFormat.Csv
        ? "text/csv; charset=utf-8"
        : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static string Extension(TabularFormat format) => format == TabularFormat.Csv ? "csv" : "xlsx";

    /// <summary>The format of an uploaded file, from its name.</summary>
    public static TabularFormat? FormatOf(string? fileName) =>
        Path.GetExtension(fileName ?? string.Empty).ToUpperInvariant() switch
        {
            ".CSV" => TabularFormat.Csv,
            ".XLSX" => TabularFormat.Xlsx,
            _ => null,
        };

    public static byte[] Write(TabularFormat format, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows) =>
        format == TabularFormat.Csv ? WriteCsv(header, rows) : WriteXlsx(header, rows);

    /// <summary>Reads at most <paramref name="maxRows"/> rows after the header; blank rows are skipped.</summary>
    public static List<string[]> Read(TabularFormat format, Stream content, int maxRows)
    {
        var rows = format == TabularFormat.Csv ? ReadCsv(content, maxRows + 1) : ReadXlsx(content, maxRows + 1);
        rows.RemoveAll(r => r.All(string.IsNullOrWhiteSpace));
        if (rows.Count > maxRows + 1)
        {
            throw new TabularFileException($"The file has more than {maxRows} rows; split it into smaller files.");
        }

        return rows;
    }

    /// <summary>The cell as written to a file: formulas neutralised.</summary>
    internal static string Safe(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    private static byte[] WriteCsv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        void Line(IEnumerable<string?> cells)
        {
            builder.AppendJoin(',', cells.Select(c => Quote(Safe(c))));
            builder.Append("\r\n");
        }

        Line(header);
        foreach (var row in rows)
        {
            Line(row);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    private static List<string[]> ReadCsv(Stream content, int limit)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when cell.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add([.. row]);
                    row.Clear();
                    if (rows.Count > limit)
                    {
                        return rows;
                    }

                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        if (quoted)
        {
            throw new TabularFileException("The CSV file has a quoted cell that is not closed.");
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }

    private static byte[] WriteXlsx(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var sheetPart = workbook.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            sheetPart.Worksheet = new Worksheet(data);
            var number = 1u;
            data.Append(XlsxRow(number++, header));
            foreach (var row in rows)
            {
                data.Append(XlsxRow(number++, row));
            }

            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = "Zelvo" }));
            workbook.Workbook.Save();
        }

        return stream.ToArray();
    }

    // Inline strings only: every cell is text, so nothing is ever read as a formula or a number.
    private static Row XlsxRow(uint number, IEnumerable<string?> cells) =>
        new([.. cells.Select(value => new Cell
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(Safe(value)) { Space = SpaceProcessingModeValues.Preserve }),
        })])
        { RowIndex = number };

    private static List<string[]> ReadXlsx(Stream content, int limit)
    {
        try
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            buffer.Position = 0;
            using var document = SpreadsheetDocument.Open(buffer, false, new OpenSettings { MaxCharactersInPart = 50_000_000 });
            var workbook = document.WorkbookPart ?? throw new TabularFileException("The Excel file has no workbook.");
            var sheet = workbook.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault()
                ?? throw new TabularFileException("The Excel file has no sheet.");
            var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
            var rows = new List<string[]>();
            foreach (var row in part.Worksheet!.Descendants<Row>())
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = ColumnIndex(cell.CellReference?.Value);
                    while (column is { } index && cells.Count < index)
                    {
                        cells.Add(string.Empty);
                    }

                    cells.Add(CellText(cell, shared));
                }

                rows.Add([.. cells]);
                if (rows.Count > limit)
                {
                    break;
                }
            }

            return rows;
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or InvalidOperationException or FormatException)
        {
            throw new TabularFileException("The file is not a valid Excel workbook (.xlsx).");
        }
    }

    private static string CellText(Cell cell, List<string> shared)
    {
        var value = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count)
        {
            return shared[index];
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        // Formulas are not evaluated: the cached value is used, the formula itself never.
        return value;
    }

    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var index = 0;
        foreach (var c in reference.TakeWhile(char.IsAsciiLetter))
        {
            index = (index * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return index == 0 ? null : index - 1;
    }
}
