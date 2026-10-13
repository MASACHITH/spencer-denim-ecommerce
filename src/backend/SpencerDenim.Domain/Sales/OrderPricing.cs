using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// One line going into the arithmetic: how many, and the three prices doc 08 §6 steps 1–3 produced for it.
/// </summary>
/// <param name="UnitOriginalPrice">Step 2's "was" price. Never below <paramref name="UnitListPrice"/>.</param>
/// <param name="UnitListPrice">Step 1's list price.</param>
/// <param name="UnitPrice">Step 3's sale price — what one unit is actually charged at.</param>
/// <param name="SaleCampaignId">
/// Which campaign produced <paramref name="UnitPrice"/>, for <c>sales.OrderLines.SaleCampaignId</c>, or
/// <see langword="null"/> when no campaign did (ADM-14). <b>It takes no part in the arithmetic</b> — the
/// totals are computed from the three prices and nothing else — and is carried here so that the one
/// mapping from a checkout line to a priced line does not have to be done twice. Optional, so the
/// hundreds of existing positional constructions in tests of steps 5 to 12 still read as they did.
/// </param>
public readonly record struct OrderPricingLine(
    int VariantId,
    short Quantity,
    decimal UnitOriginalPrice,
    decimal UnitListPrice,
    decimal UnitPrice,
    int? SaleCampaignId = null);

/// <summary>
/// The delivery rate that applies, as doc 08 §6 step 10 reads it off one
/// <c>sales.DeliveryMethodZoneRates</c> row.
/// </summary>
/// <param name="Fee">The row's <c>Fee</c>. Zero is a real answer (store pickup).</param>
/// <param name="FreeAboveAmount">
/// The row's threshold, or <see langword="null"/> for "never free". Compared against the items subtotal
/// <b>after</b> the promo discount, which is what step 10 says.
/// </param>
public readonly record struct DeliveryCharge(decimal Fee, decimal? FreeAboveAmount);

/// <summary>
/// A promo code's effect, <b>already decided</b> by whoever owns <c>marketing.PromoCodes</c>.
/// </summary>
/// <remarks>
/// <b>Steps 7 and 8 of doc 08 §6 are deliberately not in this file.</b> Eligibility (active, in date,
/// under both usage limits, minimum spend, <c>ExcludeSaleItems</c>) and the percent/fixed/cap arithmetic
/// all read columns of <c>marketing.PromoCodes</c>, which no migration has created (ADM-13, W3-05). What
/// this type carries is the *outcome* of those steps, so that steps 9, 10 and 11 — allocation, delivery
/// and the minimum-total clamp — can be built, tested and relied on now, and W3-05 plugs into them
/// without reopening the totals.
/// </remarks>
/// <param name="Amount">The discount in LKR, before the step 11 clamp. Never negative.</param>
/// <param name="FreeDelivery">
/// True for a <c>FreeDelivery</c> code: step 8 contributes nothing and step 10 discounts the whole fee.
/// </param>
/// <param name="EligibleVariantIds">
/// The lines the discount may be spread over, or <see langword="null"/> for all of them. This is where
/// <c>ExcludeSaleItems</c> lands once it exists.
/// </param>
public sealed record PromoCharge(
    decimal Amount,
    bool FreeDelivery = false,
    IReadOnlySet<int>? EligibleVariantIds = null);

/// <summary>One priced line, ready to become a <see cref="OrderLine"/> row.</summary>
public sealed record PricedOrderLine(
    int VariantId,
    short Quantity,
    decimal UnitOriginalPrice,
    decimal UnitListPrice,
    decimal UnitPrice,
    decimal LineSubtotal,
    decimal PromoDiscountAllocated,
    decimal LineTotal,
    int? SaleCampaignId = null);

