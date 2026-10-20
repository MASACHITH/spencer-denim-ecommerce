namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ProductReviews.Status</c> (doc 08 §3.2): <c>0 Pending, 1 Approved, 2 Hidden</c>. Only
/// <see cref="Approved"/> is shown to shoppers and counted in <c>ProductStats</c> (doc 08 §7.4).
/// </summary>
public enum ReviewStatus : byte
{
    /// <summary>Waiting for a moderator (doc 11 P12 step 3, while <c>Reviews.RequireApproval</c> is on).</summary>
    Pending = 0,

    /// <summary>Shown on the product page and counted in the rating.</summary>
    Approved = 1,

    /// <summary>Taken off the site by staff. Kept, because staff may approve it again (doc 11 P12 step 5).</summary>
    Hidden = 2,
}

/// <summary>
/// <c>catalog.ProductReviews</c> (PDP-08, ADM-16; doc 08 §3.2, doc 11 P12): one signed-in customer's rating and
/// review of one product.
/// <para>
/// <b>Plain text, never HTML.</b> <see cref="Title"/> and <see cref="Body"/> are stored exactly as
/// <see cref="ReviewRules"/> normalised them and every reader renders them as text (Angular interpolation),
/// so markup in a review is shown as the characters the customer typed and never runs.
/// </para>
/// <para>
/// <b>Written by set-based SQL only</b> (<c>ProductReviewStore</c>), because every write has to recompute the
/// product's rating in the same transaction under the same lock. Nothing tracks or saves this entity through
/// the change tracker; the class is the EF mapping and the read model's row.
/// </para>
/// </summary>
public sealed class ProductReview
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>The account that wrote it. Never an email address: doc 08 D4 matches purchases by account.</summary>
    public int CustomerUserId { get; set; }

    /// <summary>The <b>Delivered</b> order of this customer that contains the product (doc 08 D4), if any.</summary>
    public int? OrderId { get; set; }

    /// <summary>1 to 5 (<c>CK_ProductReviews_Rating</c>).</summary>
    public byte Rating { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }

    /// <summary>A snapshot taken at submission (doc 11 P12): first name and last initial.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Set at submission when a Delivered order of this account contains the product. A later refund does not
    /// clear it (doc 11 §7 G-27).
    /// </summary>
    public bool IsVerifiedBuyer { get; set; }

    public ReviewStatus Status { get; set; }

    public int? ModeratedByUserId { get; set; }

    public DateTime? ModeratedAtUtc { get; set; }

    public string? ModerationNote { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the customer last edited it (doc 11 §7 G-32). Moderation does not touch it.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>[RV]: the admin screen's concurrency token, exchanged as base64.</summary>
    public byte[]? RowVersion { get; set; }
}
