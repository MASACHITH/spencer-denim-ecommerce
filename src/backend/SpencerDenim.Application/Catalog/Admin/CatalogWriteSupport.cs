using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// The two decisions every ADM-11 write makes the same way: is the caller's row version current, and what
/// did the database make of the save.
/// </summary>
internal static class CatalogWriteSupport
{
    /// <summary>
    /// A conflict when the caller's token is not the row's current one, otherwise <c>null</c>.
    /// <para>
    /// This is the <i>first</i> of two concurrency checks and it earns its place: it turns "somebody else
    /// edited this while your form was open" into a clear answer before anything is written, whereas the
    /// <c>RowVersion</c> predicate EF puts on the UPDATE can only fire for a change that lands between this
    /// handler's read and its save. Keeping both means the common case gets the good message and the narrow
    /// race still cannot overwrite anybody.
    /// </para>
    /// <para>
    /// A missing or malformed token is refused too, rather than taken as "no opinion". A client that forgot
    /// to send one is a client that cannot have seen the current row, and silently allowing the write is how
    /// optimistic concurrency becomes decorative.
    /// </para>
    /// </summary>
    public static CatalogWriteResult.Conflict? CheckRowVersion(byte[]? current, string? supplied)
    {
        var expected = CatalogFieldRules.ReadRowVersion(supplied);

        if (expected is not null && current is not null && expected.AsSpan().SequenceEqual(current))
        {
            return null;
        }

        return new CatalogWriteResult.Conflict(
            CatalogConflictCodes.StaleRowVersion,
            expected is null
                ? "This request did not carry the row's current version, so it cannot be applied safely. " +
                  "Reload the page and try again."
                : "Somebody else changed this while you were editing it. Reload the page to see their " +
                  "change, then re-apply yours.");
    }

    /// <summary>Turns the store's outcome into the use case's answer.</summary>
    public static CatalogWriteResult ToResult(CatalogSaveOutcome outcome, int entityId) => outcome switch
    {
        CatalogSaveOutcome.Saved => new CatalogWriteResult.Succeeded(entityId),

        CatalogSaveOutcome.NothingToSave => new CatalogWriteResult.Unchanged(entityId),

        CatalogSaveOutcome.ConcurrencyConflict => new CatalogWriteResult.Conflict(
            CatalogConflictCodes.StaleRowVersion,
            "Somebody else changed this while your change was being saved, so nothing was written. " +
            "Reload the page and try again."),

        CatalogSaveOutcome.DuplicateKey => new CatalogWriteResult.Conflict(
            CatalogConflictCodes.DuplicateKey,
            "Somebody else has just used that name or URL slug. Choose another one and try again."),

        _ => throw new InvalidOperationException($"Unhandled save outcome '{outcome}'."),
    };

    /// <summary>
    /// The same, for a save that locked and re-checked its pictures (#223): a
    /// <see cref="CatalogSaveOutcome.MediaUnavailable"/> answer becomes a 400 on each field whose picture failed
    /// the re-check, the shape the handler's own media check already uses.
    /// </summary>
    public static CatalogWriteResult ToResult(CatalogSaveOutcome outcome, int entityId, CatalogMediaClaim media)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (outcome != CatalogSaveOutcome.MediaUnavailable)
        {
            return ToResult(outcome, entityId);
        }

        return new CatalogWriteResult.Invalid(media.Unavailable
            .Select(slot => slot.Field)
            .Distinct(StringComparer.Ordinal)
            .Select(field => new ValidationError(
                field,
                "That picture was removed from the media library, or is no longer a ready image, while this was "
                + "being saved. Nothing was saved. Choose another picture and save again."))
            .ToList());
    }

    /// <summary>
    /// Re-applies doc 08 §6 step 3 to the product a save has just written, once the save has committed.
    /// </summary>
    /// <remarks>
    /// <b>This closes a hole W3-05b opened and would otherwise have shipped.</b>
    /// <c>ProductDerivedData.RefreshStats</c> computes <c>ProductStats</c>' pricing columns from the
    /// product and its variants alone — it has no campaign and cannot have one, being a pure function
    /// over the aggregate — so a product edited <i>during</i> a live campaign had its stored sale price
    /// written back up to its list price. The money path was never affected (the bag and checkout price
    /// live, doc 08 §7.4), but the card, the listing and the Sale page would have shown the undiscounted
    /// price, and <c>RefreshCampaignPricing</c> would not have fixed it: that job only looks at campaigns
    /// whose <i>boundary</i> has just passed, and a product save moves no boundary. The wrong price would
    /// have stood until the campaign ended.
    /// <para>
    /// <b>After the commit, and only on a save that wrote something.</b> The refresher is a set-based
    /// <c>UPDATE</c> that computes from committed rows, so it cannot run inside the unit of work it is
    /// correcting; and running it on a <c>Conflict</c> or an <c>Unchanged</c> would be work for nothing.
    /// It is idempotent, so the sequence "save writes the campaign-free summary, refresh writes the
    /// campaign-aware one" converges — the window between them is one statement wide and no price a
    /// shopper is charged reads either column.
    /// </para>
    /// </remarks>
    public static async Task<CatalogWriteResult> WithCampaignPricingAsync(
        CatalogWriteResult result,
        ICampaignPricingRefresher campaigns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaigns);

        if (result is CatalogWriteResult.Succeeded succeeded)
        {
            await campaigns.RefreshProductsAsync([succeeded.EntityId], cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Re-applies doc 08 §6 step 3 to a set of products a save has just moved under or out of a campaign's
    /// reach, once the save has committed.
    /// </summary>
    /// <remarks>
    /// <b>The overload above is "this one product"; this is "these products, for a reason that is not
    /// about any one of them".</b> A category move rewrites <c>Categories.Path</c> for a whole subtree,
    /// and <c>Path</c> is what every campaign subtree predicate matches on — so the products under that
    /// subtree change which campaigns reach them without themselves being touched. See
    /// <see cref="ICampaignPricingRefresher.ProductsLinkedToCategoriesAsync"/> for why the job does not
    /// correct it: no campaign boundary has moved.
    /// <para>
    /// After the commit and only on a save that wrote something, for the reasons the overload above
    /// gives, and with the same idempotence behind it: the statement recomputes the whole answer from
    /// committed rows, so a failure here leaves a stale listing and never a wrong one, and the money path
    /// does not read these columns at all.
    /// </para>
    /// </remarks>
    public static async Task<CatalogWriteResult> WithCampaignPricingForAsync(
        CatalogWriteResult result,
        ICampaignPricingRefresher campaigns,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        ArgumentNullException.ThrowIfNull(productIds);

        if (result is CatalogWriteResult.Succeeded && productIds.Count > 0)
        {
            await campaigns.RefreshProductsAsync(productIds, cancellationToken);
        }

        return result;
    }
}
