using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales;

/// <summary>
/// Reads the bag (CART-01, CART-02).
/// <para>
/// <b>Everything about money is recomputed here, on every read.</b> Doc 08 §6: "prices are computed on
/// every cart read and again at placement". <c>CartItems.UnitPriceAtAdd</c> is carried through only so the
/// storefront can say the price has changed (§3.5), and the subtotal is never built from it. A stale
/// price is a commercial risk in both directions — an old low price the shop would have to honour, an old
/// high one the shopper would be overcharged by — and the only way neither can happen is for the stored
/// figure to have no path into any total.
/// </para>
/// <para>
/// <b>Why <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}(IQueryable{TEntity})"/>.</b>
/// <c>Product</c>, <c>Category</c> and <c>MediaAsset</c> all carry the soft-delete query filter (doc 08
/// §10.2). A cart line's product is a <i>required</i> navigation, so with the filter on, a line whose
/// product was deleted would be dropped from the join and the item would simply vanish out of the
/// shopper's bag with no message at all. That is the worst available behaviour, so the filter is turned
/// off and <c>IsDeleted</c> is read as data: the line is still shown, named, removable, and marked
/// <see cref="CartLineStatus.Unavailable"/>.
/// </para>
/// </summary>
internal sealed class CartReader(
    AppDbContext dbContext,
    IOptions<CatalogOptions> options,
    ICampaignOfferReader campaigns) : ICartReader
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<CartDto> ReadAsync(int? cartId, CancellationToken cancellationToken)
    {
        if (cartId is not { } id)
        {
            // No cart row, so nothing to read. Doc 08 §3.5: the row is created on the first Add to Bag,
            // which means "no cart" is the ordinary state of most visitors and must cost no query.
            return CartDto.Empty;
        }

        var rows = await dbContext.CartItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(item => item.CartId == id)
            .OrderBy(item => item.AddedAtUtc)
            .ThenBy(item => item.Id)
            .Select(item => new CartLineRow(
                item.VariantId,
                item.Quantity,
                item.UnitPriceAtAdd,
                item.Variant!.Sku,
                item.Variant.IsActive,
                item.Variant.PriceOverride,
                item.Variant.CompareAtPriceOverride,
                item.Variant.ProductColourId,
                item.Variant.ProductColour!.Name,
                item.Variant.SizeId,
                item.Variant.Size!.Label,
                item.Variant.Size.ScaleName,
                item.Variant.ProductId,
                item.Variant.Product!.Name,
                item.Variant.Product.Slug,
                item.Variant.Product.BasePrice,
                item.Variant.Product.CompareAtPrice,
                item.Variant.Product.IsPublished,
                item.Variant.Product.IsDeleted,
                item.Variant.Product.Department!.IsActive,
                item.Variant.Product.PrimaryCategory!.IsActive,
                item.Variant.Product.PrimaryCategory.IsDeleted,
                item.Variant.StockLevel == null ? 0 : item.Variant.StockLevel.Available,
                // The photograph of the colourway in the bag, not of the product's default colour: a bag
                // line that showed the wrong colour would contradict the words next to it. Falls back to
                // the card image, then to nothing, which the storefront draws as a placeholder.
                item.Variant.Product.Media
                    .Where(media =>
                        media.ProductColourId == item.Variant.ProductColourId
                        && media.MediaRole == ProductMediaRole.Gallery
                        && media.MediaAsset!.Status == MediaAssetStatus.Ready
                        && !media.MediaAsset.IsDeleted)
                    .OrderBy(media => media.SortOrder)
                    .ThenBy(media => media.Id)
                    .Select(media => new MediaRef(
                        media.MediaAsset!.StorageKey,
                        media.MediaAsset.PublicId,
                        media.MediaAsset.RenditionSetVersion,
                        media.MediaAsset.Width))
                    .FirstOrDefault()
                    ?? (item.Variant.Product.CardImageAsset != null
                        && item.Variant.Product.CardImageAsset.Status == MediaAssetStatus.Ready
                        && !item.Variant.Product.CardImageAsset.IsDeleted
                            ? new MediaRef(
                                item.Variant.Product.CardImageAsset.StorageKey,
                                item.Variant.Product.CardImageAsset.PublicId,
                                item.Variant.Product.CardImageAsset.RenditionSetVersion,
                                item.Variant.Product.CardImageAsset.Width)
                            : null),
                item.Variant.Product.Media
                    .Where(media =>
                        media.ProductColourId == item.Variant.ProductColourId
                        && media.MediaRole == ProductMediaRole.Gallery
                        && media.MediaAsset!.Status == MediaAssetStatus.Ready
                        && !media.MediaAsset.IsDeleted)
                    .OrderBy(media => media.SortOrder)
                    .ThenBy(media => media.Id)
                    .Select(media => media.AltTextOverride ?? media.MediaAsset!.AltText)
                    .FirstOrDefault()
                    ?? (item.Variant.Product.CardImageAsset != null
                        && item.Variant.Product.CardImageAsset.Status == MediaAssetStatus.Ready
                        && !item.Variant.Product.CardImageAsset.IsDeleted
                            ? item.Variant.Product.CardImageAsset.AltText
                            : null)))
            .ToListAsync(cancellationToken);

        // Doc 08 §6 step 3, live. One extra round-trip for the whole bag, and only when it has a line:
        // doc 08 §7.4's stored price summary is for listings and may lag five minutes at a campaign
        // boundary, so the bag — whose figures a shopper is about to be charged — reads campaigns itself.
        var offers = await campaigns.ReadAsync(
            rows.Select(row => row.ProductId).Distinct().ToList(),
            cancellationToken);

        var lines = rows.Select(row => ToLine(row, offers, _options.MediaBaseUrl)).ToList();
        return new CartDto(lines.AsReadOnly(), Totals(lines));
    }

    public async Task<CartVariantSnapshot?> FindSellableVariantAsync(
        int variantId,
        CancellationToken cancellationToken)
    {
        // **`CatalogVisibility.VisibleProduct`, composed rather than spelled out again.** A variant the
        // storefront could never link to is one the bag must refuse — otherwise a category the client
        // has deactivated can still be bought from by anyone who kept a variant id — and "what a
        // shopper may reach" has exactly one definition (GLB-11, W2-10). Writing `IsPublished` and the
        // two `IsActive`s here would have been a fourth copy of it, free to drift the next time an
        // admin gains a way to hide something.
        //
        // The query therefore starts at `Products` rather than at `ProductVariants`, which is what lets
        // the expression be applied at all: `VisibleProduct` is an `Expression<Func<Product, bool>>` and
        // there is no way to invoke one against `variant.Product` inside an EF predicate without an
        // expression rewriter. `SelectMany` over the product's variants produces the same single join
        // the three `Where` clauses did. The variant's own `IsActive` stays here: doc 08's
        // `CatalogVisibility` deliberately leaves variant activity out, because a deactivated variant
        // changes a product page's size list rather than making a URL appear or disappear.
        var row = await dbContext.Products
            .AsNoTracking()
            .Where(Catalog.CatalogVisibility.VisibleProduct)
            .SelectMany(product => product.Variants)
            .Where(variant => variant.Id == variantId && variant.IsActive)
            .Select(variant => new
            {
                variant.Id,
                variant.ProductId,
                variant.PriceOverride,
                BasePrice = variant.Product!.BasePrice,
                Available = variant.StockLevel == null ? 0 : variant.StockLevel.Available,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var list = CatalogPricing.ListPrice(row.BasePrice, row.PriceOverride);

        // Doc 08 §6 step 3 for the one variant being added. `UnitPriceAtAdd` is a snapshot the bag only
        // ever uses to say "the price has changed" (doc 08 §3.5), never to total anything — but it is the
        // figure the shopper saw on the product page, so it has to be the campaign price rather than the
        // list price, or every add during a sale would immediately announce its own price as changed.
        var offers = await campaigns.ReadAsync([row.ProductId], cancellationToken);

        return new CartVariantSnapshot(
            row.Id,
            CatalogPricing.SalePrice(list, offers.Best(row.ProductId, list)?.Price),
            Math.Max(row.Available, 0));
    }

    private static CartLineDto ToLine(CartLineRow row, CampaignOfferSet offers, string? mediaBaseUrl)
    {
        var sellable = row is { IsPublished: true, IsProductDeleted: false, IsVariantActive: true }
            && row.IsDepartmentActive
            && row.IsCategoryActive
            && !row.IsCategoryDeleted;

        var list = CatalogPricing.ListPrice(row.BasePrice, row.PriceOverride);
        var original = CatalogPricing.OriginalPrice(list, row.CompareAtPrice, row.CompareAtPriceOverride);
        var sale = CatalogPricing.SalePrice(list, offers.Best(row.ProductId, list)?.Price);
        var available = Math.Max(row.Available, 0);

        var status = !sellable
            ? CartLineStatus.Unavailable
            : available <= 0
                ? CartLineStatus.OutOfStock
                : available < row.Quantity
                    ? CartLineStatus.NotEnoughStock
                    : CartLineStatus.Buyable;

        // An unavailable line carries no *current* price: the shop has withdrawn the product, so quoting
        // what it used to cost would be quoting an offer that no longer stands — the same mistake as
        // pricing from UnitPriceAtAdd, one line further down the page.
        //
        // **Narrowly: "no price at all" is true of what is rendered and announced, not of the payload.**
        // `UnitPriceAtAdd` still travels on every line, this one included, because the storefront needs it
        // for "the price has changed" — so the historical figure is on the wire and in the bag page's
        // transfer state. That is not a leak (it is the shopper's own bag, on a `no-store` page) but it is
        // narrower than "the price is gone", and the earlier wording overstated it.
        var price = sellable
            ? new PriceDto(
                sale,
                original,
                CatalogPricing.DiscountPercent(original, sale),
                CatalogPricing.HasDiscount(original, sale),
                PriceDto.Lkr)
            : null;

        return new CartLineDto(
            row.VariantId,
            row.ProductId,
            row.ProductName,
            row.Slug,
            row.Sku,
            row.ColourId,
            row.ColourName,
            row.SizeId,
            row.SizeLabel,
            row.ScaleName,
            MediaUrlBuilder.ToRenditionUrl(mediaBaseUrl, row.Image, MediaRenditions.Thumb),
            row.Image is null ? null : row.ImageAltText,
            row.Quantity,
            MaxQuantityFor(row.Quantity, available, sellable),
            available,
            price,
            price is null ? null : price.Current * row.Quantity,
            row.UnitPriceAtAdd,
            // Compared against today's price, not against the "was" price: what changed is what the
            // shopper would pay. Only claimed for a line that still has a price at all.
            price is not null && price.Current != row.UnitPriceAtAdd,
            status);
    }

    /// <summary>
    /// The largest quantity the line's selector may offer: the per-line ceiling of 10 (doc 08 §3.5), cut
    /// down to what is in stock, but never below what the line already holds.
    /// <para>
    /// That last clause matters: a selector whose maximum is below its own current value cannot re-select
    /// the value it is showing, and a shopper with 3 of something that has 1 left would find the control
    /// refusing to admit the number printed in it.
    /// </para>
    /// </summary>
    private static short MaxQuantityFor(short quantity, int available, bool sellable)
    {
        if (!sellable)
        {
            return quantity;
        }

        var ceiling = Math.Min((int)CartQuantity.Max, Math.Max(available, 0));
        return (short)Math.Max(ceiling, quantity);
    }

    private static CartTotalsDto Totals(List<CartLineDto> lines)
    {
        if (lines.Count == 0)
        {
            return CartTotalsDto.Empty;
        }

        var itemCount = lines.Sum(line => (int)line.Quantity);

        // Buyable only. A line that cannot be sold is not priced into a figure the shopper is being asked
        // to pay — which is the other half of "the subtotal cannot be computed from a price nobody is
        // offering". NotEnoughStock lines are included at the quantity the shopper chose: the price is
        // real and the items may be restocked, and the line says how many there are.
        var buyable = lines
            .Where(line => line.Status is CartLineStatus.Buyable or CartLineStatus.NotEnoughStock)
            .ToList();

        var subtotal = buyable.Sum(line => line.Price!.Current * line.Quantity);
        var originalTotal = buyable.Sum(line => line.Price!.Original * line.Quantity);

        return new CartTotalsDto(
            itemCount,
            buyable.Sum(line => (int)line.Quantity),
            lines.Count,
            originalTotal,
            subtotal,
            originalTotal - subtotal,
            PriceDto.Lkr);
    }

    /// <summary>One <c>sales.CartItems</c> row joined to everything the bag has to say about it.</summary>
    private sealed record CartLineRow(
        int VariantId,
        short Quantity,
        decimal UnitPriceAtAdd,
        string Sku,
        bool IsVariantActive,
        decimal? PriceOverride,
        decimal? CompareAtPriceOverride,
        int ColourId,
        string ColourName,
        int SizeId,
        string SizeLabel,
        string ScaleName,
        int ProductId,
        string ProductName,
        string Slug,
        decimal BasePrice,
        decimal? CompareAtPrice,
        bool IsPublished,
        bool IsProductDeleted,
        bool IsDepartmentActive,
        bool IsCategoryActive,
        bool IsCategoryDeleted,
        int Available,
        MediaRef? Image,
        string? ImageAltText);
}
