namespace SpencerDenim.Application.Ops.Email.Contracts;

/// <summary>
/// Who an order email goes to, as the order itself records it.
/// </summary>
/// <remarks>
/// <b>From the order, never from an account.</b> Doc 11 §4.1 addresses every order email to the "Order
/// email" — the address typed at checkout — because a guest has no account and a signed-in customer may
/// have ordered to a different address. Doc 11 §4 adds the consequence: a wrong address cannot be
/// corrected on a placed order, so a resend reaches the same wrong address and those customers are
/// telephoned.
/// </remarks>
public sealed record OrderEmailAddressee(string Email, string? FirstName, string? LastName)
{
    /// <summary>
    /// The display name for the <c>To</c> header, or <see langword="null"/> when the order has neither
    /// name — never a placeholder like "Customer", which would be a name we invented.
    /// </summary>
    public string? DisplayName
    {
        get
        {
            var name = string.Join(' ', new[] { FirstName, LastName }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim()));

            return name.Length == 0 ? null : name;
        }
    }

    /// <summary>
    /// How the body opens: "Hello Nimal," or, with no first name, "Hello," — the greeting degrades
    /// rather than guessing.
    /// </summary>
    public string Greeting =>
        string.IsNullOrWhiteSpace(FirstName) ? "Hello," : $"Hello {FirstName.Trim()},";
}

/// <summary>
/// The facts every order email carries: which order, for whom, and the instant the queueing transaction
/// is stamping everything else with.
/// </summary>
/// <param name="PublicId">
/// The order's public token. It is the <c>RelatedEntityId</c> on the outbox row and the last segment of
/// every link in the body, so a guest can reach their order from the email — doc 11 P6 step 11.
/// </param>
/// <param name="NowUtc">
/// The transaction's own "now", passed in rather than read from a clock inside the queue, so the outbox
/// row's <c>CreatedAtUtc</c> is the same instant as the status history row it commits beside.
/// </param>
public sealed record OrderEmailHeader(
    string OrderNumber,
    Guid PublicId,
    OrderEmailAddressee To,
    DateTime NowUtc);

/// <summary>One line of the order, as the order's own snapshot records it (doc 08 §3.5).</summary>
public sealed record OrderEmailLine(
    string ProductName,
    string ColourName,
    string SizeLabel,
    short Quantity,
    decimal LineTotal);

/// <summary>
/// The money, as <c>CK_Orders_Totals</c> holds it: subtotal − promo + delivery fee − delivery discount =
/// grand total.
/// </summary>
public sealed record OrderEmailTotals(
    decimal ItemsSubtotal,
    decimal PromoDiscountAmount,
    decimal DeliveryFeeAmount,
    decimal DeliveryDiscountAmount,
    decimal GrandTotal);

/// <summary>
/// Where the order is going, in the two shapes doc 11 P6 allows: a courier delivery to an address, or
/// collection from a store.
/// </summary>
/// <param name="AddressLines">
/// The delivery address as it was snapshotted on to the order, already split into lines. Empty for a
/// pickup order, whose <c>Ship_*</c> columns are all null by design (<c>CK_Orders_ShipAddress</c>).
/// </param>
public sealed record OrderEmailDelivery(
    string MethodName,
    bool IsPickup,
    string? PickupLocationName,
    IReadOnlyList<string> AddressLines,
    string DistrictName);

/// <summary>Everything the <c>OrderPlaced</c> email prints (doc 11 §4.1's content summary).</summary>
public sealed record PlacedOrderEmailFacts(
    OrderEmailHeader Header,
    IReadOnlyList<OrderEmailLine> Lines,
    OrderEmailTotals Totals,
    OrderEmailDelivery Delivery,
    string PaymentMethodName,
    DateTime PaymentDueAtUtc);

/// <summary>
/// What the <c>OrderShipped</c> email needs, in the two shapes doc 11 P9 ships an order in.
/// </summary>
/// <param name="TrackingUrl">
/// The courier's template with the tracking number substituted, or <see langword="null"/> for a courier
/// with no online tracking — in which case the number is printed as text and nothing pretends to be a
/// link (doc 08 §3.5 <c>Couriers</c>).
/// </param>
/// <param name="PickupOpeningHours">
/// Read from the pickup location, because doc 11 §4.1 lists "pickup location, hours" in this email's
/// content and the order's own snapshot keeps only the location's name (doc 08 §3.5
/// <c>Orders.Delivery</c>).
/// </param>
public sealed record ShippedOrderEmailFacts(
    OrderEmailHeader Header,
    bool IsPickup,
    string? CourierName,
    string? TrackingNumber,
    string? TrackingUrl,
    string? PickupLocationName,
    string? PickupAddress,
    string? PickupOpeningHours);

/// <summary>
/// What the <c>PaymentConfirmed</c> email needs (doc 11 §4.1: "Payment received, amount, invoice number,
/// what happens next").
/// </summary>
/// <param name="InvoiceNumber">
/// Issued inside the same transaction that queues this message (doc 11 P7 step 6a), so it is never null
/// in practice — but it is nullable, because an email that printed "Invoice " and stopped would be worse
/// than one that omits the line.
/// </param>
public sealed record PaymentConfirmedEmailFacts(
    OrderEmailHeader Header,
    decimal AmountLkr,
    string? InvoiceNumber,
    bool WasLatePayment);

/// <summary>
/// What the <c>OrderCancelled</c> email needs. See <c>EmailTemplateCodes.OrderCancelled</c> for the scope
/// note: this template is doc 11 §7 G-14's recommendation, not a v1 commitment of doc 08 or doc 11 §4.
/// </summary>
/// <param name="CancelledByCustomer">
/// Doc 11 P10 step 3 records who closed the order, and the customer needs to be told which it was: "you
/// cancelled this" and "we cancelled this" are not the same message to receive.
/// </param>
public sealed record CancelledOrderEmailFacts(
    OrderEmailHeader Header,
    bool CancelledByCustomer);
