namespace SpencerDenim.Application.Catalog;

/// <summary>
/// Catalogue read-path settings, bound from the <c>Catalog</c> configuration section (environment
/// variables <c>Catalog__*</c>), so nothing depends on the host (doc 03 §5).
/// <para>
/// Doc 08 §9 puts <c>Catalog.NewArrivalDays</c>, <c>Catalog.BestSellerTopN</c> and
/// <c>Inventory.DefaultLowStockThreshold</c> in the <c>ops.Settings</c> table. That table belongs to
/// the admin settings milestone (ADM-22, W3-07), so this milestone reads the same three values from
/// configuration. Moving them behind an <c>ISettingsProvider</c> later changes no caller.
/// </para>
/// </summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>
    /// Fallback for <c>inventory.StockLevels.LowStockThreshold</c> when a variant has none
    /// (<c>Inventory.DefaultLowStockThreshold</c>, doc 08 §9 default 5).
    /// </summary>
    public int DefaultLowStockThreshold { get; set; } = 5;

    /// <summary>How long a newly published product keeps the "New" badge (doc 08 §9 default 30).</summary>
    public int NewArrivalDays { get; set; } = 30;

    /// <summary>
    /// How many products per department count as best sellers for the computed badge
    /// (doc 08 §9 default 10).
    /// </summary>
    public int BestSellerTopN { get; set; } = 10;

    /// <summary>Page size used when the request does not ask for one (PLP-07 "Load More").</summary>
    public int DefaultPageSize { get; set; } = 24;

    /// <summary>Upper bound on <c>pageSize</c>, so one request cannot ask for the whole catalogue.</summary>
    public int MaxPageSize { get; set; } = 96;

    /// <summary>
    /// Public base URL for media, e.g. <c>/media</c> behind the reverse proxy or a CDN origin later.
    /// A storage key is appended to it (doc 08 §3.3: the database stores relative keys only).
    /// </summary>
    public string MediaBaseUrl { get; set; } = "/media";

    /// <summary>How many related products the product page returns (PDP-06).</summary>
    public int RelatedProductCount { get; set; } = 4;

    /// <summary>
    /// How many products one <c>sitemap.xml</c> may list (GLB-11).
    /// <para>
    /// 45,000 rather than a round 50,000 because 50,000 is the sitemap protocol's own hard limit for a
    /// single file, and a limit a document is allowed to reach exactly is a limit it will one day be
    /// one row past. The margin is the room to notice: the reader reports when the cap is reached, and
    /// the answer is then a sitemap index with several files, which is a change to make deliberately
    /// rather than discover from a crawler's error report. The figure also bounds the one public
    /// endpoint that enumerates the catalogue, which is the other reason it exists.
    /// </para>
    /// </summary>
    public int SitemapMaxProducts { get; set; } = 45_000;
}
