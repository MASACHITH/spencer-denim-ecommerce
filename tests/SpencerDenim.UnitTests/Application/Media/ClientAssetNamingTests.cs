using SpencerDenim.Application.Media.ClientAssets;

namespace SpencerDenim.UnitTests.Application.Media;

/// <summary>
/// The rule that decides <b>which garment a photograph is of</b>, and the two repairs the client's
/// delivered batch needs.
/// <para>
/// This is the contract worth testing hardest. The pixels are the client's and cannot be asserted on;
/// what can go silently wrong is a photograph of a polo shirt ending up on a belt, or a file nobody
/// places being skipped so quietly that the product is simply missing a picture on the day the shop
/// opens. Both are decided here, by a pure function, so both are tested here.
/// </para>
/// </summary>
public sealed class ClientAssetNamingTests
{
    /// <summary>The slugs the delivered batch touches, plus a few that make the repairs ambiguous.</summary>
    private static readonly HashSet<string> Catalogue = new(StringComparer.Ordinal)
    {
        "washed-linen-camp-shirt",
        "court-canvas-sneakers",
        "leather-look-belt",
        "pique-polo-shirt",
        "kandyan-osariya-handloom-saree",
        "kanjivaram-silk-blend-saree",
        "straight-fit-rigid-jeans",
        "chino-shorts",
    };

    [Theory]
    // The shape the README promises.
    [InlineData("washed-linen-camp-shirt-1.jpg", 1)]
    // The shape most of the delivered batch actually has: a doubled extension.
    [InlineData("washed-linen-camp-shirt-1.jpg.jpg", 2)]
    [InlineData("kandyan-osariya-handloom-saree-2.jpg", 1)]
    [InlineData("kanjivaram-silk-blend-saree-2.jpg", 1)]
    public void A_doubled_extension_resolves_to_the_same_product_as_a_single_one(string fileName, int stripped)
    {
        var (_, actuallyStripped) = ClientAssetNaming.StripMediaExtensions(fileName);
        Assert.Equal(stripped, actuallyStripped);

        var result = Assert.IsType<ProductAssetNameResult.Resolved>(
            ClientAssetNaming.ParseProductFile(fileName, Catalogue));

        Assert.Contains(result.Name.Slug, Catalogue);
        Assert.Null(result.Name.RepairedFrom);
    }

    [Fact]
    public void The_first_photograph_is_the_card_and_the_second_is_the_hover()
    {
        var card = Assert.IsType<ProductAssetNameResult.Resolved>(
            ClientAssetNaming.ParseProductFile("chino-shorts-1.jpg.jpg", Catalogue));

        var hover = Assert.IsType<ProductAssetNameResult.Resolved>(
            ClientAssetNaming.ParseProductFile("chino-shorts-2.jpg.jpg", Catalogue));

        Assert.Equal(("chino-shorts", 1), (card.Name.Slug, card.Name.Index));
        Assert.Equal(("chino-shorts", 2), (hover.Name.Slug, hover.Name.Index));
    }

    /// <summary>
    /// The two corruptions actually in the delivered batch, with the slug each one has to reach. Both
    /// are reported as repairs, because a rename this code inferred is a decision somebody has to check.
    /// </summary>
    [Theory]
    [InlineData("ourt-canvas-sneakers-2.jpg.jpg", "court-canvas-sneakers", "ourt-canvas-sneakers", 2)]
    [InlineData("productsleather-look-belt-1.jpg.jpg", "leather-look-belt", "productsleather-look-belt", 1)]
    [InlineData("productspique-polo-shirt-2.jpg.jpg", "pique-polo-shirt", "productspique-polo-shirt", 2)]
    public void A_corrupted_name_resolves_to_the_intended_slug_and_says_that_it_was_repaired(
        string fileName,
        string expectedSlug,
        string expectedSpelling,
        int expectedIndex)
    {
        var result = Assert.IsType<ProductAssetNameResult.Resolved>(
            ClientAssetNaming.ParseProductFile(fileName, Catalogue));

        Assert.Equal(expectedSlug, result.Name.Slug);
        Assert.Equal(expectedIndex, result.Name.Index);
        Assert.Equal(expectedSpelling, result.Name.RepairedFrom);
        Assert.True(result.Name.WasRepaired);
    }

