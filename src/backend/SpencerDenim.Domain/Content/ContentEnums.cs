namespace SpencerDenim.Domain.Content;

/// <summary>
/// <c>content.HomeSections.SectionType</c> (doc 08 §3.7). The ten blocks of the homepage
/// (HOME-01..10), numbered as the signed proposal orders them. A CK pins the range in the
/// database, so a new block is a migration and not a stray number.
/// </summary>
public enum HomeSectionType : byte
{
    /// <summary>HOME-01: admin-uploaded video with a poster, and an image on mobile.</summary>
    HeroVideo = 1,

    /// <summary>HOME-02: Women / Men / Kids tiles.</summary>
    ShopByDepartment = 2,

    /// <summary>HOME-03: a product row from the New Arrivals listing.</summary>
    NewArrivals = 3,

    /// <summary>HOME-04: trending tiles, each opening a filtered listing.</summary>
    TrendingItems = 4,

    /// <summary>HOME-05: full-width campaign banner.</summary>
    CampaignBanner = 5,

    /// <summary>HOME-06: activewear campaign feature.</summary>
    ActivewearFeature = 6,

    /// <summary>HOME-07: a product row from the Sale listing.</summary>
    SaleSection = 7,

    /// <summary>HOME-08: social gallery, each image linking to a social profile.</summary>
    SocialGallery = 8,

    /// <summary>HOME-09: delivery / returns / quality.</summary>
    Benefits = 9,

    /// <summary>HOME-10: newsletter signup.</summary>
    Newsletter = 10,
}
