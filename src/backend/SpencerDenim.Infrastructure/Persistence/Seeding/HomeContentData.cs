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

            // The type is still ActivewearFeature — it is HOME-06's slot in the signed proposal and
            // renaming an enum member would rewrite history in the database. What the slot *says* is
            // content, and the client's picture for it is a linen and denim flat-lay, so it says that
            // (W1-14). Picture, words and destination have to agree; issue #63 is what happens when
            // they do not.
            Title: "Linen and denim",
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

    /// <summary>
    /// The seed version this build ships, recorded in <c>Seed.HomeContentVersion</c>.
    /// <list type="number">
    /// <item><description>The sections, department tiles, benefits and announcements above (the baseline,
    /// seeded idempotently on every start, as before).</description></item>
    /// <item><description>#63: the artwork rows below, offered <b>once</b>, and the retraction of the
    /// words an earlier importer wrote over HOME-06.</description></item>
    /// </list>
    /// </summary>
    public const int SeedVersion = 2;

    /// <summary>
    /// The mega-menu card seed version this build ships, recorded in <c>Seed.MegaMenuCardsVersion</c> (#215).
    /// <list type="number">
    /// <item><description>One switched-off card per department in the words the storefront hard-coded before
    /// #215.</description></item>
    /// </list>
    /// </summary>
    public const int MegaMenuCardSeedVersion = 1;

    /// <summary>The button the storefront hard-coded on every mega-menu card before #215.</summary>
    public const string MegaMenuCardButtonLabel = "Shop now";

    /// <summary>The link the storefront hard-coded on every mega-menu card before #215.</summary>
    public const string MegaMenuCardLinkUrl = "/new-arrivals";

    /// <summary>
    /// The card the storefront used to draw for a department, as a switched-off row: "New in {name}", the
    /// description (when it fits the card's 200 characters; the menu's own fallback still shows the whole of
    /// it), "Shop now" and <c>/new-arrivals</c>. A name too long for the heading is cut, never refused.
    /// </summary>
    public static MegaMenuPromoCard MegaMenuCardFor(string departmentName, string? description)
    {
        ArgumentNullException.ThrowIfNull(departmentName);

        var title = "New in " + departmentName.Trim();
        var subtitle = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        return new MegaMenuPromoCard
        {
            Title = title.Length > MegaMenuPromoCardLimits.TitleMaxLength
                ? title[..MegaMenuPromoCardLimits.TitleMaxLength]
                : title,
            Subtitle = subtitle is { Length: <= MegaMenuPromoCardLimits.SubtitleMaxLength } ? subtitle : null,
            ButtonLabel = MegaMenuCardButtonLabel,
            LinkUrl = MegaMenuCardLinkUrl,
            SortOrder = 0,
            IsActive = false,
        };
    }

    /// <summary>
    /// The homepage's words and links for every block whose picture comes from the client (#63): the hero,
    /// the two banners, the four trending tiles and the six social tiles.
    /// <para>
    /// <b>These used to be written by <c>ClientAssetImporter</c>, and that was the defect.</b> A class whose
    /// job is to move bytes was also deciding what the homepage says, so there was no moment at which a row
    /// said what it was <i>for</i> before a photograph arrived — and a linen flat-lay went in under the
    /// heading "Activewear". Now each row exists from the first start, says what it is waiting for, and is
    /// <b>switched off</b> until a picture is attached: by the importer (which only attaches, and switches
    /// the row on) or by a staff member in ADM-17. Their sections are switched off as well, so a fresh
    /// shop's homepage looks exactly as it did.
    /// </para>
    /// <para>
    /// <b>Keyed on (section type, sort order)</b> — the slot the importer's filename contract names
    /// (<c>trending-3.jpg</c> is the trending row at position 3) — and offered once, by version: a row the
    /// client deletes is not put back by the next deployment.
    /// </para>
    /// <para>
    /// The copy is the copy the importer used to write, word for word, so a database the importer filled
    /// before this change and one seeded after it say the same thing. The social links are dummies on
    /// <c>example.invalid</c> (RFC 2606: a domain that can never be registered) and each label says
    /// PLACEHOLDER out loud, until the client sends the real account addresses.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ArtworkItemSeed> ArtworkItems { get; } =
    [
        new(
            HomeSectionType.HeroVideo,
            SortOrder: 1,
            Title: "Dressed for Colombo",
            Subtitle: "Linen, denim and everyday pieces made for the weather here.",
            ButtonLabel: "Shop new arrivals",
            LinkUrl: "/new-arrivals"),

        new(
            HomeSectionType.CampaignBanner,
            SortOrder: 1,
            Title: "The new season",
            Subtitle: "Linen, denim and everything in between.",
            ButtonLabel: "Shop new arrivals",
            LinkUrl: "/new-arrivals"),

        // W1-14: the words follow the photograph the client actually sent — a white linen co-ord and a
        // mid-blue denim co-ord — and the link is a listing that genuinely holds those fabrics.
        new(
            HomeSectionType.ActivewearFeature,
            SortOrder: 1,
            Title: "Linen and denim",
            Subtitle: "Breathable linen for the heat, washed indigo for everything else.",
            ButtonLabel: "Shop linen and denim",
            LinkUrl: "/women?material=linen&material=linen-blend&material=denim"),

        // HOME-04, in the delivered photographs' numbered order: a leather bag, a stack of linen shirts, a
        // dress with raffia slides, children's knitwear. Each opens a listing the catalogue can serve.
        new(HomeSectionType.TrendingItems, SortOrder: 1, Title: "Bags and accessories", LinkUrl: "/women/accessories"),
        new(HomeSectionType.TrendingItems, SortOrder: 2, Title: "Linen shirts", LinkUrl: "/men/shirts"),
        new(HomeSectionType.TrendingItems, SortOrder: 3, Title: "Dresses and sandals", LinkUrl: "/women/dresses"),
        new(HomeSectionType.TrendingItems, SortOrder: 4, Title: "Kids' new in", LinkUrl: "/kids?sort=newest"),

        Social(1, "Instagram", "instagram"),
        Social(2, "Facebook", "facebook"),
        Social(3, "TikTok", "tiktok"),
        Social(4, "Pinterest", "pinterest"),
        Social(5, "YouTube", "youtube"),
        Social(6, "WhatsApp", "whatsapp"),
    ];

    /// <summary>
    /// Words an earlier <c>ClientAssetImporter</c> wrote over HOME-06 and then took back (W1-14), keyed by
    /// <b>field</b>: "Activewear" is retracted as a heading, not wherever it appears, because a staff member
    /// may type it into the button label on purpose. Applied once, with version 2, to an item that still
    /// holds exactly the retracted value in exactly that field.
    /// </summary>
    public static IReadOnlyList<RetractedCopy> Retractions { get; } =
    [
        new(HomeSectionType.ActivewearFeature, 1, nameof(HomeSectionItem.Title), "Activewear"),
        new(HomeSectionType.ActivewearFeature, 1, nameof(HomeSectionItem.Subtitle), "Pieces that move with you."),
        new(HomeSectionType.ActivewearFeature, 1, nameof(HomeSectionItem.LinkUrl), "/women/activewear"),
        new(HomeSectionType.ActivewearFeature, 1, nameof(HomeSectionItem.ButtonLabel), "Shop activewear"),
    ];

    private static ArtworkItemSeed Social(short sortOrder, string network, string slug) =>
        new(
            HomeSectionType.SocialGallery,
            sortOrder,
            Title: $"{network} — PLACEHOLDER LINK, the real account URL has not been supplied yet",
            LinkUrl: $"https://example.invalid/PLACEHOLDER/{slug}");

    internal sealed record ArtworkItemSeed(
        HomeSectionType SectionType,
        short SortOrder,
        string Title,
        string LinkUrl,
        string? Subtitle = null,
        string? ButtonLabel = null);

    internal sealed record RetractedCopy(HomeSectionType SectionType, short SortOrder, string Field, string Value);

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
