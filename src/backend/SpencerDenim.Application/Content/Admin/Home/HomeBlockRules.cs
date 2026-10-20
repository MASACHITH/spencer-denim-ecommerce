using System.Collections.Frozen;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin.Home;

/// <summary>
/// What each of the ten homepage blocks (HOME-01..10) lets a staff member edit — the one table the write
/// handlers validate against and the admin screen draws its form from (doc 11 P13 steps 6 and 7).
/// <para>
/// <b>One table, sent to the screen, so the two cannot disagree.</b> <c>content.HomeSections</c> and
/// <c>content.HomeSectionItems</c> are deliberately wide — one item table serves a benefit, a gallery
/// image and the hero — so without this every block would accept every column, and a video on a benefit
/// or an icon on a banner would be stored and then silently ignored by the storefront. A field outside a
/// block's set must arrive as <see langword="null"/>, and the handler says so by name if it does not.
/// </para>
/// <para>
/// <b>The item limits follow what each block draws</b>: the hero and the two banners draw their first item
/// only (<c>items[0]</c> in <c>home-hero.ts</c> and <c>feature-banner.ts</c>), so a second item is a row
/// nobody would ever see; the grids wrap, and twelve is a generous gallery.
/// </para>
/// </summary>
public static class HomeBlockRules
{
    /// <summary>Section-level field names, as the API and the screen spell them.</summary>
    public static class SectionFields
    {
        public const string Title = "title";
        public const string Subtitle = "subtitle";
        public const string Mode = "mode";
        public const string Count = "count";
        public const string ListingUrl = "listingUrl";
        public const string LinkLabel = "linkLabel";
        public const string Overlay = "overlay";
    }

    /// <summary>Item-level field names, as the API and the screen spell them.</summary>
    public static class ItemFields
    {
        public const string Title = "title";
        public const string Subtitle = "subtitle";
        public const string Body = "body";
        public const string ButtonLabel = "buttonLabel";
        public const string LinkUrl = "linkUrl";
        public const string Image = "image";
        public const string MobileImage = "mobileImage";
        public const string Video = "video";
        public const string Department = "department";
        public const string Product = "product";
        public const string IconKey = "iconKey";
    }

    /// <summary>
    /// The icon keys the storefront's benefits row draws (<c>BENEFIT_ICONS</c> in <c>benefits-row.ts</c>).
    /// A key outside this set would draw the fallback star, which is never what was meant, so it is
    /// refused instead. Kept in step with the storefront by the admin e2e stub and a unit test.
    /// </summary>
    public static IReadOnlyList<string> BenefitIcons { get; } =
    [
        "delivery",
        "shipping",
        "returns",
        "exchanges",
        "quality",
        "secure",
        "payment",
        "support",
        "contact",
    ];

    /// <summary>The overlay values <see cref="HomeSectionSettings"/> reads.</summary>
    public static IReadOnlyList<string> Overlays { get; } = ["dark", "light", "none"];

    private static readonly string[] HeadingFields = [SectionFields.Title, SectionFields.Subtitle];

    private static readonly string[] ProductRowFields =
    [
        SectionFields.Title,
        SectionFields.Subtitle,
        SectionFields.Count,
        SectionFields.ListingUrl,
        SectionFields.LinkLabel,
    ];

    private static readonly string[] BannerFields = [SectionFields.Title, SectionFields.Subtitle, SectionFields.Overlay];

    private static readonly string[] BannerItemFields =
    [
        ItemFields.Title,
        ItemFields.Subtitle,
        ItemFields.Body,
        ItemFields.ButtonLabel,
        ItemFields.LinkUrl,
        ItemFields.Image,
        ItemFields.MobileImage,
    ];

