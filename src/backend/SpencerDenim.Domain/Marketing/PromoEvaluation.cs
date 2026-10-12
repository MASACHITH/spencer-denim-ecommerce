using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Domain.Marketing;

/// <summary>
/// Why a code was refused, or <see cref="None"/>. A reason travels and the wording is the storefront's —
/// the contract <c>CartLineStatus</c> and <c>CheckoutBlocker</c> already use, and for the same reason: a
/// test can assert on a code and not on a sentence.
/// </summary>
public enum PromoRefusal : byte
{
    /// <summary>The code applies.</summary>
    None = 0,

    /// <summary>
    /// No such code, or it is inactive, soft-deleted, not started yet or over.
    /// <para>
    /// <b>One reason for five states, deliberately.</b> Telling a shopper "that code has expired" when
    /// "we do not recognise that code" would otherwise have been the answer turns the box into an oracle
    /// for which strings are codes the shop has ever issued. The four causes are all answered by the same
    /// action — check the code, or ask for a current one — so nothing actionable is withheld. The reasons
    /// below are each distinguished because each one tells the shopper something they can act on.
    /// </para>
    /// </summary>
    NotRecognised = 1,

    /// <summary>
    /// Every use is taken (doc 08 §3.6's total limit). Doc 11 P3's own exception list asks for this
    /// sentence in particular: "the other is told the code is fully used and can place the order without
    /// it", and a shopper who is not told would simply retype it.
    /// </summary>
    FullyUsed = 2,

    /// <summary>
    /// The items subtotal is under <c>MinSpendAmount</c>. Actionable — the shopper can add more — so the
    /// threshold itself is carried on <see cref="PromoEvaluation.MinimumSpend"/> and printed.
    /// </summary>
    MinimumSpend = 3,

    /// <summary>
    /// Nothing in the bag the code may discount. Reachable only through <c>ExcludeSaleItems</c>, so it
    /// cannot happen until sale campaigns exist (ADM-14) — see <see cref="PromoEvaluator"/>.
    /// </summary>
    NoEligibleItems = 4,

    /// <summary>
    /// This email has already used the code as many times as <c>PerCustomerLimit</c> allows (doc 11
    /// line 468). <b>Decided at placement only</b>, never on a quote: the limit counts orders by
    /// normalised email, and answering it on a GET whose email is a free-text box would let anyone test
    /// whether a given address had used a given code. Doc 11 P3 step 10 puts it at placement for the
    /// plainer reason that that is when the email is known.
    /// </summary>
    AlreadyUsedByCustomer = 5,
}

/// <summary>
/// The answer to doc 08 §6 steps 7 and 8: either a <see cref="PromoCharge"/> the calculator can take, or
/// a reason.
/// </summary>
/// <param name="Refusal"><see cref="PromoRefusal.None"/> when <paramref name="Charge"/> is set.</param>
/// <param name="Charge">
/// Steps 7 and 8's outcome, in the exact shape <c>OrderPricing.Price</c> already accepts. Non-null if and
/// only if <paramref name="Refusal"/> is <see cref="PromoRefusal.None"/>.
/// </param>
/// <param name="MinimumSpend">
/// The threshold, for <see cref="PromoRefusal.MinimumSpend"/> only. Null otherwise.
/// </param>
public sealed record PromoEvaluation(
    PromoRefusal Refusal,
    PromoCharge? Charge,
    decimal? MinimumSpend = null)
{
    public bool IsUsable => Refusal == PromoRefusal.None;

    public static PromoEvaluation Refused(PromoRefusal refusal) => new(refusal, null);

    public static PromoEvaluation NeedsMinimumSpend(decimal minimumSpend) =>
        new(PromoRefusal.MinimumSpend, null, minimumSpend);

    public static PromoEvaluation Applies(PromoCharge charge) => new(PromoRefusal.None, charge);
}

