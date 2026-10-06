using SpencerDenim.Application.Sales.Cart.Contracts;

namespace SpencerDenim.Application.Sales.Checkout.Contracts;

/// <summary>Why a checkout cannot be completed as it stands (CHK-01).</summary>
/// <remarks>
/// A reason travels, not a sentence — the same contract <see cref="CartLineStatus"/> uses, and for the
/// same reason: the wording is the storefront's and a code is what a test can assert on.
/// </remarks>
public enum CheckoutBlocker : byte
{
    /// <summary>Nothing in the bag. Doc 11 P6's first precondition.</summary>
    EmptyBag = 1,

    /// <summary>
    /// A line is unavailable or over available stock. Doc 11 P5/G-08: warn and block until the shopper
    /// adjusts it; never reduce a quantity or drop a line on their behalf.
    /// </summary>
    LineNotPurchasable = 2,

    /// <summary>
    /// No delivery method is on and available for this district's zone. Doc 11 P6 precondition 3 and
    /// doc 08 §3.5's "a missing row means unavailable".
    /// </summary>
    NoDeliveryMethod = 3,

    /// <summary>
    /// No payment provider is enabled. Doc 11 P6 precondition 2. The shop cannot take an order it has no
    /// way to be paid for, and saying so is better than a Place order button that always fails.
    /// </summary>
    NoPaymentMethod = 4,
}

/// <summary>
/// A district a delivery address can name (doc 08 §3.1, CHK-01).
/// </summary>
/// <param name="DeliveryZone">
/// Sent so the storefront can group or explain a rate without a second request. It is the <b>current</b>
/// zone: the figure an order keeps is the snapshot taken at placement.
/// </param>
public sealed record DistrictDto(int Id, string Name, byte DeliveryZone);

/// <summary>
/// One delivery option as this district's zone prices it (CHK-02, doc 08 §3.5 and §6 step 10).
/// </summary>
/// <param name="IsAvailable">
/// False when the zone rate row says so (Express island-wide) <b>or</b> when the method has no rate row
/// for this zone at all, which doc 08 §3.5 defines as unavailable. The option is still listed, with
/// <paramref name="Fee"/> null, because a silently missing option looks like a bug to a shopper who used
/// it last week — and an admin gap check needs the difference to be visible.
/// </param>
/// <param name="Fee">
/// The zone rate's fee, or <see langword="null"/> when the option is unavailable. Zero is a real fee
/// (store pickup) and is not the same answer as null.
/// </param>
/// <param name="FreeAboveAmount">
/// The free-delivery threshold, or <see langword="null"/> for "never free". Doc 11 P6 step 4 asks for the
/// "free delivery above Rs. 10,000.00" message, and this is what it is drawn from.
/// </param>
/// <param name="FeeAfterDiscount">
/// What this option would actually add to the total for the bag as it stands: <paramref name="Fee"/>, or
/// zero once the threshold is met. Computed on the server so the shopper's screen and the order row
/// cannot disagree about it.
/// </param>
/// <param name="RequiresAddress">False for store pickup, which asks for a location instead.</param>
public sealed record DeliveryOptionDto(
    int Id,
    string Code,
    string Name,
    string? Description,
    bool RequiresAddress,
    bool IsAvailable,
    decimal? Fee,
    decimal? FreeAboveAmount,
    decimal? FeeAfterDiscount,
    byte? EstimatedDaysMin,
    byte? EstimatedDaysMax);

/// <summary>A store to collect from (CHK-02, doc 08 §3.5 <c>PickupLocations</c>).</summary>
public sealed record PickupLocationDto(
    int Id,
    string Name,
    string AddressLine,
    string City,
    int DistrictId,
    string? Phone,
    string? OpeningHours);

/// <summary>
/// An enabled payment provider (doc 08 §3.5 <c>PaymentProviders</c>; PAY-01/PAY-03).
/// <para>
/// <b>Nothing about money beyond the name and the description.</b> Bank details, the reference to quote,
/// the slip upload and the PayPal conversion all belong to W3-03's steps 2 and 3 (doc 11 P7, P8). This
/// payload exists so checkout can list what the shop accepts and record which one the shopper chose.
/// </para>
/// </summary>
public sealed record PaymentMethodDto(string Code, string DisplayName, string? CheckoutDescription);

/// <summary>
/// One line as checkout shows it (CHK-01). The same facts as a bag line plus the snapshot columns an
/// order line keeps, so that the summary on screen is drawn from exactly the figures that would be
/// written.
/// </summary>
public sealed record CheckoutLineDto(
    int VariantId,
    int ProductId,
    string ProductName,
    string Slug,
    string ColourName,
    string SizeLabel,
    string? ImageUrl,
    string? ImageAltText,
    short Quantity,
    int AvailableQuantity,
    decimal UnitOriginalPrice,
    decimal UnitPrice,
    decimal LineSubtotal,
    CartLineStatus Status);