    /// <summary>
    /// The repairs are bounded by the catalogue, so neither can invent a product. Take
    /// <c>court-canvas-sneakers</c> out of the list and <c>ourt-canvas-sneakers-2</c> stops resolving —
    /// it does not fall back to some other sneaker.
    /// </summary>
    [Fact]
    public void A_repair_that_lands_on_no_real_product_is_not_made()
    {
        var withoutSneakers = Catalogue.Where(slug => slug != "court-canvas-sneakers")
            .ToHashSet(StringComparer.Ordinal);

        var result = Assert.IsType<ProductAssetNameResult.Unmappable>(
            ClientAssetNaming.ParseProductFile("ourt-canvas-sneakers-2.jpg.jpg", withoutSneakers));

        Assert.Contains("ourt-canvas-sneakers", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>A near miss with two answers is a coin toss, and it refuses to toss it.</summary>
    [Fact]
    public void A_truncated_name_two_products_could_claim_is_refused_rather_than_guessed()
    {
        var ambiguous = new HashSet<string>(StringComparer.Ordinal) { "red-linen-shirt", "bed-linen-shirt" };

        var result = Assert.IsType<ProductAssetNameResult.Unmappable>(
            ClientAssetNaming.ParseProductFile("ed-linen-shirt-1.jpg", ambiguous));

        Assert.Contains("bed-linen-shirt", result.Reason, StringComparison.Ordinal);
        Assert.Contains("red-linen-shirt", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repair reaches a short distance on purpose. Four characters is enough for the damage that was
    /// delivered and not enough to reach across the catalogue to a slug that merely ends the same way.
    /// </summary>
    [Fact]
    public void A_name_missing_more_than_the_allowed_prefix_is_not_repaired()
    {
        var catalogue = new HashSet<string>(StringComparer.Ordinal) { "everyday-canvas-tote" };

        Assert.IsType<ProductAssetNameResult.Unmappable>(
            ClientAssetNaming.ParseProductFile("canvas-tote-1.jpg", catalogue));

        // One character inside the limit, and it resolves.
        var near = new HashSet<string>(StringComparer.Ordinal) { "a-canvas-tote" };
        var resolved = Assert.IsType<ProductAssetNameResult.Resolved>(
            ClientAssetNaming.ParseProductFile("canvas-tote-1.jpg", near));

        Assert.Equal("a-canvas-tote", resolved.Name.Slug);
    }

    [Theory]
    [InlineData("nonsense.jpg")]
    [InlineData("no-such-product-1.jpg")]
    [InlineData("washed-linen-camp-shirt.jpg")]
    [InlineData("washed-linen-camp-shirt-0.jpg")]
    [InlineData("washed-linen-camp-shirt-x.jpg")]
    [InlineData(".jpg")]
    public void Anything_that_cannot_be_placed_fails_with_a_reason_rather_than_a_guess(string fileName)
    {
        var result = Assert.IsType<ProductAssetNameResult.Unmappable>(
            ClientAssetNaming.ParseProductFile(fileName, Catalogue));

        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(fileName, result.FileName);
    }

    /// <summary>
    /// Real alt text, which is the point: the seeded placeholders said "Placeholder image — no photograph
    /// of X has been supplied yet", and they were telling the truth. These sentences replace them.
    /// </summary>
    [Theory]
    [InlineData(1, "Wide-Leg Rinse Jeans, front view")]
    [InlineData(2, "Wide-Leg Rinse Jeans, alternate view")]
    [InlineData(3, "Wide-Leg Rinse Jeans, view 3")]
    public void Alt_text_names_the_garment_and_never_says_placeholder(int index, string expected)
    {
        var alt = ClientAssetNaming.ProductAltText("Wide-Leg Rinse Jeans", index);

        Assert.Equal(expected, alt);
        Assert.DoesNotContain("laceholder", alt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trending-1.jpg", 1)]
    [InlineData("trending-4.jpg.jpg", 4)]
    [InlineData("trending.jpg", null)]
    [InlineData("social-1.jpg", null)]
    public void A_trending_tile_is_recognised_by_its_number(string fileName, int? expected) =>
        Assert.Equal(expected, ClientAssetNaming.TrendingIndexOf(fileName));

    [Theory]
    [InlineData("social-6.jpg.jpg", 6)]
    [InlineData("social-1.jpg", 1)]
    [InlineData("trending-1.jpg", null)]
    public void A_social_tile_is_recognised_by_its_number(string fileName, int? expected) =>
        Assert.Equal(expected, ClientAssetNaming.SocialIndexOf(fileName));

    [Theory]
    [InlineData("hero.mp4", ClientAssetNaming.HeroVideoFile, true)]
    [InlineData("hero-poster.jpg.jpg", ClientAssetNaming.HeroPosterFile, true)]
    [InlineData("campaign.jpg", ClientAssetNaming.CampaignFile, true)]
    // The folder holds an earlier take of the hero as well. It is not hero.mp4 and is not the hero.
    [InlineData("Woman_posing_near_sheer_curtain_20260930003046.mp4", ClientAssetNaming.HeroVideoFile, false)]
    public void A_fixed_contract_name_is_matched_whatever_its_extension_doubling(
        string fileName,
        string contractName,
        bool expected) =>
        Assert.Equal(expected, ClientAssetNaming.IsNamed(fileName, contractName));
}
