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
    /// <b>One reason for five states, deliberately.</b> Telling a shopper "that code has expired" where
    /// "we do not recognise that code" would otherwise have been the answer turns the box into an oracle
    /// for which strings are codes the shop has ever issued. All five are answered by the same action —
    /// check the code, or ask for a current one — so nothing actionable is withheld. The reasons below are
    /// each distinguished because each one tells the shopper something they can act on.
    /// </para>
    /// </summary>
    NotRecognised = 1,

    /// <summary>
    /// Every use is taken (doc 08 §3.6's total limit). Doc 11 P3's own exception list asks for this
    /// sentence in particular: "the other is told the code is fully used and can place the order without
    /// it", and a shopper who was not told would simply retype it.
    /// </summary>
    FullyUsed = 2,

    /// <summary>
    /// The items subtotal is under <c>MinSpendAmount</c>. Actionable — the shopper can add more — so the
    /// threshold itself travels on <see cref="PromoEvaluation.MinimumSpend"/> and is printed.
    /// </summary>
    MinimumSpend = 3,

    /// <summary>
    /// Nothing in the bag the code may discount. Reachable only through <c>ExcludeSaleItems</c>, which
    /// needed sale campaigns to exist — <b>and since W3-05b (ADM-14) they do</b>, so this is a reason a
    /// shopper can now actually be given: every line in the bag is campaign-priced and the code excludes
    /// sale items. See <see cref="PromoEvaluator"/>.
    /// </summary>
    NoEligibleItems = 4,

    /// <summary>
    /// This email has already used the code as many times as <c>PerCustomerLimit</c> allows (doc 11
    /// line 468). <b>Decided at placement only</b> and never by <see cref="PromoEvaluator"/>: the limit
    /// counts orders by normalised email under an app lock, which is I/O, and doc 11 P3 step 10 puts it at
    /// placement because that is when the email is known. Answering it on a quote whose email is a
    /// free-text box would also let anyone test whether a given address had used a given code.
    /// </summary>
    AlreadyUsedByCustomer = 5,
}

/// <summary>
/// Everything about one <c>marketing.PromoCodes</c> row that doc 08 §6 steps 7 and 8 read.
/// </summary>
/// <remarks>
/// <b>A snapshot and not the entity, for two reasons.</b> The reader projects straight to this, so a
/// lookup selects thirteen columns rather than the row plus its audit, soft-delete and rowversion
/// columns. And <see cref="PromoCode.IsDeleted"/> has a private setter — doc 08 §10.2 DB5 allows it to
/// move only through <see cref="PromoCode.SoftDelete"/> — so a unit test could not construct a deleted
/// entity to check that the evaluator refused it. There is nothing to check: <b><c>IsDeleted</c> is
/// deliberately not on this type</b>, because a soft-deleted code must never reach the evaluator at all.
/// Two things keep it away — the model's global soft-delete filter (<c>ApplySoftDelete</c>, doc 08 §1.5)
/// and the reader's own predicate — and <c>UX_PromoCodes_Code</c> is filtered the same way, which is why
/// "the row for this code" is only well defined among undeleted rows. An integration test asserts that a
/// removed code is <i>not found</i> rather than found-and-refused.
/// </remarks>
public readonly record struct PromoCodeTerms(
    int Id,
    string Code,
    PromoDiscountType DiscountType,
    decimal? DiscountValue,
    decimal? MaxDiscountAmount,
    decimal? MinSpendAmount,
    bool ExcludeSaleItems,
    DateTime StartsAtUtc,
    DateTime? EndsAtUtc,
    int? TotalUsageLimit,
    int? PerCustomerLimit,
    int UsageCount,
    bool IsActive);

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
/// rule — none of it needs a database, and all of it has to give the same answer on the checkout quote and
/// inside the placement transaction. The two checks that are <i>not</i> here are the ones that cannot be:
/// the per-customer limit counts rows in <c>sales.Orders</c> under an app lock, and the total limit is
/// re-applied by a conditional UPDATE at placement because the count this type reads can be stale by the
/// time the order is written.
/// </para>
/// </summary>
public static class PromoEvaluator
{
    /// <summary>LKR has two decimal places, and doc 08 §6 rounds "half away from zero to 2 dp".</summary>
    private const int MoneyDecimals = 2;

