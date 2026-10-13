using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Domain.Marketing;

/// <summary>
/// One active campaign as doc 08 §6 step 3 reads it: the three columns the arithmetic needs, and the id,
/// because <c>sales.OrderLines.SaleCampaignId</c> records which campaign priced a line.
/// </summary>
/// <remarks>
/// A snapshot and not the entity, for the reason <see cref="PromoCodeTerms"/> gives: the reader projects
/// three columns instead of a row with its audit, soft-delete and rowversion columns, and
/// <see cref="SaleCampaign.IsDeleted"/> has a private setter, so a unit test could not construct a deleted
/// campaign to check that it was ignored. <b><c>IsDeleted</c>, <c>IsActive</c> and the window are
/// deliberately not on this type.</b> A campaign that is inactive, deleted or out of window must never
/// reach the arithmetic at all — <see cref="CampaignWindow.IsLive"/> is the one predicate that decides
/// that, the model's global soft-delete filter removes deleted rows, and the reader applies both.
/// </remarks>
public readonly record struct CampaignOffer(int Id, SaleDiscountType DiscountType, decimal DiscountValue);

/// <summary>
/// The outcome of doc 08 §6 step 3 for one variant: which campaign won, and what it charges.
/// </summary>
/// <param name="CampaignId">
/// The winner's <c>marketing.SaleCampaigns.Id</c>, for <c>sales.OrderLines.SaleCampaignId</c>.
/// </param>
/// <param name="Price">
/// The resulting unit price, already floored at <see cref="CatalogPricing.MinimumSalePrice"/> and always
/// strictly below the list price it was computed from.
/// </param>
public readonly record struct CampaignPrice(int CampaignId, decimal Price);

/// <summary>
/// Doc 08 §6 <b>step 3</b>: "For each active campaign targeting the product … Percent →
/// <c>round(list × (1 − v/100))</c>; Fixed → <c>list − v</c>. Lowest wins, floor at Rs. 1.00, no
/// stacking."
/// <para>
/// <b>"Lowest wins" is a rule about the resulting price, not about the discount and not about the
/// target.</b> Nothing in doc 08 §3.6, doc 08 §6 or doc 11 P3 makes a product target beat a department
/// target, and no precedence rule is invented here. A 30%-off department campaign and a Rs. 500-off
/// product campaign therefore resolve <i>per variant</i>: at a list price of Rs. 1,000.00 the fixed one
/// wins (500.00 against 700.00) and at Rs. 2,000.00 the percentage one does (1,400.00 against 1,500.00),
/// so one pair of campaigns can be won by different campaigns on two products — or on two variants of one
/// product, which is why this takes a list price and not a product.
/// </para>
/// <para>
/// <b>Pure, and in the Domain, for the reason <see cref="PromoEvaluator"/> and
/// <see cref="Sales.OrderPricing"/> are.</b> The bag, the checkout quote, the placement transaction and
/// the <c>RefreshCampaignPricing</c> job all have to reach the same price; doc 08 §7.4 makes the last of
/// those a batched <c>UPDATE</c> that computes in T-SQL, so this file is also the specification that
/// statement is tested against rather than a second implementation of it.
/// </para>
/// </summary>
public static class CampaignPricing
{
    /// <summary>LKR has two decimal places, and doc 08 §6 rounds "half away from zero to 2 dp".</summary>
    private const int MoneyDecimals = 2;

    /// <summary>
    /// The price one campaign would charge for a unit at <paramref name="listPrice"/>, floored at
    /// <see cref="CatalogPricing.MinimumSalePrice"/>.
    /// </summary>
    /// <remarks>
    /// The floor is applied here rather than only to the winner, and the two give the same answer because
    /// flooring is monotonic. It is here so that no caller can read an unfloored figure: a Rs. 500-off
    /// campaign on a Rs. 200.00 item is Rs. 1.00 and never minus Rs. 300.00, and
    /// <c>CK_OrderLines_UnitPrice</c> would reject the latter inside a placement transaction, which the
    /// shopper would meet as a failed checkout with no explanation.
    /// </remarks>
    public static decimal ResultingPrice(CampaignOffer offer, decimal listPrice)
    {
        var price = offer.DiscountType switch
        {
            // Round, then floor: doc 08 §6 step 3's own order. Rounding after the floor could in principle
            // put the price back under Rs. 1.00; it cannot here, 1.00 being exact at two decimal places,
            // so the order is the document's rather than a property of this particular floor.
            SaleDiscountType.Percent => Round(listPrice * (1m - (offer.DiscountValue / 100m))),
            SaleDiscountType.FixedAmount => listPrice - offer.DiscountValue,

            // A tinyint outside CK_SaleCampaigns_DiscountType, which the constraint makes unstorable.
            // Loud rather than silently charging the list price: a campaign the shop is advertising that
            // quietly discounts nothing is the defect, not the exception.
            _ => throw new InvalidOperationException(
                $"Sale campaign {offer.Id} has discount type {(byte)offer.DiscountType}, which "
                + "CK_SaleCampaigns_DiscountType does not allow."),
        };

        return Math.Max(price, CatalogPricing.MinimumSalePrice);
    }

    /// <summary>
    /// Doc 08 §6 step 3's "lowest wins, no stacking": the one campaign that produces the lowest price for
    /// a unit at <paramref name="listPrice"/>, or <see langword="null"/> when none of them beats the list
    /// price.
    /// </summary>
    /// <param name="offers">
    /// The campaigns that target this product and are live <i>now</i>, in any order. An empty set is the
    /// ordinary case and answers <see langword="null"/>.
    /// </param>
    /// <param name="listPrice">
    /// Step 1's list price for the variant being priced — <c>Variant.PriceOverride ?? Product.BasePrice</c>.
    /// </param>
    /// <remarks>
    /// <b>Two campaigns can tie, and the tie is broken by the lower id</b> so that two reads of one bag
    /// cannot attribute the same price to two different campaigns. Which one wins is arbitrary — the price
    /// is identical either way — but it must be the <i>same</i> arbitrary one on the quote, at placement
    /// and in the batched refresh, because <c>OrderLines.SaleCampaignId</c> is a stored fact and an
    /// "ends soonest" rule would change the winner as a clock moved.
    /// <para>
    /// <b>A campaign that does not beat the list price is not a winner.</b> With a floor of Rs. 1.00 and
    /// the check constraints (percent in (0, 90], fixed &gt; 0) that can only happen at a list price of
    /// Rs. 1.00, where every campaign resolves to Rs. 1.00 — and returning a <see cref="CampaignPrice"/>
    /// there would stamp <c>SaleCampaignId</c> on a line that was never discounted, show a "−0%" badge and
    /// make <c>ExcludeSaleItems</c> throw the line out of a promo code's eligible base for nothing.
    /// </para>
    /// </remarks>
    public static CampaignPrice? Best(IEnumerable<CampaignOffer> offers, decimal listPrice)
    {
        ArgumentNullException.ThrowIfNull(offers);

        CampaignPrice? best = null;

        foreach (var offer in offers)
        {
            var price = ResultingPrice(offer, listPrice);

            if (price >= listPrice)
            {
                continue;
            }

            if (best is not { } winner
                || price < winner.Price
                || (price == winner.Price && offer.Id < winner.CampaignId))
            {
                best = new CampaignPrice(offer.Id, price);
            }
        }

        return best;
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyDecimals, MidpointRounding.AwayFromZero);
}
