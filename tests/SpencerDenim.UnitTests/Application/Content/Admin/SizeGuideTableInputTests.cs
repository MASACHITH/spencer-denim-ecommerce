using System.Text;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Content.Admin.SizeGuides;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// Issue #70's first half: what a staff member may save into <c>catalog.SizeGuides.TableJson</c>. The
/// read path survives anything; the write path refuses what the read path only survives.
/// </summary>
public sealed class SizeGuideTableInputTests
{
    [Fact]
    public void A_well_formed_table_is_stored_in_one_canonical_shape()
    {
        var ok = SizeGuideTableInput.TryRead(
            """{ "columns": [" Size ", "Chest (cm)"], "rows": [["S", 86], ["M", "91–96"]] }""",
            out var canonical,
            out var error);

        Assert.True(ok, error);
        Assert.Null(error);

        // Trimmed, every cell a string, and nothing that could make the reader pad or truncate.
        Assert.Equal("""{"columns":["Size","Chest (cm)"],"rows":[["S","86"],["M","91–96"]]}""", canonical);
    }

    /// <summary>Whatever the write path accepts the storefront must read back exactly.</summary>
    [Fact]
    public void Anything_accepted_reads_back_identically_on_the_product_page()
    {
        var json = Table(SizeGuideTableInput.MaxColumns, SizeGuideTableInput.MaxCells / SizeGuideTableInput.MaxColumns, "x");
        Assert.True(SizeGuideTableInput.TryRead(json, out var canonical, out _));

        var read = SizeGuideTable.Parse(canonical);

        Assert.NotNull(read);
        Assert.Equal(SizeGuideTableInput.MaxColumns, read.Columns.Count);
        Assert.Equal(SizeGuideTableInput.MaxCells / SizeGuideTableInput.MaxColumns, read.Rows.Count);
    }

    /// <summary>
    /// The write bounds sit inside the read bounds, so a chart the admin saves is never one the product page
    /// drops for being too big.
    /// </summary>
    [Fact]
    public void The_write_bounds_are_inside_the_read_bounds()
    {
        Assert.True(SizeGuideTableInput.MaxColumns <= SizeGuideTable.MaxColumns);
        Assert.True(SizeGuideTableInput.MaxRows <= SizeGuideTable.MaxRows);
        Assert.True(SizeGuideTableInput.MaxCells < SizeGuideTableInput.MaxColumns * SizeGuideTableInput.MaxRows);
    }

