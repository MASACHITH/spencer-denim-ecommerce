using System.Text;
using SpencerDenim.Application.Admin.Exports;

namespace SpencerDenim.UnitTests.Application.Admin.Exports;

/// <summary>
/// The CSV writer every admin export uses (ADM-21 first): formula injection, quoting, encoding.
/// </summary>
public sealed class CsvDocumentTests
{
    [Theory]
    [InlineData("=1+2@example.com")]
    [InlineData("+94771234567@example.com")]
    [InlineData("-2+3@example.com")]
    [InlineData("@SUM(A1:A2)")]
    [InlineData("\t=1+2")]
    [InlineData("\r=1+2")]
    [InlineData("  =cmd|' /C calc'!A0")]
    public void A_value_a_spreadsheet_would_run_is_prefixed_with_a_single_quote(string hostile)
    {
        var cell = CsvDocument.Cell(hostile);

        Assert.StartsWith("\"'", cell, StringComparison.Ordinal);
        Assert.True(CsvDocument.StartsLikeAFormula(hostile));
    }

    [Theory]
    [InlineData("nimali@example.com")]
    [InlineData("2026-10-01 10:00")]
    [InlineData("footer")]
    [InlineData("a=b@example.com")]
    [InlineData("නිමාලි@example.lk")]
    public void An_ordinary_value_is_left_alone(string ordinary)
    {
        Assert.False(CsvDocument.StartsLikeAFormula(ordinary));
        Assert.Equal($"\"{ordinary}\"", CsvDocument.Cell(ordinary));
    }

    [Fact]
    public void Quotes_are_doubled_so_a_value_cannot_close_its_own_cell()
    {
        Assert.Equal("\"say \"\"hi\"\", then, go\"", CsvDocument.Cell("say \"hi\", then, go"));
    }

    /// <summary>The quote goes inside the quotes, before an escaped quote, so the neutraliser cannot be undone by one.</summary>
    [Fact]
    public void A_hostile_value_with_quotes_is_neutralised_and_escaped()
    {
        Assert.Equal(
            "\"'=HYPERLINK(\"\"http://evil.example\"\")\"",
            CsvDocument.Cell("=HYPERLINK(\"http://evil.example\")"));
    }

    [Fact]
    public void An_empty_or_null_cell_is_an_empty_quoted_cell()
    {
        Assert.Equal("\"\"", CsvDocument.Cell(null));
        Assert.Equal("\"\"", CsvDocument.Cell(string.Empty));
    }

    /// <summary>
    /// The file starts with the UTF-8 byte-order mark — what makes Excel read Sinhala and Tamil correctly —
    /// rows end CRLF, and the text decodes back byte for byte.
    /// </summary>
    [Fact]
    public void The_file_is_UTF8_with_a_BOM_and_CRLF_rows()
    {
        var bytes = new CsvDocument()
            .Row("Email", "Name")
            .Row("nimali@example.lk", "නිමාලි පෙරේරා")
            .Row("kavitha@example.lk", "கவிதா ராஜா")
            .ToBytes();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);

        Assert.Equal(
            "\"Email\",\"Name\"\r\n"
            + "\"nimali@example.lk\",\"නිමාලි පෙරේරා\"\r\n"
            + "\"kavitha@example.lk\",\"கவிதா ராஜா\"\r\n",
            text);
    }

    [Fact]
    public void A_line_break_inside_a_value_stays_inside_its_cell()
    {
        var bytes = new CsvDocument().Row("one\r\ntwo", "three").ToBytes();
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        Assert.Equal("\"one\r\ntwo\",\"three\"\r\n", text);
    }
}
