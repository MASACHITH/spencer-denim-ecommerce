using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>
/// The four things doc 11 P1 step 13 says a product save has to do "in the same transaction" besides
/// writing the row: refresh the price summary, rebuild the search text, set the card and hover image, and
/// write the audit row. The first three are here; the audit row is <see cref="CatalogAuditTrail"/>'s.
/// <para>
/// <b>Why they are pure functions over the aggregate and not an infrastructure job.</b> Doc 08 §7.4 lists
/// the sync rules for <c>ProductStats</c> and <c>ProductSearchIndex</c>, and every one of them that this
/// task can trigger — a price change, a variant added, a colour renamed, a category moved — happens inside
/// a product save. A nightly refresh instead would mean the listing priced yesterday's catalogue; a
/// trigger or a stored procedure would put the rule where no test can reach it without a database. The
/// part doc 08 reserves for the nightly job (<c>SoldQty30d</c>, the rating) is deliberately left alone
/// here: see <see cref="RefreshStats"/>.
/// </para>
/// </summary>
public static class ProductDerivedData
{
    /// <summary><c>catalog.ProductSearchIndex.SearchText</c> is <c>nvarchar(2000)</c>.</summary>
    public const int SearchTextMaxLength = 2000;

    /// <summary><c>catalog.ProductSearchIndex.NameText</c> is <c>nvarchar(200)</c>.</summary>
    public const int NameTextMaxLength = 200;

    /// <summary>
    /// Doc 08 §6 steps 1, 2 and 4, aggregated over the product's <b>active</b> variants — the same
    /// arithmetic <c>DemoCatalogueSeeder.BuildStats</c> does, through the same
    /// <see cref="CatalogPricing"/> functions, so the seeded catalogue and an admin-edited one cannot
    /// disagree about a price.
    /// <para>
    /// <b>Only the pricing columns move.</b> <c>RatingAverage</c>, <c>RatingCount</c>, <c>SoldQty30d</c> and
    /// <c>SoldQtyTotal</c> belong to the review moderation and the nightly sales job (doc 08 §7.4), and
    /// <c>StatsRefreshedAtUtc</c> is their timestamp. Writing them here would reset a product's rating to
    /// nothing every time an admin fixed a typo in its description.
    /// </para>
    /// <para>
    /// <b>Inactive variants are excluded, and a product with none falls back to its own base price.</b> An
    /// empty aggregate has no min and no max, and <c>CK_ProductStats_PriceRange</c> would refuse a row
    /// built from zeroes next to a positive <c>MaxOriginalPrice</c>. A product with no active variant is
    /// also a product G-01 will not let anybody publish, so the fallback is a placeholder for a row that
    /// no shopper can reach rather than a price anybody is shown.
    /// </para>
    /// </summary>
    public static void RefreshStats(Product product, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(product);

        var stats = product.Stats ??= new ProductStats { ProductId = product.Id };

        var active = product.Variants.Where(variant => variant.IsActive).ToList();

        List<decimal> listPrices = active.Count == 0
            ? [product.BasePrice]
            : active.Select(variant => CatalogPricing.ListPrice(product.BasePrice, variant.PriceOverride)).ToList();

        List<decimal> originalPrices = active.Count == 0
            ? [CatalogPricing.OriginalPrice(product.BasePrice, product.CompareAtPrice, null)]
            : active
                .Select(variant =>
                {
                    var list = CatalogPricing.ListPrice(product.BasePrice, variant.PriceOverride);
                    return CatalogPricing.OriginalPrice(list, product.CompareAtPrice, variant.CompareAtPriceOverride);
                })
                .ToList();

        List<decimal> discounts = active.Count == 0
            ? [CatalogPricing.DiscountPercent(
                CatalogPricing.OriginalPrice(product.BasePrice, product.CompareAtPrice, null),
                CatalogPricing.SalePrice(product.BasePrice, campaignPrice: null))]
            : active
                .Select(variant =>
                {
                    var list = CatalogPricing.ListPrice(product.BasePrice, variant.PriceOverride);
                    var original = CatalogPricing.OriginalPrice(
                        list, product.CompareAtPrice, variant.CompareAtPriceOverride);
                    return CatalogPricing.DiscountPercent(
                        original, CatalogPricing.SalePrice(list, campaignPrice: null));
                })
                .ToList();

        var maxDiscount = discounts.Max();

        stats.MinListPrice = listPrices.Min();
        stats.MaxListPrice = listPrices.Max();

        // **The sale price here is the list price, and a campaign is not considered — because a pure
        // function over this aggregate cannot see one.** Doc 08 §6 step 3's campaigns live in
        // `marketing.SaleCampaigns` (ADM-14), and a product save that is running during a live campaign
        // therefore writes these three columns back up to the undiscounted figures. That is corrected
        // immediately after the save commits: `CatalogWriteSupport.WithCampaignPricingAsync` runs
        // `ICampaignPricingRefresher` for this one product, which recomputes the same three columns with
        // the campaigns applied. Do not "fix" this by passing a campaign price in — doc 08 §7.4 requires
        // the campaign recompute to be one set-based statement reading committed rows, which is the one
        // thing a function inside the unit of work cannot be.
        stats.MinSalePrice = listPrices.Min();
        stats.MaxSalePrice = listPrices.Max();
        stats.MaxOriginalPrice = originalPrices.Max();
        stats.MaxDiscountPercent = maxDiscount;
        stats.HasDiscount = maxDiscount > 0m;
        stats.PricingRefreshedAtUtc = utcNow;

        // Only on the row's first appearance: it is the nightly job's column, and NOT NULL.
        if (stats.StatsRefreshedAtUtc == default)
        {
            stats.StatsRefreshedAtUtc = utcNow;
        }
    }

