using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Promotions.Abstractions;

/// <summary>
/// The live side of doc 08 §6 step 3: which campaigns are pricing these products <b>right now</b>.
/// </summary>
/// <remarks>
/// <b>This is the read the bag and the checkout use, and doc 08 §7.4 is explicit that they must.</b> The
/// stored price summary on <c>catalog.ProductStats</c> may lag by up to five minutes at a campaign
/// boundary, because the <c>RefreshCampaignPricing</c> job writes it every five minutes — that is a
/// documented performance decision for listings and the Sale page, not a defect. "Checkout always prices
/// live" is the other half of the same decision, and it is this interface. A bag or checkout read that
/// went to <c>ProductStats</c> instead would charge last-known prices for up to five minutes after a
/// campaign ended, which is money.
/// <para>
/// <b>It takes product ids and not variants.</b> Targeting is a property of the product (doc 08 §3.6);
/// the list price, and therefore the winner, is a property of the variant (doc 08 §6 step 1). One
/// round-trip per read answers the first and <see cref="CampaignOfferSet.Best"/> answers the second per
/// line.
/// </para>
/// </remarks>
public interface ICampaignOfferReader
{
    /// <summary>
    /// The live campaigns targeting each of <paramref name="productIds"/>, as of one instant captured
    /// inside the call.
    /// </summary>
    /// <param name="productIds">
    /// The products being priced. An empty collection answers <see cref="CampaignOfferSet.None"/> without
    /// a round-trip, which is what an empty bag costs.
    /// </param>
    Task<CampaignOfferSet> ReadAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken);
}
