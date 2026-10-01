using System.Text.Json;

namespace SpencerDenim.Application.Catalog.Contracts;

/// <summary>
/// Turns <c>catalog.SizeGuides.TableJson</c> into the grid the PDP-04 modal draws.
/// <para>
/// <b>Nothing here throws, and that is the whole point of it being a separate function.</b> The column
/// is <c>nvarchar(max)</c> guarded only by <c>ISJSON</c> (doc 08 §1.4, DB15), which says the text
/// parses as JSON and nothing whatever about its shape: <c>{}</c> satisfies it, so does <c>[]</c>, so
/// does <c>{"columns":42}</c>, and the column's own default value is <c>{}</c>. A measurement table
/// that cannot be read is a reason to show the product page without a size guide; it is never a reason
/// to fail the product page, which is the only page the shopper actually came for.
/// </para>
/// <para>
/// Every unusable input therefore resolves to <see langword="null"/>, which <c>hasSizeGuideContent</c>
/// on the storefront already treats as "no table": not an object at the root, no <c>columns</c> array,
/// an empty <c>columns</c>, a column header that is not a scalar, no readable row, or text that is not
/// JSON at all (which <c>ISJSON</c> should prevent and a restored or hand-edited database can still
/// hold).
/// </para>
/// </summary>
public static class SizeGuideTable
{
    private const string ColumnsProperty = "columns";
    private const string RowsProperty = "rows";

    /// <summary>
    /// The grid, or <see langword="null"/> when the stored text holds no table a shopper could read.
    /// </summary>
    /// <param name="tableJson">The raw column value. <see langword="null"/>, empty and blank are all fine.</param>
    public static SizeGuideTableDto? Parse(string? tableJson)
    {
        if (string.IsNullOrWhiteSpace(tableJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(tableJson);
        }
        catch (JsonException)
        {
            // ISJSON is supposed to make this unreachable. It is caught rather than trusted because the
            // check constraint lives in one database and this code runs against every copy of it.
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var columns = ReadColumns(root);
            if (columns is null)
            {
                return null;
            }

            var rows = ReadRows(root, columns.Count);
            return rows.Count == 0 ? null : new SizeGuideTableDto(columns, rows);
        }
    }

    /// <summary>The header row, or <see langword="null"/> when there is not a usable one.</summary>
    private static List<string>? ReadColumns(JsonElement root)
    {
        if (!root.TryGetProperty(ColumnsProperty, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var columns = new List<string>(element.GetArrayLength());
        foreach (var column in element.EnumerateArray())
        {
            // A header that is an object or an array cannot be labelled, and inventing a name for it
            // would put a word on the screen that nobody wrote. The table goes instead of the header.
            if (ToCell(column) is not { } label)
            {
                return null;
            }

            columns.Add(label);
        }

        return columns.Count == 0 ? null : columns;
    }

    /// <summary>
    /// The body, every row exactly <paramref name="columnCount"/> cells wide.
    /// <para>
    /// A short row is padded with blanks and a long one is truncated, because a <c>&lt;tr&gt;</c> with
    /// more cells than the header has nowhere to put them and widening the header instead would invent
    /// column names. A row that is not an array at all is dropped on its own rather than taking the
    /// rest of the table with it.
    /// </para>
    /// </summary>
    private static List<IReadOnlyList<string>> ReadRows(JsonElement root, int columnCount)
    {
        if (!root.TryGetProperty(RowsProperty, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<IReadOnlyList<string>>(element.GetArrayLength());
        foreach (var row in element.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var cells = new string[columnCount];
            var index = 0;

            foreach (var cell in row.EnumerateArray())
            {
                if (index == columnCount)
                {
                    break;
                }

                cells[index++] = ToCell(cell) ?? string.Empty;
            }

            while (index < columnCount)
            {
                cells[index++] = string.Empty;
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// One cell as text, or <see langword="null"/> when the value is not a scalar.
    /// <para>
    /// A number is kept verbatim rather than parsed and reformatted: a cell reads "88-93" or "60", and
    /// round-tripping it through a <see cref="decimal"/> is how "32.0" appears where someone typed 32.
    /// </para>
    /// </summary>
    private static string? ToCell(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => null,
    };
}