    /// <summary>
    /// Doc 08 §3.2: "Name, category path names, colour names and families, material, fit, occasion,
    /// ProductCode, SKUs", lower-cased and space separated.
    /// <para>
    /// Built from every category the product is in, not only the primary one, because a product linked into
    /// "Sale" or "New In" is a product a shopper may search for by that word. Duplicates are dropped
    /// case-insensitively so a product whose colour and colour family are both "Blue" does not spend two of
    /// its 2,000 characters saying it twice.
    /// </para>
    /// </summary>
    public static void RefreshSearchIndex(
        Product product,
        IReadOnlyCollection<CategoryReference> categories,
        ProductReferenceData references,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(references);

        var index = product.SearchIndex ??= new ProductSearchIndex { ProductId = product.Id };

        var parts = new List<string>(48) { product.Name };

        foreach (var category in categories)
        {
            parts.AddRange(category.PathNames);
        }

        foreach (var colour in product.Colours)
        {
            parts.Add(colour.Name);

            if (references.ColourFamilies.TryGetValue(colour.ColourFamilyId, out var family))
            {
                parts.Add(family);
            }
        }

        foreach (var attributeId in new[] { product.MaterialId, product.FitId, product.OccasionId })
        {
            if (attributeId is { } id && references.AttributeOptions.TryGetValue(id, out var attribute))
            {
                parts.Add(attribute.Name);
            }
        }

        parts.Add(product.ProductCode);
        parts.AddRange(product.Variants.Select(variant => variant.Sku));

        var searchText = string.Join(
                ' ',
                parts
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            .ToLowerInvariant();

        index.NameText = Truncate(product.Name.ToLowerInvariant(), NameTextMaxLength);
        index.SearchText = Truncate(searchText, SearchTextMaxLength);
        index.UpdatedAtUtc = utcNow;
    }

    /// <summary>
    /// Doc 08 §3.2: <c>CardImageAssetId</c> and <c>HoverImageAssetId</c> are "denormalised from
    /// ProductMedia on save", which is what lets a listing of 24 cards read two columns instead of joining
    /// the gallery.
    /// <para>
    /// The card image is the <b>first gallery image</b> in sort order. The hover image is the row an admin
    /// marked <see cref="ProductMediaRole.Hover"/>, and when there is none it is the <b>second</b> gallery
    /// image — CARD-01's hover effect is the next picture of the same product, so the obvious default is
    /// better than no hover at all. A product with one image gets no hover rather than a hover of itself,
    /// which would read to a shopper as a card that is broken.
    /// </para>
    /// <para>
    /// A video is never either of them: doc 11 P1's WooCommerce comparison is explicit that video "can
    /// never be the main image", and a card whose image was an MP4 renders as nothing.
    /// </para>
    /// </summary>
    public static void RefreshCardAndHoverImages(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var gallery = product.Media
            .Where(media => media.MediaRole == ProductMediaRole.Gallery)
            .OrderBy(media => media.SortOrder)
            .ThenBy(media => media.Id)
            .ToList();

        var hover = product.Media
            .Where(media => media.MediaRole == ProductMediaRole.Hover)
            .OrderBy(media => media.SortOrder)
            .ThenBy(media => media.Id)
            .ToList();

        product.CardImageAssetId = gallery.Count > 0 ? gallery[0].MediaAssetId : null;

        product.HoverImageAssetId = hover.Count > 0
            ? hover[0].MediaAssetId
            : gallery.Count > 1 ? gallery[1].MediaAssetId : null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
