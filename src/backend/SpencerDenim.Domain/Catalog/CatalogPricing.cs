using System.Globalization;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// Steps 1, 2 and 4 of the pricing order in doc 08 §6, for the catalogue's read path. Campaigns
/// (step 3) arrive with marketing in week 3; until then the sale price equals the list price, which
/// is why <see cref="SalePrice"/> takes the campaign price as an argument rather than looking it up.
/// </summary>
public static class CatalogPricing
{
    /// <summary>Doc 08 §6 step 3: a campaign price can never push a line below Rs. 1.00.</summary>
    public const decimal MinimumSalePrice = 1.00m;

    /// <summary>Step 1: the list price is the variant override, or the product's base price.</summary>
    public static decimal ListPrice(decimal basePrice, decimal? priceOverride) =>
        priceOverride ?? basePrice;

    /// <summary>
    /// Step 2 (DB17): the original ("was") price is the variant override, else the product's
    /// compare-at price, else the list price — and never below the list price, so a displayed
    /// discount can never be negative.
    /// </summary>
    public static decimal OriginalPrice(decimal listPrice, decimal? productCompareAtPrice, decimal? variantCompareAtOverride)
    {
        var candidate = variantCompareAtOverride ?? productCompareAtPrice ?? listPrice;
        return Math.Max(candidate, listPrice);
    }

    /// <summary>Step 3: the campaign price, floored at <see cref="MinimumSalePrice"/>. No campaign → the list price.</summary>
    public static decimal SalePrice(decimal listPrice, decimal? campaignPrice)
    {
        if (campaignPrice is null || campaignPrice >= listPrice)
        {
            return listPrice;
        }

        return Math.Max(campaignPrice.Value, MinimumSalePrice);
    }

    /// <summary>
    /// Step 4: the displayed discount, <c>floor((original − sale) / original × 100)</c>. Zero when the
    /// original price is zero or not above the sale price, so the card never shows "−0%".
    /// </summary>
    public static decimal DiscountPercent(decimal originalPrice, decimal salePrice)
    {
        if (originalPrice <= 0m || salePrice >= originalPrice)
        {
            return 0m;
        }

        return Math.Floor((originalPrice - salePrice) / originalPrice * 100m);
    }

    /// <summary>True when the card must show a struck-through original price and a discount badge.</summary>
    public static bool HasDiscount(decimal originalPrice, decimal salePrice) =>
        DiscountPercent(originalPrice, salePrice) > 0m;

    /// <summary>Formats an LKR amount for logs and seed text. The storefront formats for display itself.</summary>
    public static string FormatLkr(decimal amount) =>
        string.Create(CultureInfo.InvariantCulture, $"Rs. {amount:N2}");
}