    [Theory]
    [InlineData(20, 50, true)]
    [InlineData(10, 100, true)]
    [InlineData(21, 1, false)]
    [InlineData(1, 101, false)]
    [InlineData(20, 51, false)]
    [InlineData(11, 100, false)]
    public void The_bound_is_on_columns_rows_and_their_product(int columns, int rows, bool accepted)
    {
        var ok = SizeGuideTableInput.TryRead(Table(columns, rows, "1"), out _, out var error);

        Assert.Equal(accepted, ok);
        if (!accepted)
        {
            Assert.Contains("columns", error, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// W1-16's measurement, on the way in: 2,000 columns by 20,000 empty rows is 146 KiB of text and 306.5 MiB
    /// once sized. Here it is refused on its counts, read off the arrays before a single row is sized.
    /// </summary>
    [Fact]
    public void The_payload_that_allocated_three_hundred_megabytes_is_refused_before_it_is_parsed()
    {
        var builder = new StringBuilder("{\"columns\":[");
        builder.Append(string.Join(',', Enumerable.Repeat("\"c\"", 2_000)));
        builder.Append("],\"rows\":[");
        builder.Append(string.Join(',', Enumerable.Repeat("[]", 20_000)));
        builder.Append("]}");

        Assert.False(SizeGuideTableInput.TryRead(builder.ToString(), out _, out var error));
        Assert.Contains("2000 columns and 20000 rows", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_longer_than_the_input_cap_never_reaches_the_parser()
    {
        var json = "{\"columns\":[\"" + new string('a', SizeGuideTableInput.MaxInputLength) + "\"],\"rows\":[[\"x\"]]}";

        Assert.False(SizeGuideTableInput.TryRead(json, out _, out var error));
        Assert.Contains("too large", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_payload_with_a_huge_shape_is_refused_by_its_counts()
    {
        // Under the length cap, over every count: the counts are read off the arrays before anything is sized.
        var json = "{\"columns\":[" + string.Join(',', Enumerable.Repeat("\"c\"", 500)) + "],\"rows\":[[]]}";

        Assert.False(SizeGuideTableInput.TryRead(json, out _, out var error));
        Assert.Contains("500 columns", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Add the size table")]
    [InlineData("  ", "Add the size table")]
    [InlineData("not json", "could not be read")]
    [InlineData("[]", "headings")]
    [InlineData("""{"rows":[["S"]]}""", "headings")]
    [InlineData("""{"columns":"Size","rows":[]}""", "headings")]
    [InlineData("""{"columns":[],"rows":[["S"]]}""", "at least one")]
    [InlineData("""{"columns":["Size"],"rows":[]}""", "at least one")]
    [InlineData("""{"columns":["Size",""],"rows":[["S","1"]]}""", "heading")]
    [InlineData("""{"columns":["Size",{"x":1}],"rows":[["S","1"]]}""", "heading")]
    [InlineData("""{"columns":["Size","Chest"],"rows":[["S"]]}""", "exactly 2 cells")]
    [InlineData("""{"columns":["Size","Chest"],"rows":[["S","1","2"]]}""", "exactly 2 cells")]
    [InlineData("""{"columns":["Size"],"rows":["S"]}""", "exactly 1 cells")]
    [InlineData("""{"columns":["Size"],"rows":[[["S"]]]}""", "not text")]
    [InlineData("""{"columns":["Size"],"rows":[[true]]}""", "not text")]
    [InlineData("""{"columns":["Size"],"rows":[[null]]}""", "not text")]
    [InlineData("""{"columns":["Size"],"rows":[["a\tb"]]}""", "not text")]
    public void What_the_reader_only_survives_is_refused_on_the_way_in(string? json, string message)
    {
        Assert.False(SizeGuideTableInput.TryRead(json, out var canonical, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, canonical);
    }

    [Fact]
    public void A_cell_longer_than_sixty_characters_is_refused()
    {
        var at = $$"""{"columns":["Size"],"rows":[["{{new string('a', 60)}}"]]}""";
        var over = $$"""{"columns":["Size"],"rows":[["{{new string('a', 61)}}"]]}""";

        Assert.True(SizeGuideTableInput.TryRead(at, out _, out _));
        Assert.False(SizeGuideTableInput.TryRead(over, out _, out var error));
        Assert.Contains("60 characters", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unpaired surrogate that once 500'd every product page in a department, in both its forms: raw
    /// (cannot be transcoded, so it never reaches the parser) and escaped (parses, then fails to unescape).
    /// </summary>
    [Fact]
    public void An_unpaired_surrogate_is_refused_in_either_spelling()
    {
        var raw = "{\"columns\":[\"Size\"],\"rows\":[[\"" + '\uD800' + "\"]]}";
        var escaped = """{"columns":["Size"],"rows":[["\uD800"]]}""";
        var inHeader = """{"columns":["\uDC00"],"rows":[["S"]]}""";

        Assert.False(SizeGuideTableInput.TryRead(raw, out _, out var rawError));
        Assert.False(SizeGuideTableInput.TryRead(escaped, out _, out var escapedError));
        Assert.False(SizeGuideTableInput.TryRead(inHeader, out _, out var headerError));

        Assert.NotNull(rawError);
        Assert.Contains("cannot be stored", escapedError, StringComparison.Ordinal);
        Assert.Contains("cannot be stored", headerError, StringComparison.Ordinal);
    }

    private static string Table(int columns, int rows, string cell)
    {
        var header = string.Join(',', Enumerable.Range(1, columns).Select(index => $"\"C{index}\""));
        var row = "[" + string.Join(',', Enumerable.Repeat($"\"{cell}\"", columns)) + "]";

        return "{\"columns\":[" + header + "],\"rows\":[" + string.Join(',', Enumerable.Repeat(row, rows)) + "]}";
    }
}