/// <summary>
/// The order summary's money ladder (CHK-01), and it has to add up on screen:
/// <c>ItemsOriginalTotal − SavedTotal − PromoDiscountAmount + DeliveryFeeAmount − DeliveryDiscountAmount
/// = GrandTotal</c>.
/// </summary>
/// <remarks>
/// <b>The labels are the bag's, deliberately.</b> W2-09 settled that <c>Subtotal</c> is the original
/// ("was") total, the deduction row is <b>Savings</b>, and <c>Total</c> is the net figure — because the
/// bag had shipped a ladder where subtotal minus discount did not equal the total. Checkout prints the
/// same three rows from the same arithmetic, with delivery added, so a shopper moving from one page to the
/// other never sees the same word mean two things.
/// <para>
/// <b>And the <i>count</i> beside the Subtotal label is the bag's too, which is
/// <see cref="BuyableItemCount"/> and not <see cref="ItemCount"/>.</b> It was <c>ItemCount</c>, and that
/// is a defect found in testing: a bag of "470 ×2 + 471 ×1" whose 471 had been deactivated printed
/// "Subtotal (2 items) Rs. 7,980" on the bag page and "Subtotal (3 items) Rs. 7,980" on checkout. The
/// money was right both times; the label counted an item its own figure excludes. One count per question
/// is the fix, which is what the bag already had — see <c>CartTotalsDto</c>.
/// </para>
/// <para>
/// <b><c>DeliveryFeeAmount</c> is null until a delivery option is chosen</b>, and null is not zero. Doc 08
/// §6 step 10 needs a district and a method before a fee exists at all; printing 0.00 would be a promise
/// of free delivery.
/// </para>
/// </remarks>
/// <param name="ItemCount">
/// Σ quantity over <b>every</b> line, including the ones that cannot be bought. It is what a header badge
/// or a "your bag has N things in it" sentence counts, and it is deliberately not what the Subtotal label
/// counts, because it is not what the Subtotal figure sums.
/// </param>
/// <param name="BuyableItemCount">
/// Σ quantity over the lines that are priced into <see cref="ItemsOriginalTotal"/> and
/// <see cref="ItemsSubtotal"/> — exactly <c>CheckoutQuote.IsPriced</c>, which is exactly the bag's rule.
/// This is the figure the Subtotal label prints, so the count and the money describe the same set of
/// lines.
/// </param>
public sealed record CheckoutTotalsDto(
    int ItemCount,
    int BuyableItemCount,
    int LineCount,
    decimal ItemsOriginalTotal,
    decimal ItemsSubtotal,
    decimal SavedTotal,
    decimal PromoDiscountAmount,
    decimal? DeliveryFeeAmount,
    decimal DeliveryDiscountAmount,
    decimal? GrandTotal,
    bool PromoDiscountClamped,
    string Currency);

/// <summary>
/// Everything the checkout page needs for one visitor (CHK-01..03).
/// </summary>
/// <param name="CheckoutKey">
/// The idempotency token for this attempt (<c>sales.Orders.CheckoutIdempotencyKey</c>). Issued by the
/// server and echoed back on Place order, so a double-clicked button, a retried request or a reloaded
/// form produces one order rather than two with two stock reservations. It is unguessable because doc 08
/// §3.5 asks for a <c>uniqueidentifier</c> and the server is the only party whose entropy it can vouch
/// for.
/// </param>
/// <param name="SelectedDistrictId">
/// The district the quote was priced for: whatever was asked for, or <see langword="null"/>. There is no
/// default. Doc 08 §3.5 R7 would take one from <c>Users.DefaultDistrictId</c> or the
/// <c>Delivery.DefaultZone</c> setting, and neither is reachable — there is no customer sign-in (ACC-01)
/// and no <c>ops.Settings</c> table (ADM-22) — so rather than guess a zone and print a fee from it, the
/// page asks.
/// </param>
/// <param name="Blockers">
/// Empty when the order can be placed. Sent as a list rather than a boolean so the page can say which
/// thing is wrong, and so a test can assert on the reason rather than on a disabled button.
/// </param>
public sealed record CheckoutSessionDto(
    Guid CheckoutKey,
    IReadOnlyList<CheckoutLineDto> Lines,
    CheckoutTotalsDto Totals,
    IReadOnlyList<DistrictDto> Districts,
    IReadOnlyList<DeliveryOptionDto> DeliveryOptions,
    IReadOnlyList<PickupLocationDto> PickupLocations,
    IReadOnlyList<PaymentMethodDto> PaymentMethods,
    int? SelectedDistrictId,
    int? SelectedDeliveryMethodId,
    IReadOnlyList<CheckoutBlocker> Blockers);
