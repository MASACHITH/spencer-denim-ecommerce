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
/// <b>There is deliberately no size limit here.</b> The parser handles an enormous table perfectly
/// well — 20,000 columns, 10,000 rows and a 9.5 MiB value were all measured — but the content is
/// embedded on the product payload, so a 10 MB table makes a 10.2 MB product page taking 9–19 s, for
/// every product inheriting that guide, on every request. That is a question about what an admin may
/// save, not about what this may read, so it is bounded on the write path: <b>issue #70</b>, owed
/// before ADM-11 ships. Capping it here would silently truncate a measurement instead.
/// </para>
/// <para>
/// <b>And <c>ISJSON</c> guarantees less than it appears to even about parsing.</b> It accepts an
/// unpaired surrogate, in both the raw and the escaped form, and the two break in different places —
/// one cannot be transcoded before parsing, the other parses and then cannot be unescaped. The first
/// version of this caught <see cref="JsonException"/> alone and so 500'd every product page in a
/// department on one bad character. That is why there are <b>three</b> guards and not one: the catch in
/// <see cref="Parse"/>, the backstop around the walk, and <see cref="ToCell"/>.
/// </para>
/// <para>
/// Every unusable input therefore resolves to <see langword="null"/>, which <c>hasSizeGuideContent</c>
/// on the storefront already treats as "no table": not an object at the root, no <c>columns</c> array,
/// an empty <c>columns</c>, a column header that is not a scalar or cannot be unescaped, no readable
/// row, or text that is not JSON at all (which <c>ISJSON</c> should prevent and a restored or
/// hand-edited database can still hold). A single unreadable <i>cell</i> is the one case that costs
/// less than the whole table: it goes blank and its neighbours survive.
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
        // Two different failures, in two different steps, and only the first one looks like a JSON
        // problem:
        //
        //   JsonException   — the text does not parse. ISJSON is supposed to make this unreachable, and
        //                     it is caught rather than trusted because the check constraint lives in one
        //                     database while this code runs against every copy of it.
        //   ArgumentException — the text never reaches the parser. Parse(string) transcodes UTF-16 to
        //                     UTF-8 first, and an unpaired surrogate cannot be transcoded:
        //                     "Cannot transcode invalid UTF-16 string to UTF-8 JSON text". ISJSON
        //                     accepts an unpaired surrogate, as a raw NCHAR(0xD800) and as the \uD800
        //                     escape alike, so the constraint is no guard at all here.
        //
        // The second one was missing, and the cost was out of all proportion to the input: one character
        // pasted out of a word processor into one guide's table meant HTTP 500 on *every* product page
        // inheriting that guide, while the listings carried on answering 200 — doc 06 L1, the shop
        // looking healthy with its product pages gone.
        //
        // Deliberately not "repaired" by re-encoding with a replacement fallback: that would keep the
        // table and silently put U+FFFD in a measurement, and it would put an encode on the hot path of
        // every product page to rescue data that is broken anyway. Losing the table is the right loss.
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }

        using (document)
        {
            try
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
            // The backstop for the *third* way this input throws, which is the surprising one: a lone
            // surrogate written as the escape `\uD800` is six ordinary characters, so it transcodes and
            // parses perfectly, and then blows up later when a string is unescaped —
            // "Cannot read incomplete UTF-16 JSON text as string with missing low surrogate", an
            // InvalidOperationException from somewhere down inside the walk rather than from Parse.
            //
            // ToCell handles the cells it can (see there), so this catch exists for the paths it does not
            // own — a property name carrying the same escape, and anything a later edit adds — because
            // the promise in this type's summary has to hold for the whole function and not just for the
            // lines somebody remembered.
            catch (InvalidOperationException)
            {
                return null;
            }
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
    /// One cell as text, or <see langword="null"/> when the value is not a scalar — or is a string that
    /// cannot be turned into one.
    /// <para>
    /// A number is kept verbatim rather than parsed and reformatted: a cell reads "88-93" or "60", and
    /// round-tripping it through a <see cref="decimal"/> is how "32.0" appears where someone typed 32.
    /// </para>
    /// <para>
    /// <b><c>GetString()</c> throws on a string holding an escaped lone surrogate</b>
    /// (<c>"\uD800"</c>), which parses as valid JSON and only fails when it is unescaped. Answering
    /// <see langword="null"/> for it puts that string exactly where a non-scalar already goes, so the
    /// existing rules apply unchanged and one bad character costs as little as it can: a bad
    /// <i>header</i> takes the table, because a column cannot be left unnamed, while a bad <i>cell</i>
    /// becomes blank and the other 23 measurements still reach the shopper.
    /// </para>
    /// </summary>
    private static string? ToCell(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => TryGetString(element),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => null,
    };

    /// <summary>
    /// The string, or <see langword="null"/> when unescaping it is impossible. Only an unpaired
    /// surrogate escape gets here: the element is known to be a <see cref="JsonValueKind.String"/>, so
    /// the wrong-token-type reason for <see cref="InvalidOperationException"/> cannot arise.
    /// </summary>
    private static string? TryGetString(JsonElement element)
    {
        try
        {
            return element.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
