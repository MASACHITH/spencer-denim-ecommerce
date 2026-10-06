using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// Turns the bag plus a chosen district and method into the figures both the checkout page and the order
/// row use.
/// <para>
/// <b>There is one of these and the placement path calls it too.</b> The brief's standing lesson on this
/// project is a summary whose rows did not add up, and the only structural defence is that the number on
/// the screen and the number in the database come out of the same function. <c>GetCheckoutQueryHandler</c>
/// renders what this returns; <c>PlaceOrderCommandHandler</c> compares it with what the shopper saw and
/// then stores it.
/// </para>
/// </summary>
internal static class CheckoutQuote
{
    /// <summary>
    /// The lines that are priced into the summary.
    /// <para>
    /// <b>Exactly the bag's rule</b> (<c>CartReader.Totals</c> and the storefront's <c>isBuyable</c>):
    /// <see cref="CartLineStatus.Buyable"/> and <see cref="CartLineStatus.NotEnoughStock"/> count,
    /// <see cref="CartLineStatus.OutOfStock"/> and <see cref="CartLineStatus.Unavailable"/> do not. Having
    /// checkout apply a different rule would make the two pages print different subtotals for one bag,
    /// which is the same class of defect as a ladder that does not add up.
    /// </para>
    /// <para>
    /// A short-stock line being <i>priced</i> is not the same as it being <i>placeable</i>: placement
    /// requires every line to be <see cref="CartLineStatus.Buyable"/> (G-08 — warn and block, never reduce
    /// a quantity for the shopper).
    /// </para>
    /// </summary>
    public static bool IsPriced(CheckoutCartLine line) =>
        line.Status is CartLineStatus.Buyable or CartLineStatus.NotEnoughStock;

    /// <summary>Every line must be buyable before an order may be placed (doc 11 P5, G-08).</summary>
    public static bool IsPlaceable(CheckoutCartLine line) => line.Status is CartLineStatus.Buyable;

    /// <summary>
    /// Prices the bag, or returns <see langword="null"/> when there is nothing to price — an empty bag, or
    /// one whose every line has gone.
    /// </summary>
    /// <param name="rate">
    /// The zone rate for the chosen district and method, or <see langword="null"/> when no option has been
    /// chosen yet. Null gives a <b>partial</b> quote: item figures only, with no fee and no grand total,
    /// because doc 08 §6 step 10 has no fee to give without a rate and a zero would read as free delivery.
    /// </param>
    public static CheckoutTotalsDto? Totals(
        IReadOnlyList<CheckoutCartLine> lines,
        CheckoutZoneRate? rate)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var priced = lines.Where(IsPriced).ToList();
        var itemCount = lines.Sum(line => (int)line.Quantity);

        // The count that goes beside the Subtotal *figure*, so the two describe the same lines. The bag
        // has had this distinction since W2-09 (`CartTotalsDto.BuyableItemCount`); checkout printed
        // `itemCount` against a subtotal that excludes the unbuyable lines, and so said "3 items" for a
        // sum of two.
        var buyableItemCount = priced.Sum(line => (int)line.Quantity);

        if (priced.Count == 0)
        {
            return lines.Count == 0
                ? null
                : new CheckoutTotalsDto(
                    itemCount,
                    buyableItemCount,
                    lines.Count,
                    0m,
                    0m,
                    0m,
                    0m,
                    null,
                    0m,
                    null,
                    false,
                    PriceDto.Lkr);
        }

        if (rate is not { IsAvailable: true } available)
        {
            // Item figures only. Summed here rather than through OrderPricing, because that function
            // refuses to price without a delivery rate on purpose (see its remarks) — an order has to have
            // a delivery decision, a quote does not yet.
            var original = priced.Sum(line => line.UnitOriginalPrice * line.Quantity);
            var subtotal = priced.Sum(line => line.UnitPrice * line.Quantity);

            return new CheckoutTotalsDto(
                itemCount,
                buyableItemCount,
                lines.Count,
                original,
                subtotal,
                original - subtotal,
                0m,
                null,
                0m,
                null,
                false,
                PriceDto.Lkr);
        }

        var pricing = Price(priced, available);

        return new CheckoutTotalsDto(
            itemCount,
            buyableItemCount,
            lines.Count,
            pricing.ItemsOriginalTotal,
            pricing.ItemsSubtotal,

            // The markdown and campaign savings, which is what the bag's "Savings" row shows. The promo
            // discount is a row of its own, so that `Subtotal − Savings − Promo + Delivery = Total` reads
            // on screen as the subtraction it is.
            pricing.MarkdownDiscountTotal + pricing.CampaignDiscountTotal,
            pricing.PromoDiscountAmount,
            pricing.DeliveryFeeAmount,
            pricing.DeliveryDiscountAmount,
            pricing.GrandTotal,
            pricing.PromoDiscountClamped,
            PriceDto.Lkr);
    }

    /// <summary>
    /// Doc 08 §6 over the priced lines. The single place the Domain's calculator is called from, so the
    /// quote and the stored order cannot diverge.
    /// </summary>
    /// <remarks>
    /// <b>No promo code is passed, and that is a gap rather than a decision.</b> Doc 08 §6 steps 7 and 8
    /// read <c>marketing.PromoCodes</c>, which no migration has created (ADM-13, W3-05). The calculator
    /// takes a promo outcome and applies steps 9 and 11 — the allocation and the Rs. 1.00 clamp — so W3-05
    /// changes this one argument and nothing else about the totals.
    /// </remarks>
    public static OrderPricingResult Price(
        IReadOnlyList<CheckoutCartLine> pricedLines,
        CheckoutZoneRate rate)
    {
        ArgumentNullException.ThrowIfNull(pricedLines);

        var lines = pricedLines
            .Select(line => new OrderPricingLine(
                line.VariantId,
                line.Quantity,
                line.UnitOriginalPrice,
                line.UnitListPrice,
                line.UnitPrice))
            .ToList();

        return OrderPricing.Price(
            lines,
            new DeliveryCharge(rate.Fee, rate.FreeAboveAmount),
            promo: null);
    }

    /// <summary>
    /// The fee this option would add to the bag as it stands, for the option list: the rate's fee, or zero
    /// once its free-delivery threshold is met by the items subtotal.
    /// <para>
    /// It mirrors doc 08 §6 step 10 rather than reimplementing it: with no promo code in the picture the
    /// post-promo subtotal is the subtotal, so the comparison is the same one.
    /// </para>
    /// </summary>
    public static decimal FeeAfterDiscount(CheckoutZoneRate rate, decimal itemsSubtotal) =>
        rate.FreeAboveAmount is { } threshold && itemsSubtotal >= threshold ? 0m : rate.Fee;
}
