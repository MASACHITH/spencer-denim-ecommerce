using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Content.Admin.SizeGuides;

/// <summary>
/// The write-path half of issue #70: what a staff member may save into <c>catalog.SizeGuides.TableJson</c>.
/// <para>
/// <b>The bound is on cells, checked before anything is sized.</b> <see cref="SizeGuideTable"/>'s read
/// path learned that memory grows as columns × rows while the text grows as columns + rows — 146 KiB of
/// JSON allocated 306.5 MiB — so a byte cap alone admits the table that hurts. Here the text length is
/// bounded first (a parser is never handed more than <see cref="MaxInputLength"/>), then the column and
/// row counts are read off the arrays with <c>GetArrayLength</c> and their <b>product</b> is checked
/// against <see cref="MaxCells"/>, all before a single cell is allocated.
/// </para>
/// <para>
/// <b>Tighter than the read path, on purpose.</b> The reader's 40 × 200 is the line beyond which the
/// process is at risk; this is the line beyond which a size chart stops being one. The guide is embedded
/// in the product payload of every product that inherits it, on every request, so a table under the
/// reader's cap can still cost every product page in a department — the point issue #70 makes. A unit test
/// pins that every bound here is inside the reader's, so anything this accepts the storefront can read.
/// </para>
/// <para>
/// <b>Refuse what the reader has to survive.</b> The reader tolerates a ragged row, an object where a
/// cell should be, a header that cannot be named and an unpaired surrogate, because a stored row may have
/// come from anywhere. None of those is accepted here: each is a named error on <c>tableJson</c>, and
/// what is stored is re-serialised in one canonical shape — every cell a string.
/// </para>
/// </summary>
public static class SizeGuideTableInput
{
    /// <summary>The widest chart a staff member may save. Body measurements plus UK/EU/US is about a dozen.</summary>
    public const int MaxColumns = 20;

    /// <summary>The longest chart a staff member may save.</summary>
    public const int MaxRows = 100;

    /// <summary>
    /// Columns × rows. Checked as a product, before anything is allocated, because the product is what
    /// costs memory (see the type remarks). 20 × 50 or 10 × 100, never 20 × 100.
    /// </summary>
    public const int MaxCells = 1000;

    /// <summary>The longest text in one cell or header. "88–93 cm (34½–36½ in)" is 21.</summary>
    public const int MaxCellLength = 60;

    /// <summary>
    /// The longest <c>tableJson</c> handed to the parser at all. Generous for <see cref="MaxCells"/> cells
    /// of <see cref="MaxCellLength"/> characters each with their quoting, and small enough that parsing it
    /// costs nothing worth measuring.
    /// </summary>
    public const int MaxInputLength = 128 * 1024;

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Reads and checks a table. On success <paramref name="canonicalJson"/> is what to store and
    /// <paramref name="error"/> is null; on failure it is the sentence for the <c>tableJson</c> field.
    /// </summary>
    public static bool TryRead(string? tableJson, out string canonicalJson, out string? error)
    {
        canonicalJson = string.Empty;

        if (string.IsNullOrWhiteSpace(tableJson))
        {
            error = "Add the size table: at least one column and one row.";
            return false;
        }

        if (tableJson.Length > MaxInputLength)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"The size table is too large. Keep it to {MaxColumns} columns, {MaxRows} rows and {MaxCells} cells.");
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(tableJson);
        }
        // An unpaired surrogate cannot be transcoded to UTF-8 and arrives as ArgumentException rather than
        // JsonException — the read path's own lesson (SizeGuideTable.Parse) applied on the way in.
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            error = "The size table could not be read. Re-enter it, and avoid pasting symbols from a word processor.";
            return false;
        }

        using (document)
        {
            try
            {
                return TryReadRoot(document.RootElement, out canonicalJson, out error);
            }
            // `\uD800` written as an escape parses, and only fails when the string is unescaped.
            catch (InvalidOperationException)
            {
                error = "The size table holds a character that cannot be stored. Re-type the cell it came from.";
                return false;
            }
        }
    }

    private static bool TryReadRoot(JsonElement root, out string canonicalJson, out string? error)
    {
        canonicalJson = string.Empty;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("columns", out var columnsElement)
            || columnsElement.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("rows", out var rowsElement)
            || rowsElement.ValueKind != JsonValueKind.Array)
        {
            error = "The size table needs a list of column headings and a list of rows.";
            return false;
        }

        var columnCount = columnsElement.GetArrayLength();
        var rowCount = rowsElement.GetArrayLength();

        if (columnCount == 0 || rowCount == 0)
        {
            error = "Add at least one column and one row to the size table.";
            return false;
        }

        // Every count is checked before any list is sized from it; the product last, because it is the
        // one that matters (type remarks).
        if (columnCount > MaxColumns || rowCount > MaxRows || (long)columnCount * rowCount > MaxCells)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"The size table has {columnCount} columns and {rowCount} rows. Keep it to {MaxColumns} columns, "
                + $"{MaxRows} rows and {MaxCells} cells in all.");
            return false;
        }

        var columns = new List<string>(columnCount);
        foreach (var column in columnsElement.EnumerateArray())
        {
            if (Cell(column) is not { } heading || heading.Length == 0)
            {
                error = "Every column of the size table needs a heading.";
                return false;
            }

            if (heading.Length > MaxCellLength)
            {
                error = TooLong(heading);
                return false;
            }

            columns.Add(heading);
        }

        var rows = new List<string[]>(rowCount);
        var rowNumber = 0;
        foreach (var row in rowsElement.EnumerateArray())
        {
            rowNumber++;

            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != columnCount)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Row {rowNumber} of the size table must have exactly {columnCount} cells, one per column.");
                return false;
            }

            var cells = new string[columnCount];
            var index = 0;
            foreach (var cell in row.EnumerateArray())
            {
                if (Cell(cell) is not { } text)
                {
                    error = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Row {rowNumber} of the size table holds something that is not text.");
                    return false;
                }

                if (text.Length > MaxCellLength)
                {
                    error = TooLong(text);
                    return false;
                }

                cells[index++] = text;
            }

            rows.Add(cells);
        }

        canonicalJson = JsonSerializer.Serialize(new { columns, rows }, CanonicalOptions);
        error = null;
        return true;
    }

    /// <summary>A cell as trimmed text: a string or a number, and nothing else. Control characters refused.</summary>
    private static string? Cell(JsonElement element)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };

        if (text is null)
        {
            return null;
        }

        text = text.Trim();

        return text.Any(char.IsControl) ? null : text;
    }

    private static string TooLong(string text) => string.Create(
        CultureInfo.InvariantCulture,
        $"Keep each cell of the size table to {MaxCellLength} characters (\"{text[..Math.Min(text.Length, 20)]}…\" is longer).");
}
