using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// What one read made of the code a shopper typed.
/// </summary>
/// <param name="AppliedCode">
/// The <b>stored</b> form of the code when it applied, and <see langword="null"/> otherwise. It is the
/// database's spelling and not the shopper's: the collation is <c>Latin1_General_100_CI_AS_SC</c> (doc 08
/// §1.1), so <c>welcome10</c> finds <c>WELCOME10</c>, and the order must record the code the shop issued.
/// </param>
/// <param name="Terms">The row, for the placement transaction's limits. Null when nothing applied.</param>
/// <param name="Charge">Steps 7 and 8's outcome, ready for <c>OrderPricing</c>. Null when nothing applied.</param>
/// <param name="Refusal">The reason, when a code was sent and refused. Null when none was sent, or it applied.</param>
/// <param name="MinimumSpend">The threshold, only with <see cref="PromoRefusal.MinimumSpend"/>.</param>
internal readonly record struct ResolvedPromo(
    string? AppliedCode,
    PromoCodeTerms? Terms,
    PromoCharge? Charge,
    PromoRefusal? Refusal,
    decimal? MinimumSpend)
{
    /// <summary>No code was asked for. Not the same as a code that was refused.</summary>
    public static ResolvedPromo None => default;

    /// <summary>True when a code applied, which is exactly when <see cref="Terms"/> is set.</summary>
    public bool Applied => Terms is not null;

    public static ResolvedPromo Refused(PromoRefusal refusal, decimal? minimumSpend = null) =>
        new(null, null, null, refusal, minimumSpend);
}

/// <summary>
/// The one path from "a shopper typed a string" to "doc 08 §6 step 8 says it is worth this much" (ADM-13).
/// </summary>
/// <remarks>
/// <b>One copy, because the quote and the placement must reach the same verdict.</b>
/// <c>GetCheckoutQueryHandler</c> calls it to price the page and <c>PlaceOrderCommandHandler</c> calls it
/// again at placement — doc 11 P3 step 12 asks for exactly that ("re-validates the code on every bag read
/// <i>and again at placement</i>") — and if the two resolved it differently the G-10 total check would
/// refuse the order with no way for the shopper to tell why.
/// <para>
/// <b>What it does not do: the per-customer limit.</b> That counts rows in <c>sales.Orders</c> under an
/// app lock inside the placement transaction (doc 08 §3.6, doc 11 line 468), and doc 11 P3 step 10 puts it
/// at placement because that is when the email is known. See <c>IOrderPlacementStore</c>.
/// </para>
/// </remarks>
internal static class PromoResolver
{
    /// <summary>
    /// Resolves <paramref name="rawCode"/> against the bag.
    /// </summary>
    /// <param name="rawCode">Whatever arrived on the request. Null, blank or whitespace is "no code".</param>
    /// <param name="lines">The whole bag; the priced subset is taken here, with <c>CheckoutQuote</c>'s rule.</param>
    /// <param name="reader">The lookup.</param>
    /// <param name="utcNow">The instant the window is judged against.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<ResolvedPromo> ResolveAsync(
        string? rawCode,
        IReadOnlyList<CheckoutCartLine> lines,
        ICheckoutReader reader,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(reader);

        var normalised = PromoCodeText.Normalise(rawCode);

        if (normalised is null)
        {
            return ResolvedPromo.None;
        }

        // Refused without a round trip, because a string that is not a well-formed code cannot match a
        // stored one: `CK`-free as it is, every row went in through the admin validator. It also means a
        // caller cannot use this endpoint to run arbitrary 40-character strings past the database.
        if (!PromoCodeText.IsWellFormed(normalised))
        {
            return ResolvedPromo.Refused(PromoRefusal.NotRecognised);
        }

        var priced = lines.Where(CheckoutQuote.IsPriced).ToList();

        if (priced.Count == 0)
        {
            // Nothing to discount, and nothing worth saying about the code: the page is already telling
            // the shopper their bag is empty (`CheckoutBlocker.EmptyBag`) or that every line has gone, and
            // a second sentence about a code on top of that is noise. Deliberately `None` rather than a
            // refusal, so the box clears quietly instead of blaming the code.
            return ResolvedPromo.None;
        }

        var terms = await reader.FindPromoCodeAsync(normalised, cancellationToken);
        var evaluation = CheckoutQuote.EvaluatePromo(priced, terms, utcNow);

        return evaluation is { IsUsable: true, Charge: { } charge }
            ? new ResolvedPromo(terms!.Value.Code, terms, charge, null, null)
            : ResolvedPromo.Refused(evaluation.Refusal, evaluation.MinimumSpend);
    }
}