/// <summary>
/// Doc 08 §6 <b>steps 7 and 8</b>: is this code usable on this bag, and if so what is it worth.
/// <para>
/// <b>Steps 9, 10 and 11 are not here and must not be.</b> The allocation, the delivery discount and the
/// Rs. 1.00 minimum-total clamp are already written, once, in <see cref="OrderPricing"/>, whose own
/// remarks on <see cref="PromoCharge"/> say this function's output is the one argument they were waiting
/// for. This file computes an amount; it never decides a total.
/// </para>
/// <para>
/// <b>Pure, and in the Domain, for the same reason <see cref="OrderPricing"/> is.</b> Validity is a
/// comparison against a clock, the eligible base is a sum over lines, and the percentage is a rounding
/// rule — none of it needs a database, and all of it has to give the same answer on the bag page, on the
/// checkout quote and inside the placement transaction. The <i>per-customer</i> limit is the one check
/// that is not here, because it counts rows in <c>sales.Orders</c> under a lock; see
/// <see cref="PromoRefusal.AlreadyUsedByCustomer"/>.
/// </para>
/// </summary>
public static class PromoEvaluator
{
    /// <summary>LKR has two decimal places, and doc 08 §6 rounds "half away from zero to 2 dp".</summary>
    private const int MoneyDecimals = 2;

    /// <summary>
    /// Evaluates <paramref name="code"/> against the lines that are being priced.
    /// </summary>
    /// <param name="code">The row as it stands. A soft-deleted row is refused here as well as filtered
    /// out by the reader, so a caller that forgets the filter cannot discount anything.</param>
    /// <param name="lines">
    /// Exactly the lines <see cref="OrderPricing.Price"/> is about to be given. Passing a different set
    /// would make the eligible base and the allocation disagree, which is the one way step 9 can throw.
    /// </param>
    /// <param name="utcNow">
    /// The instant to judge the window against. <b>UTC, and that is not a timezone bug:</b> the stored
    /// bounds are UTC too (<c>StartsAtUtc</c>, <c>EndsAtUtc</c>), so the comparison is offset-free. What
    /// has to know about Asia/Colombo is the admin screen that turns "valid until 12 October, 23:59" into
    /// an instant — see <c>PromoCodeSchedule</c>.
    /// </param>
    public static PromoEvaluation Evaluate(
        PromoCode code,
        IReadOnlyList<OrderPricingLine> lines,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(lines);

        // Step 7, in doc 11 P3 step 10's order: exists and is active, then the window, then the total
        // limit, then the minimum spend, then at least one eligible line. The order is what decides which
        // sentence a shopper sees when two things are wrong at once.
        if (code.IsDeleted || !code.IsActive || !IsWithinWindow(code, utcNow))
        {
            return PromoEvaluation.Refused(PromoRefusal.NotRecognised);
        }

        if (code.TotalUsageLimit is { } totalLimit && code.UsageCount >= totalLimit)
        {
            return PromoEvaluation.Refused(PromoRefusal.FullyUsed);
        }

        var itemsSubtotal = 0m;
        var eligibleBase = 0m;
        var eligibleVariantIds = new HashSet<int>();
        var everyLineEligible = true;

        foreach (var line in lines)
        {
            var lineSubtotal = line.UnitPrice * line.Quantity;
            itemsSubtotal += lineSubtotal;

            if (IsEligible(code, line))
            {
                eligibleBase += lineSubtotal;
                eligibleVariantIds.Add(line.VariantId);
            }
            else
            {
                everyLineEligible = false;
            }
        }

        // Doc 08 §6 step 7 and doc 11 P3's rules list: the minimum spend is compared with the **items
        // subtotal after sale prices**, not with the eligible base and not with the grand total. So a
        // bag that clears the threshold only because of its excluded lines still clears it.
        if (code.MinSpendAmount is { } minimumSpend && itemsSubtotal < minimumSpend)
        {
            return PromoEvaluation.NeedsMinimumSpend(minimumSpend);
        }

        if (eligibleBase <= 0m)
        {
            // Doc 11 P3 step 10's last check. Refused rather than applied-as-zero, because a discount row
            // reading "− Rs. 0.00" tells a shopper their code worked when it did nothing.
            return PromoEvaluation.Refused(PromoRefusal.NoEligibleItems);
        }

        // Null rather than the set when nothing is excluded: `PromoCharge.EligibleVariantIds` documents
        // null as "all of them", and handing step 9 a set it does not need would make a line's eligibility
        // depend on a HashSet lookup instead of on nothing at all.
        var eligible = everyLineEligible ? null : eligibleVariantIds;

        // Step 8.
        return code.DiscountType switch
        {
            PromoDiscountType.FreeDelivery =>
                // "FreeDelivery → 0 here": the whole effect is step 10's, and the eligible set still
                // travels so that a later code type cannot inherit a silent "all lines".
                PromoEvaluation.Applies(new PromoCharge(0m, FreeDelivery: true, eligible)),

            PromoDiscountType.Percent =>
                PromoEvaluation.Applies(new PromoCharge(
                    PercentDiscount(code, eligibleBase),
                    FreeDelivery: false,
                    eligible)),

            PromoDiscountType.FixedAmount =>
                // "Fixed → min(v, base)". The cap is what makes a Rs. 1,000 code on Rs. 800 of eligible
                // items worth Rs. 800.00 (doc 11 P3's own worked edge case) rather than a negative line.
                PromoEvaluation.Applies(new PromoCharge(
                    Math.Min(code.DiscountValue ?? 0m, eligibleBase),
                    FreeDelivery: false,
                    eligible)),

            // A tinyint outside CK_PromoCodes_DiscountType, which the constraint makes unstorable. Loud
            // rather than silently worth nothing: a code that discounts nothing while the screen says it
            // applied is the defect, not the exception.
            _ => throw new InvalidOperationException(
                $"Promo code {code.Id} has discount type {(byte)code.DiscountType}, which "
                + "CK_PromoCodes_DiscountType does not allow."),
        };
    }

