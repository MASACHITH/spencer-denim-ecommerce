namespace SpencerDenim.Application.Sales.Checkout.Contracts;

/// <summary>One line of a placed order, read back from the snapshot columns (CHK-04).</summary>
/// <remarks>
/// <b>Every field here comes from <c>sales.OrderLines</c> and nothing is joined to the catalogue.</b>
/// That is the point of the snapshot: an order has to keep reading the same after the product has been
/// renamed, repriced, unpublished or deleted. The image is the one exception in spirit — the storage key
/// was captured at placement, so a withdrawn photograph leaves a gap rather than changing the order.
/// </remarks>
public sealed record OrderLineDto(
    short LineNumber,
    string ProductName,
    string ProductCode,
    string Sku,
    string ColourName,
    string SizeLabel,
    string? ImageUrl,
    short Quantity,
    decimal UnitOriginalPrice,
    decimal UnitPrice,
    decimal LineSubtotal,
    decimal PromoDiscountAllocated,
    decimal LineTotal);

/// <summary>Where a placed order is going (CHK-04).</summary>
/// <param name="PickupLocationName">
/// Set only for a store pickup order, from <c>Orders.Delivery.PickupLocationName</c>.
/// </param>
public sealed record OrderDeliveryDto(
    string MethodCode,
    string MethodName,
    string? PickupLocationName,
    string? RecipientName,
    string? Phone,
    string? Line1,
    string? Line2,
    string? City,
    string DistrictName,
    string? PostalCode,
    string? Landmark,
    byte EstimatedDaysMin,
    byte EstimatedDaysMax);

/// <summary>
/// A placed order as the success page and the guest order link show it (CHK-04, doc 11 P6 steps 10–11).
/// </summary>
/// <param name="PublicId">
/// The unguessable token in the order's address. It is in this payload because the page the shopper lands
/// on has to be reloadable and shareable with nobody else: doc 08 §3.5 keeps it for exactly that, and a
/// sequential id would let anyone read the next person's order.
/// </param>
/// <param name="Status">
/// The order status as a name (<c>PendingPayment</c>). The success page says the order is waiting for
/// payment; <b>what happens to the money is not this task</b> — bank details, the reference to quote, the
/// slip upload and the expiry countdown are doc 11 P7, W3-03's step 3.
/// </param>
/// <param name="PaymentDueAtUtc">
/// When the hold lapses (<c>Orders.PaymentDueAtUtc</c>). Shown so a shopper knows the items are held, and
/// sent as UTC so the storefront renders it in Colombo time rather than the server's.
/// </param>
/// <param name="IsDuplicateSubmission">
/// True when this response is a replay of an earlier identical submission — the same
/// <c>CheckoutIdempotencyKey</c> arrived twice and the first order was returned again. Nothing about the
/// order differs; it exists so a test can prove the second request created nothing, and so the storefront
/// never says "order placed" twice for one order.
/// </param>
public sealed record OrderConfirmationDto(
    string OrderNumber,
    Guid PublicId,
    string Status,
    string Email,
    string FirstName,
    string? LastName,
    string? Phone,
    IReadOnlyList<OrderLineDto> Lines,
    OrderDeliveryDto Delivery,
    decimal ItemsOriginalTotal,
    decimal ItemsSubtotal,
    decimal SavedTotal,
    decimal PromoDiscountAmount,
    decimal DeliveryFeeAmount,
    decimal DeliveryDiscountAmount,
    decimal GrandTotal,
    string Currency,
    string PaymentMethodCode,
    string PaymentMethodName,
    string? CustomerNote,
    DateTime PlacedAtUtc,
    DateTime PaymentDueAtUtc,
    bool IsDuplicateSubmission = false);
