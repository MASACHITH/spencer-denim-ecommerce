using System.Text.RegularExpressions;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>The shared field rules of the ADM-17 writes (#192), and the block-rule table.</summary>
public sealed partial class ContentFieldRulesTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/women", true)]
    [InlineData("/women/jeans?fit=wide-leg&colour=navy", true)]
    [InlineData("/kids?sort=newest#top", true)]
    [InlineData("women", false)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("/women\tx", false)]
    [InlineData("/women x", false)]
    [InlineData("https://shop.example/women", false)]
    [InlineData("javascript:alert(1)", false)]
    public void A_site_path_starts_with_one_slash_and_stays_on_the_site(string value, bool expected)
    {
        Assert.Equal(expected, ContentFieldRules.IsSitePath(value));
    }

    [Theory]
    [InlineData("https://instagram.com/colombowardrobe", true)]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.invalid/PLACEHOLDER/instagram", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("vbscript:x", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("mailto:shop@example.com", false)]
    [InlineData("https://user:pass@example.com", false)]
    [InlineData("https://instagram.com@evil.example", false)]
    [InlineData("https://exa mple.com", false)]
    [InlineData("/women", false)]
    public void A_web_address_is_absolute_http_or_https_with_no_credentials(string value, bool expected)
    {
        Assert.Equal(expected, ContentFieldRules.IsWebAddress(value));
    }

    [Fact]
    public void Shop_time_and_utc_convert_both_ways_at_plus_five_thirty()
    {
        var utc = ContentFieldRules.ToUtc(new DateTime(2027, 1, 1, 0, 0, 0));

        Assert.Equal(new DateTime(2026, 12, 31, 18, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal("2027-01-01T00:00", ContentFieldRules.ToColomboText(utc));
        Assert.Null(ContentFieldRules.ToUtc(null));
        Assert.Null(ContentFieldRules.ToColomboText(null));
    }

    [Fact]
    public void Every_block_has_a_rule_and_its_required_fields_are_fields_it_uses()
    {
        foreach (var type in Enum.GetValues<HomeSectionType>())
        {
            var rule = HomeBlockRules.For(type);

            Assert.All(rule.RequiredItemFields, field => Assert.Contains(field, rule.ItemFields));
            Assert.Equal(rule.MaxItems == 0, rule.ItemFields.Count == 0);
            Assert.Contains(HomeBlockRules.SectionFields.Title, rule.SectionFields);
        }
    }

    /// <summary>
    /// The hero and the two banners draw <c>items[0]</c> only, so a second item would be a row nobody ever
    /// sees; and only the social gallery may send a shopper off the site.
    /// </summary>
    [Fact]
    public void The_single_picture_blocks_hold_one_item_and_only_the_gallery_leaves_the_site()
    {
        Assert.Equal(1, HomeBlockRules.For(HomeSectionType.HeroVideo).MaxItems);
        Assert.Equal(1, HomeBlockRules.For(HomeSectionType.CampaignBanner).MaxItems);
        Assert.Equal(1, HomeBlockRules.For(HomeSectionType.ActivewearFeature).MaxItems);

        Assert.Equal(
            [HomeSectionType.SocialGallery],
            Enum.GetValues<HomeSectionType>().Where(type => HomeBlockRules.For(type).LinkMayLeaveSite));

        Assert.Equal(
            [HomeSectionType.HeroVideo],
            Enum.GetValues<HomeSectionType>().Where(type => HomeBlockRules.For(type).UsesItemField(HomeBlockRules.ItemFields.Video)));
    }

    /// <summary>
    /// The icon keys the API accepts are exactly the keys the storefront's benefits row can draw, read from
    /// <c>benefits-row.ts</c> itself — a key the storefront does not know would draw the fallback star.
    /// </summary>
    [Fact]
    public void The_benefit_icons_are_the_ones_the_storefront_draws()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src", "frontend", "projects", "storefront", "src", "app", "pages", "home", "sections", "benefits-row.ts"));

        var block = BenefitIconBlock().Match(source);
        Assert.True(block.Success, "BENEFIT_ICONS was not found in benefits-row.ts.");

        var keys = BenefitIconKey().Matches(block.Groups["body"].Value).Select(match => match.Groups["key"].Value).ToList();

        Assert.Equal(keys.Order(StringComparer.Ordinal), HomeBlockRules.BenefitIcons.Order(StringComparer.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"BENEFIT_ICONS[^=]*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline)]
    private static partial Regex BenefitIconBlock();

    [GeneratedRegex(@"^\s*(?<key>[a-z]+)\s*:", RegexOptions.Multiline)]
    private static partial Regex BenefitIconKey();
}
