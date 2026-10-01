using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The homepage and announcement rows of doc 08 §9: ten sections in the signed proposal's order,
/// six items (three department tiles and three benefits) and two announcement lines.
/// <para>
/// <b>Media-dependent sections are seeded switched off.</b> The hero video (HOME-01), the trending
/// tiles (HOME-04), the campaign banner (HOME-05), the activewear feature (HOME-06) and the social
/// gallery (HOME-08) are each a picture with words on it, and there is no picture: the media library
/// and its storage are W1-04. A banner with an empty frame in it is worse than no banner, so these
/// five arrive off and the client turns each one on in ADM-17 when it has something to show.
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
            // Manual, which is what HOME-04 *is*: "each card opens the matching filtered listing"
            // (doc 02), and doc 08 §3.7 says a trending item stores that listing URL. Seeding it
            // `auto` instead shipped a best-selling product row under the heading "Trending now" —
            // a block that is not one of HOME-01..10, that does not do the thing HOME-04 is
            // defined by, and whose "view all" had nowhere on this site to go. A best-sellers row
            // is a change request to discuss, not a default.
            //
            // Off, for the same reason as the hero and the banners: a trending tile is a picture
            // with a link on it, doc 08 §9 pins this seed at six items, and the media library is
            // W1-04. Both modes are built and tested; the client switches this on with either.
            SettingsJson: """{"mode":"manual"}""",
            IsEnabled: false),

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
        // Deliberately no figure and no window. A delivery threshold is an ADM-22/CHK-02 setting that
        // does not exist yet, and a returns window is a GLB-10 policy page the client has not written —
        // so seeding either would put a promise on every page of the site that nothing in the system
        // can honour, and that nobody has agreed (#34). These three are true the day the shop opens.
        new("Island-wide delivery across Sri Lanka", SortOrder: 1),
        new("New season linen has arrived", SortOrder: 2),
        new("Questions? Message us on WhatsApp", SortOrder: 3),
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