    private static readonly FrozenDictionary<HomeSectionType, HomeBlockRule> Rules =
        new Dictionary<HomeSectionType, HomeBlockRule>
        {
            [HomeSectionType.HeroVideo] = new(
                BannerFields,
                [
                    ItemFields.Title,
                    ItemFields.Subtitle,
                    ItemFields.ButtonLabel,
                    ItemFields.LinkUrl,
                    ItemFields.Image,
                    ItemFields.MobileImage,
                    ItemFields.Video,
                ],
                [],
                MaxItems: 1,
                LinkMayLeaveSite: false,
                NeedsArtwork: true),

            [HomeSectionType.ShopByDepartment] = new(
                HeadingFields,
                [
                    ItemFields.Title,
                    ItemFields.ButtonLabel,
                    ItemFields.LinkUrl,
                    ItemFields.Image,
                    ItemFields.MobileImage,
                    ItemFields.Department,
                ],
                [ItemFields.Department],
                MaxItems: 6,
                LinkMayLeaveSite: false),

            [HomeSectionType.NewArrivals] = new(ProductRowFields, [], [], MaxItems: 0, LinkMayLeaveSite: false),

            [HomeSectionType.TrendingItems] = new(
                [.. ProductRowFields, SectionFields.Mode],
                [ItemFields.Title, ItemFields.Subtitle, ItemFields.LinkUrl, ItemFields.Image, ItemFields.Product],
                [ItemFields.Title],
                MaxItems: 12,
                LinkMayLeaveSite: false),

            [HomeSectionType.CampaignBanner] = new(BannerFields, BannerItemFields, [], MaxItems: 1, LinkMayLeaveSite: false, NeedsArtwork: true),

            [HomeSectionType.ActivewearFeature] = new(BannerFields, BannerItemFields, [], MaxItems: 1, LinkMayLeaveSite: false, NeedsArtwork: true),

            [HomeSectionType.SaleSection] = new(ProductRowFields, [], [], MaxItems: 0, LinkMayLeaveSite: false),

            // The one block whose links may leave the site: each tile opens a social profile (HOME-08).
            [HomeSectionType.SocialGallery] = new(
                HeadingFields,
                [ItemFields.Title, ItemFields.LinkUrl, ItemFields.Image],
                [ItemFields.Image],
                MaxItems: 12,
                LinkMayLeaveSite: true),

            [HomeSectionType.Benefits] = new(
                HeadingFields,
                [ItemFields.Title, ItemFields.Body, ItemFields.IconKey],
                [ItemFields.IconKey],
                MaxItems: 6,
                LinkMayLeaveSite: false),

            [HomeSectionType.Newsletter] = new(HeadingFields, [], [], MaxItems: 0, LinkMayLeaveSite: false),
        }.ToFrozenDictionary();

    /// <summary>The rule for one block. Every member of <see cref="HomeSectionType"/> has one.</summary>
    public static HomeBlockRule For(HomeSectionType type) =>
        Rules.TryGetValue(type, out var rule)
            ? rule
            : throw new ArgumentOutOfRangeException(nameof(type), type, "No block rule for this section type.");

    /// <summary>
    /// Whether an item of this block is nothing without a picture: the hero and the two banners, which the
    /// storefront draws only when an item carries artwork. Such an item can be switched on only with one.
    /// </summary>
    public static bool NeedsArtworkToShow(HomeBlockRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.NeedsArtwork;
    }

    /// <summary>Whether this block's products come from a listing (HOME-03, HOME-04 auto, HOME-07).</summary>
    public static bool IsProductRow(HomeSectionType type) =>
        type is HomeSectionType.NewArrivals or HomeSectionType.TrendingItems or HomeSectionType.SaleSection;
}

/// <summary>What one homepage block lets a staff member edit.</summary>
/// <param name="SectionFields">Section-level fields this block uses; the rest must be null.</param>
/// <param name="ItemFields">Item fields this block uses; the rest must be null.</param>
/// <param name="RequiredItemFields">Item fields that must be filled in.</param>
/// <param name="MaxItems">How many items the block may hold. Zero means it has none.</param>
/// <param name="LinkMayLeaveSite">Whether an item's link may be a full address on another site.</param>
/// <param name="NeedsArtwork">Whether an item can be switched on only with a picture (hero and banners).</param>
public sealed record HomeBlockRule(
    IReadOnlyList<string> SectionFields,
    IReadOnlyList<string> ItemFields,
    IReadOnlyList<string> RequiredItemFields,
    int MaxItems,
    bool LinkMayLeaveSite,
    bool NeedsArtwork = false)
{
    public bool UsesSectionField(string field) => SectionFields.Contains(field, StringComparer.Ordinal);

    public bool UsesItemField(string field) => ItemFields.Contains(field, StringComparer.Ordinal);

    public bool RequiresItemField(string field) => RequiredItemFields.Contains(field, StringComparer.Ordinal);
}
