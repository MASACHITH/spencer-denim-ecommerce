using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure;

/// <summary>
/// The card the seeder offers each department (#215) is the one the storefront used to hard-code, word for word,
/// and it arrives switched off so a fresh database looks as it did.
/// </summary>
public sealed class MegaMenuCardSeedDataTests
{
    [Fact]
    public void The_seeded_card_is_todays_card_switched_off()
    {
        var card = HomeContentData.MegaMenuCardFor("Women", "Everyday essentials and occasion pieces.");

        Assert.Equal("New in Women", card.Title);
        Assert.Equal("Everyday essentials and occasion pieces.", card.Subtitle);
        Assert.Equal("Shop now", card.ButtonLabel);
        Assert.Equal("/new-arrivals", card.LinkUrl);
        Assert.False(card.IsActive);
        Assert.Null(card.ImageAssetId);
        Assert.Equal(0, card.SortOrder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_department_without_a_description_gets_no_subtitle(string? description)
    {
        Assert.Null(HomeContentData.MegaMenuCardFor("Kids", description).Subtitle);
    }

    [Fact]
    public void A_description_that_fits_is_kept_whole()
    {
        var fits = new string('a', MegaMenuPromoCardLimits.SubtitleMaxLength);

        Assert.Equal(fits, HomeContentData.MegaMenuCardFor("Men", fits).Subtitle);
    }

    /// <summary>#215 tester: the seeded line is one the API's own subtitle rules accept, so the editor can save it back.</summary>
    [Fact]
    public void A_long_description_is_cut_at_a_word_within_the_limit()
    {
        var words = string.Join(' ', Enumerable.Repeat("linen", 60));

        var subtitle = HomeContentData.MegaMenuCardFor("Men", words).Subtitle!;

        Assert.True(subtitle.Length <= MegaMenuPromoCardLimits.SubtitleMaxLength, subtitle.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith("linen…", subtitle, StringComparison.Ordinal);
        Assert.Empty(OptionalTextErrors(subtitle));
    }

    [Fact]
    public void A_description_with_line_breaks_becomes_one_line_the_api_accepts()
    {
        var subtitle = HomeContentData.MegaMenuCardFor("Kids", "  Soft fabrics.\r\n\tRoom to grow.  ").Subtitle!;

        Assert.Equal("Soft fabrics. Room to grow.", subtitle);
        Assert.Empty(OptionalTextErrors(subtitle));
    }

    [Fact]
    public void An_unbroken_description_too_long_for_the_card_is_cut_hard()
    {
        var subtitle = HomeContentData.MegaMenuCardFor("Men", new string('a', 500)).Subtitle!;

        Assert.Equal(MegaMenuPromoCardLimits.SubtitleMaxLength, subtitle.Length);
    }

    private static List<SpencerDenim.Application.Abstractions.Results.ValidationError> OptionalTextErrors(string value)
    {
        var errors = new List<SpencerDenim.Application.Abstractions.Results.ValidationError>();
        SpencerDenim.Application.Content.Admin.ContentFieldRules.OptionalText(errors, "subtitle", value, MegaMenuPromoCardLimits.SubtitleMaxLength);
        return errors;
    }

    [Fact]
    public void A_name_too_long_for_the_heading_is_cut_rather_than_refused()
    {
        var name = new string('n', 200);

        var card = HomeContentData.MegaMenuCardFor(name, null);

        Assert.Equal(MegaMenuPromoCardLimits.TitleMaxLength, card.Title.Length);
        Assert.StartsWith("New in n", card.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_is_trimmed()
    {
        Assert.Equal("New in Men", HomeContentData.MegaMenuCardFor("  Men ", null).Title);
    }
}
