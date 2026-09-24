using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The homepage and announcement rows of doc 08 §9: ten sections in the signed proposal's order,
/// six items (three department tiles and three benefits) and two announcement lines.
/// <para>
/// <b>Media-dependent sections are seeded switched off.</b> The hero video (HOME-01), the campaign
/// banner (HOME-05), the activewear feature (HOME-06) and the social gallery (HOME-08) are nothing
/// but artwork, and there is no artwork: the media library and its storage are W1-04. A banner with
/// an empty frame in it is worse than no banner, so these four arrive off and the client turns each
/// one on in ADM-17 when it has something to show.
/// </para>
/// <para>
/// <b>The copy is deliberately plain.</b> docs/04 §4 decision 8 makes the delivery threshold, the
/// returns window and the announcement lines placeholders that the client replaces before go-live
/// (D8), and nothing here promises anything the rest of the system has not been built to keep.
/// </para>
/// </summary>
internal static class HomeContentData
{
    /// <summary>Every section, in the order they appear down the page. Sort order is the section number.</summary>
    public static IReadOnlyList<HomeSectionSeed> Sections { get; } =
    [
        new(
            HomeSectionType.HeroVideo,
            SortOrder: 1,
            Title: null,
            Subtitle: null,
            // Light text over a photograph, with a dark fade under it (docs/04 decision 7).
            SettingsJson: """{"overlay":"dark"}""",
            IsEnabled: false),

        new(
            HomeSectionType.ShopByDepartment,
            SortOrder: 2,
            Title: "Shop by department",
            Subtitle: null,
            SettingsJson: null,
            IsEnabled: true),

        new(
            HomeSectionType.NewArrivals,
            SortOrder: 3,
            Title: "New arrivals",
            Subtitle: null,
            // The row is the head of the listing its link opens, so the two can never disagree.
            SettingsJson: """{"mode":"auto","count":8,"listingUrl":"/new-arrivals","linkLabel":"View all new arrivals"}""",
            IsEnabled: true),

        new(
            HomeSectionType.TrendingItems,
            SortOrder: 4,
            Title: "Trending now",
            Subtitle: null,
            // Auto, because the alternative — the tiles of HOME-04, each opening its own filtered
            // listing — is pure artwork and there is none yet (W1-04), and doc 08 §9 pins this
            // seed at six items. There is no "everything" listing route to link a row of the whole
            // catalogue to, so no linkLabel is set and the section renders no "view all".
            SettingsJson: """{"mode":"auto","count":8,"listingUrl":"/?sort=best-selling"}""",
            IsEnabled: true),

        new(
            HomeSectionType.CampaignBanner,
            SortOrder: 5,
            Title: null,
            Subtitle: null,
            SettingsJson: """{"overlay":"dark"}""",
            IsEnabled: false),

        new(
            HomeSectionType.ActivewearFeature,
            SortOrder: 6,
            Title: "Activewear",
            Subtitle: null,
            SettingsJson: """{"overlay":"dark"}""",
            IsEnabled: false),

        new(
            HomeSectionType.SaleSection,
            SortOrder: 7,
            Title: "Sale",
            Subtitle: null,
            SettingsJson: """{"mode":"auto","count":8,"listingUrl":"/sale","linkLabel":"Shop all sale"}""",
            IsEnabled: true),

        new(
            HomeSectionType.SocialGallery,
            SortOrder: 8,
            Title: "Follow us",
            Subtitle: null,
            SettingsJson: null,
            IsEnabled: false),

        new(
            HomeSectionType.Benefits,
            SortOrder: 9,
            Title: null,
            Subtitle: null,
            SettingsJson: null,
            IsEnabled: true),

        new(
            HomeSectionType.Newsletter,
            SortOrder: 10,
            Title: "Newsletter",
            Subtitle: "New arrivals and sale news, straight to your inbox.",
            SettingsJson: null,
            IsEnabled: true),
    ];

    /// <summary>
    /// The three department tiles of HOME-02, keyed by the seeded department slug. No artwork: a
    /// tile without a photograph still names its department and opens it, which is the whole point
    /// of the block, and the client adds the photograph in ADM-17.
    /// </summary>
    public static IReadOnlyList<DepartmentTileSeed> DepartmentTiles { get; } =
    [
        new("women", SortOrder: 1, ButtonLabel: "Shop Women"),
        new("men", SortOrder: 2, ButtonLabel: "Shop Men"),
        new("kids", SortOrder: 3, ButtonLabel: "Shop Kids"),
    ];

    /// <summary>
    /// The three customer benefits of HOME-09. <c>IconKey</c> names an icon in the storefront's own
    /// set, never a file, so this block needs no media either.
    /// <para>
    /// Placeholder copy (docs/04 §4 decision 8, D8). None of it states a figure the system has not
    /// been configured with: the delivery charge comes from the zone rates at checkout and the
    /// returns window is a GLB-10 page the client has still to write.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BenefitSeed> Benefits { get; } =
    [
        new(
            IconKey: "delivery",
            SortOrder: 1,
            Title: "Island-wide delivery",
            Body: "We deliver across Sri Lanka. The charge for your district is shown at checkout."),
        new(
            IconKey: "returns",
            SortOrder: 2,
            Title: "Returns and exchanges",
            Body: "Changed your mind, or need another size? Get in touch and we will sort it out."),
        new(
            IconKey: "quality",
            SortOrder: 3,
            Title: "Made to last",
            Body: "Denim and everyday pieces in fabrics chosen for the weather here."),
    ];

    /// <summary>
    /// The two announcement lines of doc 08 §9 (GLB-03). Both are placeholders the client replaces
    /// before go-live (docs/04 §4 decision 8), and both are the lines the storefront shell has been
    /// showing from a constant since GLB-03 — they move into the database here, where the client can
    /// edit them.
    /// </summary>
    public static IReadOnlyList<AnnouncementSeed> Announcements { get; } =
    [
        new("Free island-wide delivery on orders over Rs. 10,000", SortOrder: 1),
        new("Easy 14-day returns and exchanges on every order", SortOrder: 2),
    ];

    internal sealed record HomeSectionSeed(
        HomeSectionType SectionType,
        short SortOrder,
        string? Title,
        string? Subtitle,
        string? SettingsJson,
        bool IsEnabled);

    internal sealed record DepartmentTileSeed(string DepartmentSlug, short SortOrder, string ButtonLabel);

    internal sealed record BenefitSeed(string IconKey, short SortOrder, string Title, string Body);

    internal sealed record AnnouncementSeed(string Message, short SortOrder);
}