/// <summary>
/// Every money column of <c>sales.Orders</c>, computed once. The names are the column names on purpose:
/// the thing this type exists to prevent is a figure being recomputed differently somewhere else.
/// </summary>
/// <param name="PromoDiscountClamped">
/// True when step 11's minimum-total clamp reduced the promo discount. The storefront says "discount
/// limited to the order total" when it is set; nothing else depends on it.
/// </param>
public sealed record OrderPricingResult(
    IReadOnlyList<PricedOrderLine> Lines,
    decimal ItemsOriginalTotal,
    decimal ItemsListTotal,
    decimal ItemsSubtotal,
    decimal MarkdownDiscountTotal,
    decimal CampaignDiscountTotal,
    decimal PromoDiscountAmount,
    decimal DeliveryFeeAmount,
    decimal DeliveryDiscountAmount,
    decimal TaxAmount,
    decimal GrandTotal,
    bool PromoDiscountClamped)
{
    /// <summary>
    /// What the shopper saved against the "was" prices plus the code: doc 08 §6's worked example calls
    /// this "You saved". It is <see cref="MarkdownDiscountTotal"/> + <see cref="CampaignDiscountTotal"/> +
    /// <see cref="PromoDiscountAmount"/>, which is also <see cref="ItemsOriginalTotal"/> −
    /// <see cref="ItemsSubtotal"/> + <see cref="PromoDiscountAmount"/>.
    /// </summary>
    public decimal SavedTotal => MarkdownDiscountTotal + CampaignDiscountTotal + PromoDiscountAmount;
}

/// <summary>
/// Doc 08 §6, steps 5 to 12: turn priced lines, a delivery rate and a promo outcome into the order's
/// money columns.
/// <para>
/// <b>Why this is a pure function in the Domain.</b> <c>CK_Orders_Totals</c> enforces four equalities
/// between seven columns, and <c>CK_OrderLines_LineTotal</c> a fifth per line. A constraint can only
/// reject a wrong row; it cannot make the row right, and it cannot see the cross-row invariant
/// <c>ItemsSubtotal = Σ LineSubtotal</c> at all (W3-03's review flagged exactly that). So the arithmetic
/// is written once, in one place with no I/O, and the same function serves the checkout page's quote and
/// the placement transaction. Two implementations of a total is how this project has already shipped a
/// summary where subtotal minus discount did not equal the total.
/// </para>
/// </summary>
public static class OrderPricing
{
    /// <summary>
    /// Doc 08 §6 step 11's floor (G-11): no order is ever payable for less than this, because every
    /// payment path the shop has — a bank transfer and a PayPal capture — needs a real amount to move.
    /// </summary>
    public const decimal MinimumGrandTotal = 1.00m;

    /// <summary>LKR has two decimal places, and every figure doc 08 §6 rounds, it rounds to two.</summary>
    private const int MoneyDecimals = 2;

    /// <summary>
    /// Prices the order.
    /// </summary>
    /// <param name="lines">
    /// The lines, already carrying steps 1–3's three prices. Must be non-empty: an order with no lines is
    /// not a cheaper order, it is a rejected checkout, and that is the caller's check to make.
    /// </param>
    /// <param name="delivery">
    /// The zone rate that applies. <see langword="null"/> means <b>no rate was found</b>, which is not
    /// "free": doc 08 §6 step 10 requires a row that exists and is available, so the caller must refuse
    /// the checkout rather than ask for a price. Passing null here therefore throws.
    /// </param>
    /// <param name="promo">The promo outcome, or <see langword="null"/> for no code.</param>
    public static OrderPricingResult Price(
        IReadOnlyList<OrderPricingLine> lines,
        DeliveryCharge? delivery,
        PromoCharge? promo = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line to be priced.", nameof(lines));
        }

        if (delivery is not { } rate)
        {
            // Deliberately an exception and not a zero fee. "There is no rate for this district and
            // method" is a state the shopper has to be told about and the order refused in; silently
            // charging nothing for delivery would be the shop paying for every island-wide express
            // parcel, and silently inventing a figure is worse.
            throw new ArgumentNullException(
                nameof(delivery),
                "Doc 08 §6 step 10 needs a delivery zone rate that exists and is available. "
                + "Refuse the checkout instead of pricing it without one.");
        }

        ValidateLines(lines);

        var itemsOriginalTotal = 0m;
        var itemsListTotal = 0m;
        var itemsSubtotal = 0m;
        var lineSubtotals = new decimal[lines.Count];

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            // Step 5. Not rounded: a 2 dp price times an integer quantity is already 2 dp, and rounding
            // here would be a second rounding of a figure `CK_OrderLines_LineSubtotal` compares exactly.
            var lineSubtotal = line.UnitPrice * line.Quantity;

