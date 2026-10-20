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
/// <b>The grid is bounded — see <see cref="MaxColumns"/> — because the bound that matters is not the
/// one that is obvious.</b> Memory here grows as columns × rows while the JSON grows as columns +
/// rows, so 146 KiB of text allocates 306.5 MiB and a byte cap would not have caught it. An earlier
/// version of this comment said there was deliberately no limit, on the grounds that capping would
/// truncate a measurement; that was wrong twice over — over the cap the whole table goes, which is a
/// loss this type already takes elsewhere, and the thing being avoided was an
/// <see cref="OutOfMemoryException"/> rather than a wide table.
/// </para>
/// <para>
/// A cap here does not retire the write-path bound (<b>issue #70</b>): a table under the cap can still
/// be large enough to matter, because the content is embedded on the product payload and is paid for
/// by every product inheriting that guide, on every request. That is a question about what an admin
/// may save; this is only the guard that stops a bad row count costing the process.
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
    /// The widest and longest grid this will build. Beyond either, the table goes.
    /// <para>
    /// <b>The bound is the cell count, not the byte count, and that is the whole point.</b> Every row
    /// allocates <c>columns.Count</c> strings whether the JSON held that many cells or not, so memory
    /// grows as columns × rows while the text grows as columns + rows. Measured: 2,000 columns by
    /// 20,000 <i>empty</i> rows is <b>146 KiB</b> of JSON and <b>306.5 MiB</b> allocated — a factor of
    /// 2,145. A byte cap of a megabyte would still have admitted several gigabytes, and the failure is
    /// an <see cref="OutOfMemoryException"/>, which none of the three guards above catches and which
    /// costs the whole process rather than one department's pages.
    /// </para>
    /// <para>
    /// 40 and 200 because they are far outside any garment chart and nowhere near the allocation that
    /// hurts: the seeded guides are 4 columns by 5–6 rows, and a generous real one — body measurements
    /// plus UK/EU/US equivalents — reaches perhaps a dozen columns. The ceiling is 8,000 cells, which
    /// is a table no shopper could read and a few hundred kilobytes at worst.
    /// </para>
    /// <para>
    /// Over the cap the whole table goes, rather than being truncated to fit. Truncating would publish
    /// a measurement chart that silently disagrees with the one the admin saved, which is worse than no
    /// chart; losing it is the same loss this type already takes for a table it cannot read, and the
    /// bound belongs on the write path regardless (issue #70).
    /// </para>
    /// </summary>
    public const int MaxColumns = 40;

    /// <inheritdoc cref="MaxColumns"/>
    public const int MaxRows = 200;

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

        // Checked before the list is sized, because `GetArrayLength` is the attacker's number: sizing a
        // list from it is itself the allocation being guarded against.
        if (element.GetArrayLength() > MaxColumns)
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

        // Same reason as the column check, and this is the one that matters: every row below allocates
        // `columnCount` strings whether the JSON held that many cells or not, so the memory is the
        // product while the text is the sum.
        if (element.GetArrayLength() > MaxRows)
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
