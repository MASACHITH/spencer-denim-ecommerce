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
    public void A_description_that_fits_is_kept_and_one_that_does_not_is_left_to_the_fallback()
    {
        var fits = new string('a', MegaMenuPromoCardLimits.SubtitleMaxLength);
        var over = new string('a', MegaMenuPromoCardLimits.SubtitleMaxLength + 1);

        Assert.Equal(fits, HomeContentData.MegaMenuCardFor("Men", fits).Subtitle);
        Assert.Null(HomeContentData.MegaMenuCardFor("Men", over).Subtitle);
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
