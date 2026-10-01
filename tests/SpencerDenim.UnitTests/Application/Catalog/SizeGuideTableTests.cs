using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Application.Catalog;

/// <summary>
/// PDP-04, issue #32: <c>catalog.SizeGuides.TableJson</c> is <c>nvarchar(max)</c> behind an
/// <c>ISJSON</c> check, which guarantees the text parses and nothing about its shape. Everything here
/// is about the same promise: an unreadable table costs the product page its size guide and never the
/// page itself.
/// </summary>
public sealed class SizeGuideTableTests
{
    [Fact]
    public void A_seeded_guide_parses_into_its_real_measurement_table()
    {
        var women = TableJsonFor("Women's apparel");

        var table = SizeGuideTable.Parse(women);

        Assert.NotNull(table);
        Assert.Equal(["Size", "Bust", "Waist", "Hip"], table.Columns);
        Assert.Equal(6, table.Rows.Count);
        Assert.Equal(["XS", "78-82", "60-64", "86-90"], table.Rows[0]);
        Assert.Equal(["XXL", "107-113", "89-95", "115-121"], table.Rows[^1]);
        Assert.All(table.Rows, row => Assert.Equal(table.Columns.Count, row.Count));
    }

    [Theory]
    [InlineData("Women's apparel", 4, 6)]
    [InlineData("Men's apparel", 4, 5)]
    [InlineData("Kids and baby", 4, 6)]
    public void All_three_seeded_guides_parse(string name, int columns, int rows)
    {
        var table = SizeGuideTable.Parse(TableJsonFor(name));

        Assert.NotNull(table);
        Assert.Equal(columns, table.Columns.Count);
        Assert.Equal(rows, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.All(row, cell => Assert.False(string.IsNullOrWhiteSpace(cell))));
    }

    /// <summary>
    /// Every one of these is something the column can legally hold. <c>{}</c> is the property's own
    /// default value, so it is the case a guide an admin has created and not filled in will be in.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"a string is valid JSON\"")]
    [InlineData("""[{"columns":["Size"],"rows":[["S"]]}]""")]
    [InlineData("""{"rows":[["S","88-93"]]}""")]
    [InlineData("""{"columns":42,"rows":[["S"]]}""")]
    [InlineData("""{"columns":[],"rows":[["S"]]}""")]
    [InlineData("""{"columns":["Size"],"rows":[]}""")]
    [InlineData("""{"columns":["Size"]}""")]
    [InlineData("""{"columns":["Size"],"rows":"S"}""")]
    [InlineData("""{"columns":[{"label":"Size"}],"rows":[["S"]]}""")]
    [InlineData("""{"columns":[["Size"]],"rows":[["S"]]}""")]
    [InlineData("""{"COLUMNS":["Size"],"ROWS":[["S"]]}""")]
    public void Anything_without_a_readable_grid_is_no_table_rather_than_an_exception(string? tableJson)
    {
        Assert.Null(SizeGuideTable.Parse(tableJson));
    }

    /// <summary>
    /// <c>ISJSON</c> should make this unreachable, and the check constraint lives in one database while
    /// this code runs against every copy of it — including one somebody has restored or edited by hand.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"columns\":[\"Size\"],")]
    [InlineData("{'columns':['Size']}")]
    [InlineData("<html>columns</html>")]
    public void Text_that_is_not_json_at_all_is_no_table_rather_than_an_exception(string tableJson)
    {
        Assert.Null(SizeGuideTable.Parse(tableJson));
    }

    [Fact]
    public void A_short_row_is_padded_so_the_grid_stays_rectangular()
    {
        var table = SizeGuideTable.Parse(
            """{"columns":["Size","Chest","Waist"],"rows":[["S","88-93","74-79"],["M","94-99"],["L"]]}""");

        Assert.NotNull(table);
        Assert.Equal(["M", "94-99", ""], table.Rows[1]);
        Assert.Equal(["L", "", ""], table.Rows[2]);
        Assert.All(table.Rows, row => Assert.Equal(3, row.Count));
    }

    /// <summary>
    /// The alternative was widening the header to fit, which puts a column on the screen with a name
    /// nobody wrote.
    /// </summary>
    [Fact]
    public void A_row_with_more_cells_than_the_header_is_truncated_to_the_header()
    {
        var table = SizeGuideTable.Parse(
            """{"columns":["Size","Chest"],"rows":[["S","88-93","74-79","extra"]]}""");

        Assert.NotNull(table);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(["S", "88-93"], table.Rows[0]);
    }