            lineSubtotals[index] = lineSubtotal;
            itemsOriginalTotal += line.UnitOriginalPrice * line.Quantity;
            itemsListTotal += line.UnitListPrice * line.Quantity;
            itemsSubtotal += lineSubtotal;
        }

        // Step 6. Derived by subtraction rather than summed independently, because CK_Orders_Totals
        // compares exactly these differences: computing them any other way is an invitation for a
        // rounding difference to become a failed checkout.
        var markdownDiscountTotal = itemsOriginalTotal - itemsListTotal;
        var campaignDiscountTotal = itemsListTotal - itemsSubtotal;

        var requestedPromo = Math.Max(promo?.Amount ?? 0m, 0m);
        var freeDeliveryPromo = promo?.FreeDelivery ?? false;

        // A promo can never take more off than the items are worth. Step 8 says as much for a fixed-amount
        // code (`min(v, base)`) and step 9's "a share never exceeds its line subtotal" says it again for
        // the allocation; clamping here as well means the invariant holds whatever an upstream promo
        // implementation hands over.
        var promoDiscount = Math.Min(requestedPromo, itemsSubtotal);

        // Step 10, with the promo as it stands.
        var deliveryDiscount = DeliveryDiscount(rate, itemsSubtotal - promoDiscount, freeDeliveryPromo);

        // Step 11, including the minimum-total clamp (G-11).
        var grandTotal = itemsSubtotal - promoDiscount + rate.Fee - deliveryDiscount;
        var clamped = false;

        if (grandTotal < MinimumGrandTotal && promoDiscount > 0m)
        {
            var allowed = Math.Max(0m, itemsSubtotal + rate.Fee - deliveryDiscount - MinimumGrandTotal);

            if (allowed < promoDiscount)
            {
                promoDiscount = allowed;
                clamped = true;

                // Re-read step 10 against the clamped discount. Reducing the discount can only raise
                // `itemsSubtotal − promo`, so a free-delivery threshold that was already met stays met and
                // one that was not may now be: either way the figure is recomputed rather than assumed.
                deliveryDiscount = DeliveryDiscount(rate, itemsSubtotal - promoDiscount, freeDeliveryPromo);
                grandTotal = itemsSubtotal - promoDiscount + rate.Fee - deliveryDiscount;
            }
        }

        if (grandTotal < 0m)
        {
            // Unreachable with the clamp above, and an exception rather than a comment claiming so. The
            // alternative is `CK_Orders_Totals` rejecting the INSERT inside the placement transaction,
            // which the shopper would meet as a failed checkout with no explanation.
            throw new InvalidOperationException(
                $"Priced an order with a grand total of {grandTotal}, which doc 08's CK_Orders_Totals forbids.");
        }

        // Step 9, over the clamped amount.
        var priced = Allocate(lines, lineSubtotals, promoDiscount, promo?.EligibleVariantIds);

        return new OrderPricingResult(
            priced,
            itemsOriginalTotal,
            itemsListTotal,
            itemsSubtotal,
            markdownDiscountTotal,
            campaignDiscountTotal,
            promoDiscount,
            rate.Fee,
            deliveryDiscount,
            // Step 12: VAT is inclusive in the displayed price (D2), so this records nothing and is added
            // to nothing — which is why CK_Orders_Totals' GrandTotal formula does not mention it.
            TaxAmount: 0m,
            grandTotal,
            clamped);
    }

    /// <summary>
    /// Doc 08 §6 step 10's discount: the whole fee when a free-delivery code paid for it, or when the
    /// rate has a threshold and the post-promo items subtotal reaches it. Otherwise nothing.
    /// </summary>
    private static decimal DeliveryDiscount(
        DeliveryCharge rate,
        decimal itemsSubtotalAfterPromo,
        bool freeDeliveryPromo)
    {
        if (freeDeliveryPromo)
        {
            return rate.Fee;
        }

        return rate.FreeAboveAmount is { } threshold && itemsSubtotalAfterPromo >= threshold
            ? rate.Fee
            : 0m;
    }

    /// <summary>
    /// Doc 08 §6 step 9: spread the promo discount over the eligible lines pro rata by line subtotal, each
    /// share rounded to 2 dp, the remainder to the largest eligible line, and no share above its own line
    /// subtotal (so <c>LineTotal ≥ 0</c>, DB17).
    /// </summary>
    private static List<PricedOrderLine> Allocate(
        IReadOnlyList<OrderPricingLine> lines,
        decimal[] lineSubtotals,
        decimal promoDiscount,
        IReadOnlySet<int>? eligibleVariantIds)
    {
        var shares = new decimal[lines.Count];

        if (promoDiscount > 0m)
        {
            var eligibleBase = 0m;
            for (var index = 0; index < lines.Count; index++)
            {
                if (IsEligible(lines[index].VariantId, eligibleVariantIds))
                {
                    eligibleBase += lineSubtotals[index];
                }
            }

            if (eligibleBase <= 0m)
            {
                // A discount with nothing to spread it over. Upstream should not produce one (step 7's
                // eligible base is what step 8's amount is computed from), and allocating it anywhere else
                // would break `LineTotal = LineSubtotal − PromoDiscountAllocated`.
                throw new InvalidOperationException(
                    "A promo discount was given with no eligible line to allocate it to.");
            }

            var allocated = 0m;
            var largestIndex = -1;
            var largestSubtotal = -1m;

            for (var index = 0; index < lines.Count; index++)
            {
                if (!IsEligible(lines[index].VariantId, eligibleVariantIds))
                {
                    continue;
                }

                var subtotal = lineSubtotals[index];
                var share = Round(promoDiscount * subtotal / eligibleBase);

                // The cap is per line and applied before the remainder, so the remainder never lands on a
                // line that is already fully discounted.
                share = Math.Min(share, subtotal);

                shares[index] = share;
                allocated += share;

                // Strictly greater, so the first of two equal lines wins and the allocation is
                // deterministic for a given line order.
                if (subtotal > largestSubtotal)
                {
                    largestSubtotal = subtotal;
                    largestIndex = index;
                }
            }

            var remainder = promoDiscount - allocated;
            if (remainder != 0m && largestIndex >= 0)
            {
                // Still capped: a remainder that cannot be placed is left unallocated rather than pushed
                // on to a line as a negative line total. The grand total is computed from
                // `promoDiscount`, and the per-line caps can only bind when the clamp has already brought
                // the discount down to the subtotal, so the two cannot disagree in practice — and if they
                // ever did, the assertion below is what would say so.
                var target = Math.Min(shares[largestIndex] + remainder, lineSubtotals[largestIndex]);
                shares[largestIndex] = Math.Max(target, 0m);
            }
        }

        var priced = new List<PricedOrderLine>(lines.Count);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            priced.Add(new PricedOrderLine(
                line.VariantId,
                line.Quantity,
                line.UnitOriginalPrice,
                line.UnitListPrice,
                line.UnitPrice,
                lineSubtotals[index],
                shares[index],
                lineSubtotals[index] - shares[index],

                // Carried through untouched. The campaign id is a record of which campaign set
                // `UnitPrice`, not an input to any total, and the placement writes it to
                // `sales.OrderLines.SaleCampaignId` (ADM-14).
                line.SaleCampaignId));
        }

        return priced;
    }

    private static bool IsEligible(int variantId, IReadOnlySet<int>? eligibleVariantIds) =>
        eligibleVariantIds is null || eligibleVariantIds.Contains(variantId);

    private static void ValidateLines(IReadOnlyList<OrderPricingLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
            {
                throw new ArgumentException(
                    $"Variant {line.VariantId} has quantity {line.Quantity}; CK_OrderLines_Quantity needs more than 0.",
                    nameof(lines));
            }

            // CK_OrderLines on UnitOriginalPrice ≥ UnitListPrice ≥ UnitPrice > 0, checked here so the
            // failure names the variant instead of arriving as a constraint violation mid-transaction.
            if (line.UnitPrice < CatalogPricing.MinimumSalePrice
                || line.UnitListPrice < line.UnitPrice
                || line.UnitOriginalPrice < line.UnitListPrice)
            {
                throw new ArgumentException(
                    $"Variant {line.VariantId} is priced {line.UnitOriginalPrice}/{line.UnitListPrice}/{line.UnitPrice}; "
                    + "doc 08 §3.5 needs original ≥ list ≥ sale ≥ 1.00.",
                    nameof(lines));
            }
        }
    }

    /// <summary>Doc 08 §6: "rounded half away from zero to 2 dp where noted".</summary>
    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyDecimals, MidpointRounding.AwayFromZero);
}
