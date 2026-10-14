using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// The delivery address a shopper typed (CHK-01). Every field is nullable, and that is the fix for #47
/// rather than laxness — see <see cref="PlaceOrderCommand"/>.
/// </summary>
public sealed record CheckoutAddressInput(
    string? RecipientName,
    string? Phone,
    string? Line1,
    string? Line2,
    string? City,
    string? PostalCode,
    string? Landmark);

/// <summary>
/// Place the order (CHK-01..04, doc 11 P6).
/// </summary>
/// <remarks>
/// <b>Every string on this command and on <see cref="CheckoutAddressInput"/> is nullable, deliberately,
/// and this is issue #47.</b> A well-formed JSON object that simply omits a required string property binds
/// <see langword="null"/> into a non-nullable reference property — the compiler's annotation is not a
/// runtime check — and the first thing to touch it throws, which the public API returned as <b>500</b>
/// until a tester found it. A checkout request is almost entirely strings, so the shapes <c>{}</c> and
/// <c>{"email":"a@b.c"}</c> are the ones most likely to arrive from a broken client, a scanner or a
/// half-filled form. Nullable properties can hold what the binder produces;
/// <see cref="PlaceOrderValidator"/> then reports every missing field at once as one 400.
/// </remarks>
/// <param name="CheckoutKey">
/// The idempotency token from the checkout session (<c>sales.Orders.CheckoutIdempotencyKey</c>). Required:
/// doc 08 §5.4 makes the unique index on it the mechanism that turns a double submit into a no-op, and a
/// request without one is a request that cannot be made safe.
/// </param>
/// <param name="ExpectedGrandTotal">
/// The total the shopper was looking at. Required, and compared with the freshly priced total before
/// anything is written — doc 11 §7 G-10's resolution, verbatim: "send the displayed total with the
/// request; if the placed total differs, reject with 'prices changed, please review'". An order is never
/// placed at a figure the customer has not seen.
/// </param>
public sealed record PlaceOrderCommand(
    CartLookup Lookup,
    Guid CheckoutKey,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    int? DeliveryMethodId,
    int? DistrictId,
    int? PickupLocationId,
    CheckoutAddressInput? Address,
    string? PaymentMethodCode,
    decimal? ExpectedGrandTotal,
    string? CustomerNote,
    string? PlacedIpAddress,
    string? PromoCode = null)
{
    /// <summary>
    /// The account placing it, or <see langword="null"/> for a guest.
    /// <para>
    /// <b>It is always null today (CHK-03).</b> There is no customer authentication in the repository
    /// (ACC-01, W3-01), so no request can carry a customer identity and <i>every</i> checkout is a guest
    /// checkout. It is a property rather than a constructor parameter so that W3-01 sets it in the endpoint
    /// and nothing else in the placement path changes: the order's <c>CustomerUserId</c>, and therefore
    /// whether it appears in an order history, is the only difference between the two.
    /// </para>
    /// </summary>
    public int? CustomerUserId { get; init; }
}

/// <summary>Why a placement attempt ended the way it did.</summary>
public enum PlaceOrderStatus : byte
{
    /// <summary>The order exists. <see cref="PlaceOrderResult.Confirmation"/> is the success page.</summary>
    Placed = 1,

    /// <summary>
    /// The same <see cref="PlaceOrderCommand.CheckoutKey"/> had already placed an order, so this request
    /// wrote nothing and the first order is returned again (doc 08 §5.4).
    /// </summary>
    AlreadyPlaced = 2,

    /// <summary>Bad or missing input. <see cref="PlaceOrderResult.Errors"/> holds every reason at once.</summary>
    Invalid = 3,

    /// <summary>
    /// The bag cannot be checked out as it stands: empty, or holding something that cannot be bought
    /// (G-08), or no delivery option, or no payment provider.
    /// </summary>
    Blocked = 4,

    /// <summary>
    /// At least one line could not be reserved. Nothing was written and
    /// <see cref="PlaceOrderResult.ShortLines"/> says how many of each are left (doc 08 §5.4 step 3).
    /// </summary>
    OutOfStock = 5,

    /// <summary>
    /// The freshly priced total is not the total the shopper was shown (G-10).
    /// <see cref="PlaceOrderResult.Totals"/> is the new ladder, for the page to show before asking again.
    /// </summary>
    TotalChanged = 6,

    /// <summary>
    /// The promo code could not be used (ADM-13). Nothing was placed and nothing was reserved.
    /// <see cref="PlaceOrderResult.PromoRefusal"/> says why and
    /// <see cref="PlaceOrderResult.PromoMinimumSpend"/> carries the threshold when that is the reason.
    /// <para>
    /// <b>Its own status rather than a field error, because two different things happen on the way to
    /// it.</b> A code can stop qualifying between the quote and the press — the window closed, the bag
    /// changed, someone took the last use — and a code can be refused <i>inside</i> the transaction by
    /// the conditional UPDATE or the per-customer count under the app lock. Both leave the shopper in
    /// the same place and are reported the same way, so the page has one branch and not two.
    /// </para>
    /// </summary>
    PromoRejected = 7,
}

/// <summary>The outcome of <see cref="PlaceOrderCommand"/>.</summary>
public sealed record PlaceOrderResult(
    PlaceOrderStatus Status,
    OrderConfirmationDto? Confirmation,
    IReadOnlyList<ValidationError> Errors,
    IReadOnlyList<CheckoutBlocker> Blockers,
    IReadOnlyDictionary<int, int> ShortLines,
    CheckoutTotalsDto? Totals,
    PromoRefusal? PromoRefusal = null,
    decimal? PromoMinimumSpend = null)
{
    private static readonly IReadOnlyList<ValidationError> NoErrors = [];
    private static readonly IReadOnlyList<CheckoutBlocker> NoBlockers = [];
    private static readonly IReadOnlyDictionary<int, int> NoShortLines = new Dictionary<int, int>();

    public static PlaceOrderResult Placed(OrderConfirmationDto confirmation) =>
        new(PlaceOrderStatus.Placed, confirmation, NoErrors, NoBlockers, NoShortLines, null);

    public static PlaceOrderResult AlreadyPlaced(OrderConfirmationDto confirmation) =>
        new(
            PlaceOrderStatus.AlreadyPlaced,
            confirmation with { IsDuplicateSubmission = true },
            NoErrors,
            NoBlockers,
            NoShortLines,
            null);

    public static PlaceOrderResult Invalid(IReadOnlyList<ValidationError> errors) =>
        new(PlaceOrderStatus.Invalid, null, errors, NoBlockers, NoShortLines, null);

    public static PlaceOrderResult Blocked(IReadOnlyList<CheckoutBlocker> blockers) =>
        new(PlaceOrderStatus.Blocked, null, NoErrors, blockers, NoShortLines, null);

    public static PlaceOrderResult OutOfStock(IReadOnlyDictionary<int, int> shortLines) =>
        new(PlaceOrderStatus.OutOfStock, null, NoErrors, NoBlockers, shortLines, null);

    public static PlaceOrderResult TotalChanged(CheckoutTotalsDto totals) =>
        new(PlaceOrderStatus.TotalChanged, null, NoErrors, NoBlockers, NoShortLines, totals);

    /// <summary>The code was refused, before or inside the transaction (ADM-13).</summary>
    public static PlaceOrderResult PromoRejected(PromoRefusal refusal, decimal? minimumSpend = null) =>
        new(
            PlaceOrderStatus.PromoRejected,
            null,
            NoErrors,
            NoBlockers,
            NoShortLines,
            null,
            refusal,
            minimumSpend);
}