    [Fact]
    public void One_unreadable_row_is_dropped_without_taking_the_table_with_it()
    {
        var table = SizeGuideTable.Parse(
            """{"columns":["Size","Chest"],"rows":[["S","88-93"],"M",{"size":"L"},null,["XL","106-112"]]}""");

        Assert.NotNull(table);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["S", "88-93"], table.Rows[0]);
        Assert.Equal(["XL", "106-112"], table.Rows[1]);
    }

    /// <summary>
    /// A hand-written table will contain bare numbers. They are kept verbatim: parsing one into a
    /// <see cref="decimal"/> and formatting it back is how "32.0" turns up where somebody typed 32.
    /// </summary>
    [Fact]
    public void A_non_string_cell_becomes_text_rather_than_losing_the_row()
    {
        var table = SizeGuideTable.Parse(
            """{"columns":["Size","Sleeve","Lined","Note"],"rows":[["M",62,true,null],["L",64.5,false,["x"]]]}""");

        Assert.NotNull(table);
        Assert.Equal(["M", "62", "true", ""], table.Rows[0]);
        Assert.Equal(["L", "64.5", "false", ""], table.Rows[1]);
    }

    /// <summary>
    /// An unpaired surrogate — the one input that does not fail inside the reader at all.
    /// <para>
    /// <c>JsonDocument.Parse(string)</c> transcodes UTF-16 to UTF-8 <b>before</b> it parses, and
    /// transcoding a lone surrogate throws <see cref="ArgumentException"/>, not
    /// <see cref="JsonException"/> — so a catch written for malformed JSON never saw it. Every other
    /// case in this class fails after the text has been decoded, which is exactly why none of them
    /// could find this one.
    /// </para>
    /// <para>
    /// <b>And <c>ISJSON</c> accepts it</b>, both as a raw <c>NCHAR(0xD800)</c> and as the
    /// <c>\uD800</c> escape, so the check constraint is not the guard it looks like. One character
    /// pasted out of a word processor was an HTTP 500 on <i>every</i> product page inheriting that
    /// guide, while the listings carried on answering 200 — the shop looking healthy with its product
    /// pages gone.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unpaired_surrogate_is_no_table_rather_than_an_exception()
    {
        const char loneHigh = '\uD800';
        const char loneLow = '\uDC00';

        // Raw lone surrogates: inside a header, inside a cell, trailing the document, and alone.
        string[] raw =
        [
            $"{{\"columns\":[\"Si{loneHigh}ze\"],\"rows\":[[\"S\"]]}}",
            $"{{\"columns\":[\"Size\"],\"rows\":[[\"S{loneLow}\"]]}}",
            $"{{\"columns\":[\"Size\"],\"rows\":[[\"S\"]]}}{loneHigh}",
            loneLow.ToString(),
            $"{loneHigh}{loneHigh}",
        ];

        foreach (var tableJson in raw)
        {
            Assert.Null(SizeGuideTable.Parse(tableJson));
        }

        // The escaped form is six ordinary characters in the column, so it transcodes and parses
        // perfectly and then throws when the string is *unescaped* — an InvalidOperationException from
        // inside the walk, not from Parse. A different place, a different exception type, and the same
        // HTTP 500, so it needs its own cases.
        //
        // A bad header takes the table, because a column cannot be left unnamed.
        Assert.Null(SizeGuideTable.Parse("""{"columns":["\uD800"],"rows":[["S"]]}"""));

        // A bad property name must not throw either, whatever it resolves to.
        Assert.Null(SizeGuideTable.Parse("""{"col\uD800umns":["Size"],"rows":[["S"]]}"""));

        // A bad *cell* costs only that cell: the measurements either side of it still reach the shopper.
        var survivor = SizeGuideTable.Parse(
            """{"columns":["Size","Chest","Waist"],"rows":[["S","\uDC00","74-79"],["M","94-99","80-85"]]}""");

        Assert.NotNull(survivor);
        Assert.Equal(["Size", "Chest", "Waist"], survivor.Columns);
        Assert.Equal(["S", "", "74-79"], survivor.Rows[0]);
        Assert.Equal(["M", "94-99", "80-85"], survivor.Rows[1]);
    }

    /// <summary>
    /// The real seeded value, read from <c>CatalogReferenceData</c> rather than copied, so editing a
    /// seeded guide into a shape this cannot read fails here and not on a shopper's screen.
    /// </summary>
    private static string TableJsonFor(string name) =>
        CatalogReferenceData.SizeGuides.Single(guide => guide.Name == name).TableJson;

    [Fact]
    public void A_blank_column_header_is_a_column_an_admin_left_unlabelled_not_a_broken_table()
    {
        var table = SizeGuideTable.Parse("""{"columns":["Size",""],"rows":[["S","see note"]]}""");

        Assert.NotNull(table);
        Assert.Equal(["Size", ""], table.Columns);
        Assert.Equal(["S", "see note"], table.Rows[0]);
    }
}
