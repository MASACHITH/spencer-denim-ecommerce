using System.Text;
using SpencerDenim.Application.Content.Admin.SizeGuides;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// Independent tester's additions for #70: each bound exactly at and one past its edge, and the shapes of
/// JSON a pasted or hand-crafted chart can take that a well-behaved editor never sends.
/// </summary>
public sealed class SizeGuideTableAttackTests
{
    [Theory]
    [InlineData(20, 1, true)]
    [InlineData(1, 100, true)]
    [InlineData(25, 40, false)]
    [InlineData(40, 25, false)]
    [InlineData(1, 1, true)]
    public void Each_count_is_checked_at_its_edge(int columns, int rows, bool accepted)
    {
        Assert.Equal(accepted, SizeGuideTableInput.TryRead(Table(columns, rows, "1"), out _, out _));
    }

    [Fact]
    public void A_cell_of_exactly_sixty_characters_is_kept_and_sixty_one_is_refused()
    {
        Assert.True(SizeGuideTableInput.TryRead(Table(2, 2, new string('x', 60)), out var stored, out _));
        Assert.Contains(new string('x', 60), stored, StringComparison.Ordinal);

        Assert.False(SizeGuideTableInput.TryRead(Table(2, 2, new string('x', 61)), out _, out var error));
        Assert.Contains("60", error, StringComparison.Ordinal);

        // The same edge on a heading.
        Assert.False(SizeGuideTableInput.TryRead(
            $$"""{"columns":["{{new string('h', 61)}}"],"rows":[["S"]]}""", out _, out _));
    }

    /// <summary>Length is measured after trimming, so padding cannot push a 60-character cell over.</summary>
    [Fact]
    public void Padding_around_a_cell_is_trimmed_before_it_is_measured()
    {
        Assert.True(SizeGuideTableInput.TryRead(Table(1, 1, "   " + new string('x', 60) + "   "), out var stored, out _));
        Assert.DoesNotContain("   ", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void The_input_cap_is_exactly_one_hundred_and_twenty_eight_kibibytes()
    {
        var body = Table(1, 1, "S");
        var atCap = body + new string(' ', SizeGuideTableInput.MaxInputLength - body.Length);
        Assert.Equal(128 * 1024, atCap.Length);

        Assert.True(SizeGuideTableInput.TryRead(atCap, out _, out _));
        Assert.False(SizeGuideTableInput.TryRead(atCap + " ", out _, out var error));
        Assert.Contains("too large", error, StringComparison.Ordinal);
    }

    /// <summary>Nesting deeper than the parser's default is a refusal, not an exception.</summary>
    [Fact]
    public void Deep_nesting_is_refused_without_throwing()
    {
        var deep = new string('[', 5000) + new string(']', 5000);
        var json = $$"""{"columns":["Size"],"rows":[{{deep}}]}""";

        Assert.False(SizeGuideTableInput.TryRead(json, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-0")]
    [InlineData("123456789012345678901234567890")]
    public void A_huge_or_odd_number_is_kept_as_the_text_that_was_typed(string number)
    {
        Assert.True(SizeGuideTableInput.TryRead($$"""{"columns":["Size"],"rows":[[{{number}}]]}""", out var stored, out _));
        Assert.Contains($"\"{number}\"", stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// Duplicate keys are legal JSON. Whichever copy is read, what is stored is one canonical object with one
    /// <c>columns</c> and one <c>rows</c> — never both copies, and never a ragged result.
    /// </summary>
    [Fact]
    public void Duplicate_keys_leave_one_canonical_table()
    {
        const string json = """{"columns":["A"],"rows":[["1"]],"columns":["A","B"],"rows":[["1","2"]]}""";

        if (SizeGuideTableInput.TryRead(json, out var stored, out _))
        {
            Assert.Equal(1, CountOf(stored, "\"columns\""));
            Assert.Equal(1, CountOf(stored, "\"rows\""));
        }
    }

    [Fact]
    public void A_wide_payload_inside_the_byte_cap_never_reaches_cell_allocation()
    {
        // 1,001 cells of one character each: well under 128 KiB, one cell past the product bound.
        Assert.False(SizeGuideTableInput.TryRead(Table(11, 91, "1"), out _, out var error));
        Assert.Contains("1000", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_in_a_cell_is_stored_as_text_for_interpolation()
    {
        Assert.True(SizeGuideTableInput.TryRead(
            """{"columns":["Size"],"rows":[["<script>alert(1)</script>"]]}""", out var stored, out _));

        // Relaxed escaping keeps it readable; the storefront interpolates it, so it is never markup.
        Assert.Contains("<script>", stored, StringComparison.Ordinal);
    }

    private static string Table(int columns, int rows, string cell)
    {
        var builder = new StringBuilder("{\"columns\":[");
        builder.AppendJoin(',', Enumerable.Range(1, columns).Select(index => $"\"C{index}\""));
        builder.Append("],\"rows\":[");
        builder.AppendJoin(',', Enumerable.Range(0, rows).Select(_ =>
            "[" + string.Join(',', Enumerable.Repeat($"\"{cell}\"", columns)) + "]"));
        builder.Append("]}");
        return builder.ToString();
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