    /// <summary>
    /// Doc 08 §6 step 8: <c>round(base × v/100)</c>, capped by <c>MaxDiscountAmount</c>.
    /// </summary>
    /// <remarks>
    /// The cap is applied <b>after</b> the rounding, so a cap of Rs. 2,000.00 is exactly Rs. 2,000.00 and
    /// not Rs. 2,000.004 rounded to it. It is also clamped to the eligible base, which
    /// <c>CK_PromoCodes_DiscountValue</c> allows a percentage of 100 to reach exactly.
    /// </remarks>
    private static decimal PercentDiscount(PromoCode code, decimal eligibleBase)
    {
        var discount = Round(eligibleBase * (code.DiscountValue ?? 0m) / 100m);

        if (code.MaxDiscountAmount is { } cap)
        {
            discount = Math.Min(discount, cap);
        }

        return Math.Min(discount, eligibleBase);
    }

    /// <summary>
    /// Doc 08 §3.6's window: the start is inclusive, the end exclusive — the same rule
    /// <c>ContentSchedule.IsLive</c> applies to every other scheduled row in this schema, so "ends at
    /// 23:59" means the same thing on an announcement and on a promo code.
    /// </summary>
    private static bool IsWithinWindow(PromoCode code, DateTime utcNow) =>
        code.StartsAtUtc <= utcNow && (code.EndsAtUtc is null || code.EndsAtUtc.Value > utcNow);

    /// <summary>
    /// Doc 08 §6 step 7's <c>ExcludeSaleItems</c>: a line <b>a campaign priced</b> is out of the eligible
    /// base; a line that only carries a markdown stays in (doc 11 §7 G-07).
    /// </summary>
    /// <remarks>
    /// <b>A campaign-priced line is one whose sale price is below its list price</b>, which is exactly what
    /// doc 08 §6 steps 1–3 leave behind: step 2's markdown moves <c>UnitOriginalPrice</c> above
    /// <c>UnitListPrice</c>, and only step 3's campaign moves <c>UnitPrice</c> below it. So the test needs
    /// no campaign id and no join.
    /// <para>
    /// <b>It therefore excludes nothing in this build, and that is a fact about the repository rather than
    /// a claim about the rule.</b> Checked rather than assumed: <c>CheckoutReader</c>'s projection sets
    /// <c>UnitPrice</c> from the same <c>PriceOverride ?? BasePrice</c> expression as
    /// <c>UnitListPrice</c> — grep <c>UnitListPrice</c> in
    /// <c>src/backend/SpencerDenim.Infrastructure/Sales/CheckoutReader.cs</c> — because
    /// <c>marketing.SaleCampaigns</c> does not exist (ADM-14, and deliberately not in W3-05's scope). No
    /// line can satisfy <c>UnitPrice &lt; UnitListPrice</c> until it does, so the flag is carried, honoured
    /// and unit-tested here against a synthesised campaign-priced line, and left off the admin form, where
    /// a checkbox that could not do anything would be a promise.
    /// </para>
    /// </remarks>
    private static bool IsEligible(PromoCode code, OrderPricingLine line) =>
        !code.ExcludeSaleItems || line.UnitPrice >= line.UnitListPrice;

    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyDecimals, MidpointRounding.AwayFromZero);
}
