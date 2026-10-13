namespace SpencerDenim.Application.Marketing.Promotions.Abstractions;

/// <summary>
/// What one pass of the recompute did, so the caller can log a fact rather than an intention.
/// </summary>
/// <param name="Products">How many products were in the affected set.</param>
/// <param name="Batches">How many <c>UPDATE … FROM</c> statements it took (doc 08 §7.4: ≤ 1,000 each).</param>
/// <param name="RowsWritten">
/// How many <c>catalog.ProductStats</c> rows the statements touched. It can be <b>lower</b> than
/// <paramref name="Products"/>: a product with no stats row yet is not created by this path, because the
/// row is written in the same transaction as the product itself (doc 08 §7.4) and inventing one here
/// would be a pricing row for a product that may never be published.
/// </param>
public readonly record struct CampaignPricingRefreshPass(int Products, int Batches, int RowsWritten);

/// <summary>
/// Doc 08 §7.4's stored price summary: recompute <c>catalog.ProductStats</c>' pricing columns for the
/// products a campaign affects, in batches of ≤ 1,000, from the source tables.
/// </summary>
/// <remarks>
/// <b>This is the lagging side of the same decision <see cref="ICampaignOfferReader"/> is the live side
/// of.</b> Doc 08 §7.4 gives these columns a staleness of "≤ 5 min at campaign boundaries" and says in
/// the same row that "checkout always prices live". Listings, the Sale page (DEPT-03) and the Sale
/// homepage block (HOME-07) read the summary and may therefore be up to five minutes behind at a
/// boundary; the bag, the checkout quote and the placement never read it. <b>That lag is a documented
/// performance decision, not a defect to be fixed by making listings live.</b>
/// <para>
/// <b>Called on save and on a timer, and it must be safe to call twice.</b> Doc 11 P3 step 3 triggers it
/// on a campaign create, edit or delete, and the <c>RefreshCampaignPricing</c> job runs it every five
/// minutes for the boundaries that have passed. Every statement computes the whole answer from the source
/// tables rather than adjusting what is there, so the operation is idempotent: a pass that fails halfway
/// leaves the batches it completed correct and the rest stale, and the next pass — or the save that
/// follows — converges without any repair step. Two instances running at once write the same values.
/// </para>
/// </remarks>
public interface ICampaignPricingRefresher
{
    /// <summary>
    /// Recomputes the summary for exactly these products.
    /// </summary>
    /// <param name="productIds">
    /// The affected set. <b>On an edit the caller passes the union of the products the campaign targeted
    /// before and after</b>, because a product dropped from a campaign needs its summary put back just as
    /// much as one added to it needs it taken down, and after the save there is no way to find it.
    /// </param>
    Task<CampaignPricingRefreshPass> RefreshProductsAsync(
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The products these campaigns target right now — <b>whatever their window or active flag</b> — so a
    /// caller can take the affected set before it changes something.
    /// </summary>
    /// <remarks>
    /// Deliberately not filtered by <see cref="Domain.Marketing.CampaignWindow"/>: a campaign that starts
    /// tomorrow still has to have its products' summaries recomputed when it is edited today, because the
    /// recompute writes what is true now and a scheduled campaign's "true now" is no discount. Filtering
    /// here would mean a campaign brought forward never reached the products it had been going to reach.
    /// </remarks>
    Task<IReadOnlyList<int>> ProductsTargetedByAsync(
        IReadOnlyCollection<int> campaignIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// One <c>RefreshCampaignPricing</c> pass: recompute the products of every campaign whose start or
    /// end falls in <c>(sinceUtc, untilUtc]</c> (doc 08 §7.4: "for campaigns that started or ended since
    /// the last run").
    /// </summary>
    Task<CampaignPricingRefreshPass> RefreshBoundariesAsync(
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken);
}