    /// <summary>
    /// Evaluates <paramref name="terms"/> against the lines that are being priced.
    /// </summary>
    /// <param name="terms">
    /// The row as it stands, already filtered to a row that is not soft-deleted — see
    /// <see cref="PromoCodeTerms"/>.
    /// </param>
    /// <param name="lines">
    /// Exactly the lines <see cref="OrderPricing.Price"/> is about to be given. Passing a different set
    /// would make the eligible base and the allocation disagree, which is the one way step 9 can throw.
    /// </param>
    /// <param name="utcNow">
    /// The instant to judge the window against. <b>UTC, and that is not the Colombo defect:</b> the stored
    /// bounds are UTC too (<c>StartsAtUtc</c>, <c>EndsAtUtc</c>), so the comparison is offset-free. What
    /// has to know about Asia/Colombo is the admin screen that turns "valid until 12 October, 23:59" into
    /// an instant — see <see cref="PromoCodeSchedule"/>.
    /// </param>
    public static PromoEvaluation Evaluate(
        PromoCodeTerms terms,
        IReadOnlyList<OrderPricingLine> lines,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Step 7, in doc 11 P3 step 10's order: active, then the window, then the total limit, then the
        // minimum spend, then at least one eligible line. The order is what decides which sentence a
        // shopper sees when two things are wrong at once.
        if (!terms.IsActive || !IsWithinWindow(terms, utcNow))
        {
            return PromoEvaluation.Refused(PromoRefusal.NotRecognised);
        }

        if (terms.TotalUsageLimit is { } totalLimit && terms.UsageCount >= totalLimit)
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

            if (IsEligible(terms, line))
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
        // subtotal after sale prices**, not with the eligible base and not with the grand total. So a bag
        // that clears the threshold only because of its excluded lines still clears it.
        if (terms.MinSpendAmount is { } minimumSpend && itemsSubtotal < minimumSpend)
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
        // depend on a hash lookup instead of on nothing at all.
        var eligible = everyLineEligible ? null : eligibleVariantIds;

        // Step 8.
        return terms.DiscountType switch
        {
            PromoDiscountType.FreeDelivery =>
                // "FreeDelivery → 0 here": the whole effect is step 10's, and the eligible set still
                // travels so that no later code type inherits a silent "all lines".
                PromoEvaluation.Applies(new PromoCharge(0m, FreeDelivery: true, eligible)),

            PromoDiscountType.Percent =>
                PromoEvaluation.Applies(new PromoCharge(
                    PercentDiscount(terms, eligibleBase),
                    FreeDelivery: false,
                    eligible)),

            PromoDiscountType.FixedAmount =>
                // "Fixed → min(v, base)". The cap is what makes a Rs. 1,000 code on Rs. 800 of eligible
                // items worth Rs. 800.00 — doc 11 P3's own worked edge case — rather than a negative line.
                PromoEvaluation.Applies(new PromoCharge(
                    Math.Min(terms.DiscountValue ?? 0m, eligibleBase),
                    FreeDelivery: false,
                    eligible)),

            // A tinyint outside CK_PromoCodes_DiscountType, which the constraint makes unstorable. Loud
            // rather than silently worth nothing: a code that discounts nothing while the screen says it
            // applied is the defect, not the exception.
            _ => throw new InvalidOperationException(
                $"Promo code {terms.Id} has discount type {(byte)terms.DiscountType}, which "
                + "CK_PromoCodes_DiscountType does not allow."),
        };
    }

    /// <summary>
    /// Doc 08 §6 step 8: <c>round(base × v/100)</c>, capped by <c>MaxDiscountAmount</c>.
    /// </summary>
    /// <remarks>
    /// The cap is applied <b>after</b> the rounding, so a cap of Rs. 2,000.00 is exactly Rs. 2,000.00 and
    /// not Rs. 2,000.004 rounded to it. It is also clamped to the eligible base, which a percentage of
    /// 100 — the most <c>CK_PromoCodes_DiscountValue</c> allows — can reach exactly.
    /// </remarks>
    private static decimal PercentDiscount(PromoCodeTerms terms, decimal eligibleBase)
    {
        var discount = Round(eligibleBase * (terms.DiscountValue ?? 0m) / 100m);

        if (terms.MaxDiscountAmount is { } cap)
        {
            discount = Math.Min(discount, cap);
        }

        return Math.Min(discount, eligibleBase);
    }

    /// <summary>
    /// Doc 08 §3.6's window: the start is inclusive, the end exclusive — the same rule
    /// <c>ContentSchedule.IsLive</c> applies to every other scheduled row in this database, so "ends at
    /// 23:59" means the same thing on an announcement and on a promo code.
    /// </summary>
    private static bool IsWithinWindow(PromoCodeTerms terms, DateTime utcNow) =>
        terms.StartsAtUtc <= utcNow && (terms.EndsAtUtc is null || terms.EndsAtUtc.Value > utcNow);

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
    /// <b>W3-05b has made this reachable, and the paragraph that used to stand here is now wrong.</b> It
    /// said the flag excluded nothing in this build, because <c>CheckoutReader</c> computed every sale
    /// price as <c>CatalogPricing.SalePrice(list, campaignPrice: null)</c> and no line could satisfy
    /// <c>UnitPrice &lt; UnitListPrice</c>. ADM-14 exists now: <c>CheckoutReader</c> resolves a live
    /// campaign per line, so a campaign-priced line really does arrive here and really is excluded.
    /// <c>CheckoutCampaignBehaviourTests.Exclude_sale_items_drops_the_campaign_line_and_keeps_the_</c>
    /// <c>markdown_line</c> is the first test in the repository that could exercise it: the jeans are
    /// campaign-priced and dropped from the eligible base, the shirt carries only a markdown and stays in.
    /// </para>
    /// <para>
    /// <b>The checkbox is still off the admin promo-code form, and that is now a gap rather than a
    /// decision.</b> It was left off because it could not change any total; it can now, so a staff member
    /// cannot set a rule the evaluator honours. It is in W3-05b's task report, and the control is one
    /// field plus one validator line.
    /// </para>
    /// </remarks>
    private static bool IsEligible(PromoCodeTerms terms, OrderPricingLine line) =>
        !terms.ExcludeSaleItems || line.UnitPrice >= line.UnitListPrice;

    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyDecimals, MidpointRounding.AwayFromZero);
}
