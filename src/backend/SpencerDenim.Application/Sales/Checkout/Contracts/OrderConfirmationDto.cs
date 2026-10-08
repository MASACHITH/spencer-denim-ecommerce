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
/// The order status as a name (<c>PendingPayment</c>, <c>Paid</c>, <c>Expired</c>). What the page says
/// about the money follows from this and from <paramref name="Payment"/>.
/// <para>
/// <b>It is the machine value and not what the page should print.</b> See
/// <paramref name="StatusLabel"/>.
/// </para>
/// </param>
/// <param name="StatusLabel">
/// What the shopper should read, which is <b>not</b> the status name lower-cased.
/// <para>
/// <b>Doc 11 P9's pickup variant is explicit that <c>Shipped</c> is shown to staff <i>and customer</i> as
/// "Ready for collection"</b>, and <c>Delivered</c> on an order the customer walked in and took is the
/// wrong verb. The page used to print <c>status.toLowerCase()</c>, so a pickup customer read "This order
/// is shipped." — which nothing could reach until ADM-09 made the transition pressable, and which W3-04's
/// own review caught.
/// </para>
/// <para>
/// Rendered by the same <c>OrderStatusLabels.For</c> the two admin screens use, on the server, so the
/// three cannot disagree about one order's wording. That is what the admin contract's own remarks already
/// claimed; sending it here is what makes the claim true.
/// </para>
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
/// <param name="Payment">
/// What the customer has to do to pay, as the order's own payment provider describes it (PAY-03, doc 11 P7
/// step 1): the bank details, the exact amount, <b>the order number to quote as the reference</b>, the due
/// date, and whether this provider takes a slip.
/// <para>
/// <see langword="null"/> for three different and all ordinary reasons, which the page has to tell apart
/// by looking at <paramref name="Status"/> rather than at this: the order is no longer awaiting payment, so
/// there is nothing to do; the provider does not describe itself; or — the one worth knowing about — <b>the
/// client has not filled their bank details in yet</b>. Doc 11 P7 makes "bank details set by a Super Admin"
/// a precondition of the whole process, and the shop can be deployed before the client sends them. The
/// order page then says the details are being confirmed, which is true, instead of printing an empty
/// account number somebody might transfer money to.
/// </para>
/// </param>
/// <param name="Proof">
/// What the customer has already sent about this payment — their reference and their slip — so the page
/// can show it back rather than offering an empty form to somebody who filled it in yesterday.
/// <see langword="null"/> when there is no open payment to attach anything to.
/// </param>
public sealed record OrderConfirmationDto(
    string OrderNumber,
    Guid PublicId,
    string Status,
    string StatusLabel,
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
    bool IsDuplicateSubmission = false,
    OrderPaymentInstructionsDto? Payment = null,
    OrderPaymentProofDto? Proof = null);

/// <summary>
/// The customer-facing "how to pay", built by the order's <c>IPaymentProvider</c> (PAY-03, doc 11 P7
/// step 1).
/// </summary>
/// <param name="ProviderCode">Which provider wrote this, so the page can key its wording off it.</param>
/// <param name="Reference">
/// <b>The reference the customer must quote</b> — the order number for a bank transfer. It is the only
/// thing tying an anonymous credit on a bank statement to an order (P7 step 5 has staff matching "amount
/// and reference"), which is why it is its own field rather than being left for the page to infer.
/// </param>
/// <param name="AmountLkr">The exact amount to transfer.</param>
/// <param name="DueAtUtc">
/// The deadline (placed + 48 h), sent as UTC so the storefront renders it in Colombo time rather than the
/// server's.
/// </param>
/// <param name="Fields">
/// The details to print, in order — bank, branch, account name, account number. A list rather than four
/// properties, because the next provider's details are not these four and a page that renders a list needs
/// no change when they differ.
/// </param>
/// <param name="Notes">
/// The shop's own extra instructions, <b>plain text</b>. Never markup: the value is a settings field a
/// staff user types, there is no HTML sanitiser in this build, and this payload is rendered on a public
/// page. See <c>SettingKeys.Bank.Instructions</c>.
/// </param>
/// <param name="RedirectUrl">Where to go to pay, for a provider that redirects. Always null for a bank transfer.</param>
/// <param name="AcceptsProof">Whether this provider takes a customer-entered reference and a slip.</param>
public sealed record OrderPaymentInstructionsDto(
    string ProviderCode,
    string? Reference,
    decimal AmountLkr,
    DateTime DueAtUtc,
    IReadOnlyList<OrderPaymentFieldDto> Fields,
    string? Notes,
    string? RedirectUrl,
    bool AcceptsProof);

/// <summary>One labelled payment detail, e.g. "Account number" / "8001234567890".</summary>
/// <param name="Label">What to print beside the value. Written by the provider, never by a client.</param>
/// <param name="Value">The value.</param>
/// <param name="Copyable">
/// Whether the page should offer a copy button. True for the account number and the reference — the two
/// values a shopper retypes into online banking, and the two where a typo costs them money.
/// </param>
public sealed record OrderPaymentFieldDto(string Label, string Value, bool Copyable);

/// <summary>
/// What the customer has already sent about their transfer (doc 11 P7 step 3).
/// </summary>
/// <param name="Reference">The bank reference they typed, or null.</param>
/// <param name="SlipPublicId">
/// The slip's token, or null. It is a <b>private</b> asset: this token only opens through the order's own
/// authorised route and never through <c>/media</c> (doc 08 §3.3).
/// </param>
/// <param name="SlipFileName">What they called the file, so the page can say which one is on the order.</param>
/// <param name="SlipContentType">So the page knows whether it is an image or a PDF before fetching it.</param>
/// <param name="SlipUploadedAtUtc">When it arrived.</param>
public sealed record OrderPaymentProofDto(
    string? Reference,
    Guid? SlipPublicId,
    string? SlipFileName,
    string? SlipContentType,
    DateTime? SlipUploadedAtUtc);
