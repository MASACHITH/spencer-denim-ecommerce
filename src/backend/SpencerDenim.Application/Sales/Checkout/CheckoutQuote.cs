using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Marketing;
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
    /// Doc 08 §6 steps 7 and 8 over the lines that are being priced (ADM-13).
    /// </summary>
    /// <remarks>
    /// <b>The lines it evaluates are the lines that will be priced, and that is not a detail.</b>
    /// <see cref="PromoEvaluator"/>'s eligible base and <c>OrderPricing</c>'s step 9 allocation must agree
    /// or step 9 throws "a promo discount was given with no eligible line to allocate it to" — so both
    /// read the same <see cref="IsPriced"/> set, mapped by the same <see cref="ToPricingLines"/>.
    /// </remarks>
    /// <param name="pricedLines">The <see cref="IsPriced"/> lines.</param>
    /// <param name="terms">
    /// The row the reader found, or <see langword="null"/> when the shopper typed a code that matches no
    /// undeleted row — which is <see cref="PromoRefusal.NotRecognised"/> and not "no code".
    /// </param>
    /// <param name="utcNow">The instant the window is judged against.</param>
    public static PromoEvaluation EvaluatePromo(
        IReadOnlyList<CheckoutCartLine> pricedLines,
        PromoCodeTerms? terms,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(pricedLines);

        return terms is { } found
            ? PromoEvaluator.Evaluate(found, ToPricingLines(pricedLines), utcNow)
            : PromoEvaluation.Refused(PromoRefusal.NotRecognised);
    }

    /// <summary>
    /// Prices the bag, or returns <see langword="null"/> when there is nothing to price — an empty bag, or
    /// one whose every line has gone.
    /// </summary>
    /// <param name="rate">
    /// The zone rate for the chosen district and method, or <see langword="null"/> when no option has been
    /// chosen yet. Null gives a <b>partial</b> quote: item figures only, with no fee and no grand total,
    /// because doc 08 §6 step 10 has no fee to give without a rate and a zero would read as free delivery.
    /// </param>
    /// <param name="promo">
    /// The outcome of <see cref="EvaluatePromo"/> when it applied, or <see langword="null"/> for no code.
    /// A <i>refused</i> code is null here: the refusal is a sentence on the page, never a row in the
    /// ladder.
    /// </param>
    public static CheckoutTotalsDto? Totals(
        IReadOnlyList<CheckoutCartLine> lines,
        CheckoutZoneRate? rate,
        PromoCharge? promo = null)
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

            // **The Discount row is shown before a delivery option is chosen, and the figure is honest
            // rather than final.** A shopper who has just applied a code is owed the news that it worked;
            // what the row cannot yet say is whether step 11's Rs. 1.00 clamp will reduce it, because that
            // needs a delivery fee. The `Min` mirrors OrderPricing's own first clamp, so the partial
            // ladder can never show a discount larger than the items — and the moment a method is chosen,
            // the figure below is recomputed by the one function that applies every step.
            var partialPromo = Math.Min(Math.Max(promo?.Amount ?? 0m, 0m), subtotal);

            return new CheckoutTotalsDto(
                itemCount,
                buyableItemCount,
                lines.Count,
                original,
                subtotal,
                original - subtotal,
                partialPromo,
                null,
                0m,
                null,
                false,
                PriceDto.Lkr);
        }

        var pricing = Price(priced, available, promo);

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
    /// <b>The promo is now a real argument, which is what this method's own note asked for.</b> It used to
    /// pass <c>promo: null</c> and say that doing so was "a gap rather than a decision", because doc 08 §6
    /// steps 7 and 8 read <c>marketing.PromoCodes</c> and no migration had created it. W3-05 created the
    /// table and <see cref="PromoEvaluator"/> is steps 7 and 8; steps 9, 10 and 11 — the allocation, the
    /// delivery discount and the Rs. 1.00 clamp — were already here and are unchanged.
    /// </remarks>
    public static OrderPricingResult Price(
        IReadOnlyList<CheckoutCartLine> pricedLines,
        CheckoutZoneRate rate,
        PromoCharge? promo = null)
    {
        ArgumentNullException.ThrowIfNull(pricedLines);

        return OrderPricing.Price(
            ToPricingLines(pricedLines),
            new DeliveryCharge(rate.Fee, rate.FreeAboveAmount),
            promo);
    }

    /// <summary>
    /// The fee this option would add to the bag as it stands, for the option list: zero when a
    /// free-delivery code pays for it or when the rate's own threshold is met, and the fee otherwise.
    /// <para>
    /// It mirrors doc 08 §6 step 10 rather than reimplementing it — the comparison is the same one, over
    /// the same post-promo subtotal — so the fee printed against an option a shopper has <i>not</i> chosen
    /// is the fee they would be charged if they did.
    /// </para>
    /// </summary>
    /// <param name="rate">The option's zone rate.</param>
    /// <param name="itemsSubtotalAfterPromo">
    /// The items subtotal <b>less the promo discount</b>, which is what step 10 compares with
    /// <c>FreeAboveAmount</c>. Passing the gross subtotal would overstate it and could promise free
    /// delivery that the order then charges for.
    /// </param>
    /// <param name="freeDeliveryPromo">True when the applied code is a free-delivery one.</param>
    public static decimal FeeAfterDiscount(
        CheckoutZoneRate rate,
        decimal itemsSubtotalAfterPromo,
        bool freeDeliveryPromo = false)
    {
        if (freeDeliveryPromo)
        {
            return 0m;
        }

        return rate.FreeAboveAmount is { } threshold && itemsSubtotalAfterPromo >= threshold ? 0m : rate.Fee;
    }

    /// <summary>
    /// The one mapping from a checkout line to the calculator's line, so the evaluator's eligible base and
    /// step 9's allocation are computed over the same values.
    /// </summary>
    private static List<OrderPricingLine> ToPricingLines(IReadOnlyList<CheckoutCartLine> pricedLines) =>
        pricedLines
            .Select(line => new OrderPricingLine(
                line.VariantId,
                line.Quantity,
                line.UnitOriginalPrice,
                line.UnitListPrice,
                line.UnitPrice))
            .ToList();
}
