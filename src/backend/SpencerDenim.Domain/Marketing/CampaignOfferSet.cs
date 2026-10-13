namespace SpencerDenim.Domain.Marketing;

/// <summary>
/// Which live campaigns reach which products, for one read: the answer to doc 08 §6 step 3's "each active
/// campaign targeting the product", looked up by product id.
/// </summary>
/// <remarks>
/// <b>Per product and not per variant, although the winner is decided per variant.</b> Targeting is a
/// property of the product — a department, a category subtree or the product itself (doc 08 §3.6) — while
/// the resulting price depends on the variant's list price (doc 08 §6 step 1). So the <i>set</i> is
/// fetched once per product and <see cref="Best"/> is asked once per line, which is also why the winner
/// must never be cached per product; <c>CampaignPricingTests</c> pins that with two variants of one
/// product resolving to different campaigns.
/// <para>
/// <b>It is a snapshot, deliberately.</b> One bag read evaluates every line against one instant, so two
/// lines cannot straddle a campaign boundary and produce a subtotal that is the sum of two different
/// catalogues.
/// </para>
/// </remarks>
public sealed class CampaignOfferSet
{
    /// <summary>No campaign targets anything — the ordinary state of a shop that is not running a sale.</summary>
    public static CampaignOfferSet None { get; } = new(new Dictionary<int, IReadOnlyList<CampaignOffer>>());

    private readonly IReadOnlyDictionary<int, IReadOnlyList<CampaignOffer>> _byProductId;

    public CampaignOfferSet(IReadOnlyDictionary<int, IReadOnlyList<CampaignOffer>> byProductId)
    {
        ArgumentNullException.ThrowIfNull(byProductId);

        _byProductId = byProductId;
    }

    /// <summary>The live campaigns targeting <paramref name="productId"/>, or an empty list.</summary>
    public IReadOnlyList<CampaignOffer> For(int productId) =>
        _byProductId.TryGetValue(productId, out var offers) ? offers : [];

    /// <summary>
    /// Doc 08 §6 step 3 for one line: the lowest resulting price among the campaigns that reach this
    /// product, or <see langword="null"/> when none of them beats <paramref name="listPrice"/>.
    /// </summary>
    public CampaignPrice? Best(int productId, decimal listPrice) =>
        CampaignPricing.Best(For(productId), listPrice);
}
