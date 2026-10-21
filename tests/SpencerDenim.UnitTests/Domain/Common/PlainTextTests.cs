using SpencerDenim.Domain.Common;

namespace SpencerDenim.UnitTests.Domain.Common;

/// <summary>
/// <see cref="PlainText"/>'s two zero-width characters (issue #78 tester pass: a search term is the third caller,
/// after review text and customer/review names) — and that ZWJ/ZWNJ, which Sinhala and Tamil conjuncts need
/// (#189 D1), are deliberately not among them.
/// </summary>
public sealed class PlainTextTests
{
    [Theory]
    [InlineData('​')]
    [InlineData('﻿')]
    public void A_zero_width_character_is_invisible_formatting(char character) =>
        Assert.True(PlainText.IsInvisibleFormatting(character));

    [Theory]
    [InlineData('‌')]
    [InlineData('‍')]
    [InlineData('a')]
    [InlineData(' ')]
    public void A_shaping_character_or_an_ordinary_one_is_not(char character) =>
        Assert.False(PlainText.IsInvisibleFormatting(character));

    [Fact]
    public void HasUnsafeForOneLine_now_objects_to_the_zero_width_characters_too()
    {
        Assert.True(PlainText.HasUnsafeForOneLine("wide​leg"));
        Assert.True(PlainText.HasUnsafeForOneLine("wide﻿leg"));
    }

    [Fact]
    public void StripUnsafeForOneLine_drops_them_outright_rather_than_turning_them_into_a_space()
    {
        Assert.Equal("wideleg", PlainText.StripUnsafeForOneLine("wide​leg"));
        Assert.Equal("wideleg", PlainText.StripUnsafeForOneLine("wide﻿leg"));
    }

    [Fact]
    public void Zwj_and_zwnj_still_survive_stripping()
    {
        var sri = "ශ්‍රී";

        Assert.False(PlainText.HasUnsafeForOneLine(sri));
        Assert.Equal(sri, PlainText.StripUnsafeForOneLine(sri));
    }
}
