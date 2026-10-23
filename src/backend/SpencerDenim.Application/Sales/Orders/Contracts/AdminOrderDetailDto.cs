namespace SpencerDenim.Application.Sales.Orders.Contracts;

/// <summary>
/// One order, as the admin detail screen reads it (ADM-09, doc 11 P9).
/// <para>
/// <b>Everything on it is a placement-time snapshot, and that is doc 08 §3.5's design rather than this
/// record's.</b> The product names, colours, sizes, prices, address and delivery method are columns on
/// <c>sales.Orders</c> and <c>sales.OrderLines</c>, not joins — so an order keeps reading the same after
/// the product has been renamed, repriced or soft-deleted. The only things read live are the courier list
/// a dispatch may choose from and the tracking URL template (doc 08 §3.5: "tracking links are rendered
/// from the current template").
/// </para>
/// </summary>
/// <param name="OrderPublicId">The token every action and link uses.</param>
/// <param name="OrderNumber">What the customer quotes.</param>
/// <param name="Status">The status name (doc 08 §4.1).</param>
/// <param name="StatusLabel">What to read, which differs for a pickup order — see <see cref="OrderStatusLabels"/>.</param>
/// <param name="StockState">
/// <c>Reserved</c>, <c>Committed</c>, <c>Released</c> or <c>Restocked</c>. Shown because it is the other
/// half of every guard in doc 08 §4.1 and the thing that explains why an action is unavailable: an expired
/// order's stock is <i>released</i>, which is why accepting its late payment has to re-check it.
/// </param>
/// <param name="Customer">Who to telephone.</param>
/// <param name="Delivery">Where it is going, or where it is waiting to be collected.</param>
/// <param name="Money">What was charged, broken down.</param>
/// <param name="Lines">What was bought, in line order.</param>
/// <param name="Payments">Every attempt to pay, newest first — see <see cref="AdminOrderPaymentDto"/>.</param>
/// <param name="Refunds">Every refund recorded against the order, newest first.</param>
/// <param name="History">
/// The order's own timeline, <b>oldest first</b>, with who did what and when (doc 08 §3.5
/// <c>OrderStatusHistory</c>). Oldest first because it is read as a story rather than scanned as a queue.
/// </param>
/// <param name="Dispatch">The courier, the tracking number and the link, or <see langword="null"/> before dispatch.</param>
/// <param name="Actions">Which transitions this order can take right now, and why not when it cannot.</param>
/// <param name="CustomerNote">What the customer asked for. Shown to staff; never acted on by the system.</param>
/// <param name="InternalNote">
/// Staff-only. Doc 08 §4.1: orders never move backwards, so a correction is this note plus a history note.
/// </param>
/// <param name="CancelReason">Why it was cancelled, when it was.</param>
/// <param name="InvoiceNumber">Issued at payment, or <see langword="null"/>.</param>
/// <param name="InvoiceIssuedAtUtc">When the number was issued.</param>
/// <param name="PlacedAtUtc">When it was placed.</param>
/// <param name="PaymentDueAtUtc">When the hold lapses.</param>
/// <param name="SecondsUntilExpiry">
/// Time left on the server's clock while the order is still awaiting payment, negative once past;
/// <see langword="null"/> otherwise.
/// </param>
/// <param name="PaidAtUtc">When the money was taken.</param>
/// <param name="ShippedAtUtc">When it was dispatched, or placed at the pickup point.</param>
/// <param name="DeliveredAtUtc">When it arrived, or was collected.</param>
/// <param name="ClosedAtUtc">When it reached a terminal state.</param>
public sealed record AdminOrderDetailDto(
    Guid OrderPublicId,
    string OrderNumber,
    string Status,
    string StatusLabel,
    string StockState,
    AdminOrderCustomerDto Customer,
    AdminOrderDeliveryDto Delivery,
    AdminOrderMoneyDto Money,
    IReadOnlyList<AdminOrderLineDto> Lines,
    IReadOnlyList<AdminOrderPaymentDto> Payments,
    IReadOnlyList<AdminOrderRefundDto> Refunds,
    IReadOnlyList<AdminOrderHistoryDto> History,
    AdminOrderDispatchDto? Dispatch,
    AdminOrderActionsDto Actions,
    string? CustomerNote,
    string? InternalNote,
    string? CancelReason,
    string? InvoiceNumber,
    DateTime? InvoiceIssuedAtUtc,
    DateTime PlacedAtUtc,
    DateTime PaymentDueAtUtc,
    long? SecondsUntilExpiry,
    DateTime? PaidAtUtc,
    DateTime? ShippedAtUtc,
    DateTime? DeliveredAtUtc,
    DateTime? ClosedAtUtc);

/// <summary>
/// Who placed the order, from the order's own columns.
/// </summary>
/// <param name="Name">First and last name as snapshotted.</param>
/// <param name="Email">The address the order was placed with.</param>
/// <param name="Phone">
/// <see langword="null"/> after PDPA anonymisation (doc 08 §3.1 N5), which is a real state and not an
/// error: the screen says so rather than drawing a blank.
/// </param>
/// <param name="HasAccount">
/// Whether the order belongs to a signed-in customer or was placed as a guest. The <i>customer id itself
/// is not returned</i>: a guest order has no account to link to and ADM-15's customer screen is a
/// different task.
/// </param>
/// <param name="IsAnonymised">
/// Whether a PDPA removal has overwritten the personal columns. It is here so the screen can say "this
/// order's customer details were removed on request" instead of presenting blanks as missing data.
/// </param>
public sealed record AdminOrderCustomerDto(
    string Name,
    string Email,
    string? Phone,
    bool HasAccount,
    bool IsAnonymised);

/// <summary>Where the order is going.</summary>
/// <param name="MethodCode">The snapshot of <c>DeliveryMethods.Code</c>, which the pickup rules branch on.</param>
/// <param name="MethodName">The snapshot of the name.</param>
/// <param name="IsPickup">Whether the method is store pickup.</param>
/// <param name="PickupLocationName">Where to collect it, for a pickup order.</param>
/// <param name="DistrictName">The snapshot of the district.</param>
/// <param name="Zone">The snapshot of the delivery zone the fee was quoted for.</param>
/// <param name="EstimatedDaysMin">The estimate quoted at placement.</param>
/// <param name="EstimatedDaysMax">The estimate quoted at placement.</param>
/// <param name="RecipientName">The ship-to address, which is <see langword="null"/> throughout for a pickup.</param>
/// <param name="RecipientPhone">The number the courier calls.</param>
/// <param name="Line1">Street address.</param>
/// <param name="Line2">Street address, second line.</param>
/// <param name="City">Town or city.</param>
/// <param name="PostalCode">Postcode.</param>
/// <param name="Landmark">A nearby landmark. Sri Lankan addresses often need one to be findable.</param>
/// <param name="Region">An overseas address's state or region (owner decision 2026-10-21).</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 of the destination.</param>
/// <param name="CountryName">The destination country as snapshotted on the order.</param>
public sealed record AdminOrderDeliveryDto(
    string MethodCode,
    string MethodName,
    bool IsPickup,
    string? PickupLocationName,
    string? DistrictName,
    string Zone,
    byte EstimatedDaysMin,
    byte EstimatedDaysMax,
    string? RecipientName,
    string? RecipientPhone,
    string? Line1,
    string? Line2,
    string? City,
    string? PostalCode,
    string? Landmark,
    string? Region = null,
    string CountryCode = Domain.Reference.Country.SriLankaCode,
    string CountryName = "Sri Lanka");

/// <summary>
/// The money, exactly as <c>sales.Orders</c> holds it (doc 08 §3.5, §6).
/// </summary>
/// <remarks>
/// <b>Every figure is a stored column and none is recomputed here.</b> <c>CK_Orders_Totals</c> proves the
/// stored set adds up, so a screen that re-derived the grand total from the lines would be a second
/// arithmetic that could disagree with the one the customer was charged — and the one on the invoice.
/// </remarks>
/// <param name="Currency">Always <c>LKR</c> today; sent so a page never assumes.</param>
/// <param name="ItemsOriginalTotal">Σ original price × quantity.</param>
/// <param name="ItemsListTotal">Σ list price × quantity.</param>
/// <param name="ItemsSubtotal">Σ line subtotal, at the prices actually charged.</param>
/// <param name="MarkdownDiscountTotal">Original − list.</param>
/// <param name="CampaignDiscountTotal">List − subtotal: what a sale campaign took off.</param>
/// <param name="PromoCodeText">The code as the customer typed it, kept even if the promo is deleted.</param>
/// <param name="PromoDiscountAmount">What the code took off.</param>
/// <param name="DeliveryFeeAmount">The fee charged.</param>
/// <param name="DeliveryDiscountAmount">A free-delivery threshold or a promo that paid the fee.</param>
/// <param name="GrandTotal">What the customer owed.</param>
/// <param name="RefundedAmount">
/// How much of the settlement payment has gone back. <b>Zero for a refund of a non-settlement payment</b>
/// (doc 08 §3.5): the G-13 case refunds money received for an order that was never settled, so there was
/// never anything to un-settle.
/// </param>
public sealed record AdminOrderMoneyDto(
    string Currency,
    decimal ItemsOriginalTotal,
    decimal ItemsListTotal,
    decimal ItemsSubtotal,
    decimal MarkdownDiscountTotal,
    decimal CampaignDiscountTotal,
    string? PromoCodeText,
    decimal PromoDiscountAmount,
    decimal DeliveryFeeAmount,
    decimal DeliveryDiscountAmount,
    decimal GrandTotal,
    decimal RefundedAmount);

/// <summary>One line of the order, as it was bought.</summary>
/// <param name="LineNumber">1-based position within the order.</param>
/// <param name="Sku">The variant's SKU, which is what a picker reads off the shelf.</param>
/// <param name="ProductName">The name snapshotted at placement.</param>
/// <param name="ProductCode">The product code snapshotted at placement.</param>
/// <param name="ColourName">The colour snapshotted at placement.</param>
/// <param name="SizeLabel">The size snapshotted at placement.</param>
/// <param name="Quantity">How many.</param>
/// <param name="UnitOriginalPrice">The pre-markdown price.</param>
/// <param name="UnitListPrice">The listed price.</param>
/// <param name="UnitPrice">What was actually charged per unit.</param>
/// <param name="LineSubtotal">Unit price × quantity.</param>
/// <param name="PromoDiscountAllocated">This line's share of the order's promo discount.</param>
/// <param name="LineTotal">Subtotal − allocated promo discount.</param>
/// <param name="RefundedQuantity">How many units have been refunded. v1 refunds whole payments (D6).</param>
public sealed record AdminOrderLineDto(
    short LineNumber,
    string Sku,
    string ProductName,
    string ProductCode,
    string ColourName,
    string SizeLabel,
    short Quantity,
    decimal UnitOriginalPrice,
    decimal UnitListPrice,
    decimal UnitPrice,
    decimal LineSubtotal,
    decimal PromoDiscountAllocated,
    decimal LineTotal,
    short RefundedQuantity);

/// <summary>
/// One attempt to pay (doc 08 §3.5, §4.3).
/// </summary>
/// <remarks>
/// <b>Several rows per order are normal</b>, not an error state — a rejected bank transfer closes its row
/// and a resubmission opens a new one — which is exactly why the detail screen has to show all of them.
/// A customer who says "I sent it twice" is asking about this list.
/// </remarks>
/// <param name="ProviderCode">Which provider.</param>
/// <param name="Status">The payment status name (doc 08 §4.3).</param>
/// <param name="IsOrderSettlement">Whether this is the one row that settles the order.</param>
/// <param name="Amount">LKR, equal to the order's grand total.</param>
/// <param name="Currency">
/// <c>LKR</c>, and <c>CK_Payments_Currency</c> pins it there. It travels anyway so no screen hard-codes a
/// symbol beside a figure — which is a defect this screen shipped and the tester caught.
/// </param>
/// <param name="ProviderAmount">
/// What the provider was actually asked for, in <paramref name="ProviderCurrency"/>, or
/// <see langword="null"/>.
/// <para>
/// <b>This is the field that genuinely differs from LKR, and it reached no screen.</b> Doc 08 §3.5 makes
/// <c>Currency</c> LKR by check constraint and puts the foreign figure here — <c>USD</c> for PayPal (D1),
/// <see langword="null"/> for a bank transfer — and doc 11 P10 step 6 tells staff to read a refund's
/// amount out to the customer as "USD 53.55". The refund decision is made on this number, so the screen
/// that will carry that decision has to show it.
/// </para>
/// <para>
/// Null for every payment this build can create, because <c>BankTransferProvider</c> is the only provider
/// and a bank transfer is taken in rupees. The screen renders it only when it is there, so today nothing
/// appears and the day W4-01 ships it does — which is the opposite of a field nothing reads.
/// </para>
/// </param>
/// <param name="ProviderCurrency"><c>USD</c> for PayPal, <see langword="null"/> for a bank transfer.</param>
/// <param name="PayerReference">The bank reference the customer typed, or the PayPal payer id.</param>
/// <param name="SlipPublicId">
/// The uploaded slip's token, or <see langword="null"/>. It is a <b>private</b> asset and this token only
/// opens through the order-keyed endpoint, never through <c>/media</c>.
/// </param>
/// <param name="SlipContentType">So the screen knows whether to draw an image or offer a PDF.</param>
/// <param name="SlipFileName">What the customer called the file.</param>
/// <param name="FailureCode">Why it failed, e.g. <c>REJECTED</c>.</param>
/// <param name="FailureMessage">The message, where one was recorded.</param>
/// <param name="ApprovedByUserEmail">
/// Who approved or rejected it (doc 11 P7 step 6). An email rather than an id, because an id on a screen
/// is a dead end — the lesson <c>ShortStockLine</c> records about naming rather than numbering.
/// </param>
/// <param name="ApprovedAtUtc">When they did.</param>
/// <param name="ApprovalNote">Their reason, which is what staff read back if the customer asks.</param>
/// <param name="CreatedAtUtc">When the attempt was opened.</param>
/// <param name="CompletedAtUtc">When it reached a terminal state.</param>
public sealed record AdminOrderPaymentDto(
    string ProviderCode,
    string Status,
    bool IsOrderSettlement,
    decimal Amount,
    string Currency,
    decimal? ProviderAmount,
    string? ProviderCurrency,
    string? PayerReference,
    Guid? SlipPublicId,
    string? SlipContentType,
    string? SlipFileName,
    string? FailureCode,
    string? FailureMessage,
    string? ApprovedByUserEmail,
    DateTime? ApprovedAtUtc,
    string? ApprovalNote,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>One refund recorded against the order (doc 08 §3.5).</summary>
/// <param name="Amount">LKR that went back.</param>
/// <param name="Currency"><c>LKR</c>, pinned by <c>CK_Refunds_Currency</c>. See the payment's.</param>
/// <param name="ProviderAmount">
/// The foreign amount that actually went back (doc 08 §3.5: "the USD that actually went back for a PayPal
/// refund, D1"), or <see langword="null"/>. Doc 11 §6.6 step 2 has staff reading a refund's amount, date
/// and reference out to a customer; this is the amount for a PayPal one.
/// </param>
/// <param name="Method">How the money physically travelled.</param>
/// <param name="Status">Pending, Completed or Failed.</param>
/// <param name="Reason">
/// Mandatory on every refund (doc 08 §4.1 G-13), and for a manual bank transfer it also carries the
/// outgoing transfer's bank reference.
/// </param>
/// <param name="RestockItems">Whether the goods were put back on the shelf.</param>
/// <param name="CreatedByUserEmail">
/// Who refunded, or <see langword="null"/> for a refund made outside the system and learnt from a webhook
/// (G-23). The nullability is load-bearing and is not a missing lookup.
/// </param>
/// <param name="CreatedAtUtc">When the row was written.</param>
/// <param name="CompletedAtUtc">When the money actually went.</param>
public sealed record AdminOrderRefundDto(
    decimal Amount,
    string Currency,
    decimal? ProviderAmount,
    string Method,
    string Status,
    string Reason,
    bool RestockItems,
    string? CreatedByUserEmail,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>
/// One entry in the order's own timeline (doc 08 §3.5 <c>OrderStatusHistory</c>).
/// </summary>
/// <remarks>
/// <b>An entry whose <paramref name="FromStatus"/> equals its <paramref name="ToStatus"/> is an event that
/// did not move the order</b>, and the note says which — a rejected bank transfer is the documented case.
/// The convention is written down on the entity; it is repeated here because a screen that assumed every
/// row was a transition would render "Awaiting payment → Awaiting payment" and look broken.
/// </remarks>
/// <param name="FromStatus">
/// Where it came from, or <see langword="null"/> for the first row: placement has no previous status.
/// </param>
/// <param name="ToStatus">Where it went.</param>
/// <param name="FromStatusLabel">The reading wording for <paramref name="FromStatus"/>, or <see langword="null"/>.</param>
/// <param name="ToStatusLabel">The reading wording for <paramref name="ToStatus"/>.</param>
/// <param name="Source">
/// <c>Customer</c>, <c>Staff</c>, <c>SystemJob</c> or <c>PaymentProvider</c>. Doc 08 §4: "System" carries
/// no staff permission, which is why who moved an order is part of its history and not an audit lookup.
/// </param>
/// <param name="ChangedByUserEmail">
/// Who did it, or <see langword="null"/> for a job or a provider event. <b>Null is not a failed
/// lookup</b>: doc 08 writes a history row for every System transition precisely so the timeline is
/// complete, and a screen must say "by the system" rather than leave a gap that looks like missing data.
/// </param>
/// <param name="Note">Free text, e.g. doc 08 §4.1's "late payment accepted".</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record AdminOrderHistoryDto(
    string? FromStatus,
    string ToStatus,
    string? FromStatusLabel,
    string ToStatusLabel,
    string Source,
    string? ChangedByUserEmail,
    string? Note,
    DateTime OccurredAtUtc);

/// <summary>The dispatch, once there is one.</summary>
/// <param name="CourierId">
/// Which courier row the order points at, or <see langword="null"/> when that row has been deleted.
/// <para>
/// <b>It is here so the correction form can select the right courier without matching on the name.</b>
/// The first version matched <see cref="CourierName"/> against the live list, which worked until somebody
/// renamed a carrier — and then the form came up with nothing selected on an order that plainly had a
/// courier, inviting a staff member to pick whichever one looked right. GLB-11 keeps internal ids out of
/// <i>URLs</i>, not out of bodies, and <see cref="AdminCourierDto.Id"/> already travels the same way.
/// </para>
/// </param>
/// <param name="CourierName">
/// The <b>snapshot</b> taken when the order shipped (doc 11 P9: "courier name is copied into the order").
/// A renamed or deleted courier does not rewrite a past dispatch note, which is why this is the field the
/// screen <i>displays</i> and <see cref="CourierId"/> is only the one it selects by.
/// </param>
/// <param name="TrackingNumber">What the courier gave.</param>
/// <param name="TrackingUrl">
/// The tracking link, built from the courier's <b>current</b> template, or <see langword="null"/> when the
/// courier has no online tracking or has since been deleted.
/// <para>
/// Doc 08 §3.5 is explicit that the order keeps the name and the number and never a built URL, so editing
/// a template changes the link on older orders too. That is the documented behaviour, not a side effect.
/// </para>
/// </param>
/// <param name="ShippedAtUtc">When it went.</param>
public sealed record AdminOrderDispatchDto(
    int? CourierId,
    string CourierName,
    string TrackingNumber,
    string? TrackingUrl,
    DateTime ShippedAtUtc);

/// <summary>
/// Which transitions the order can take, as the server sees it.
/// </summary>
/// <remarks>
/// <b>It is a convenience for the screen and never the security boundary.</b> Every flag here is
/// re-decided inside the transition's own conditional UPDATE (doc 08 §4.1), so a client that ignored all
/// of them would simply be refused with the current state. What the flags buy is a screen that offers the
/// action that will work rather than four buttons and a shrug — and, for the two blocked cases below, a
/// sentence saying what to go and do instead of a dead control with no explanation.
/// </remarks>
/// <param name="CanStartProcessing">Paid → Processing (doc 11 P9 step 2).</param>
/// <param name="CanMarkShipped">
/// Processing → Shipped for a delivery order, which needs a courier <b>and</b> a tracking number
/// (doc 08 §4.1). False for a pickup order, which takes <paramref name="CanMarkReadyForCollection"/>.
/// </param>
/// <param name="CanMarkReadyForCollection">
/// Processing → Shipped for a pickup order, which needs neither (doc 11 P9's pickup variant).
/// </param>
/// <param name="CanMarkDelivered">Shipped → Delivered, or "Mark as collected" for a pickup order.</param>
/// <param name="CanEditDispatch">
/// Whether the courier and the tracking number may still be corrected. Doc 11 P9: after placement only
/// those two and the internal note are editable, and a wrong tracking number is corrected on the order
/// rather than reversed.
/// </param>
/// <param name="CanAcceptLatePayment">
/// Whether this order is <c>Expired</c> or <c>Cancelled</c> with a bank payment that could still settle it
/// (doc 08 §4.1's <c>Expired → Paid</c>, doc 11 P7 step 8).
/// <para>
/// <b>This flag is issue #124.</b> The bank queue lists <c>Pending</c> payments and expiry closes the
/// payment in the same transaction as the order, so an expired order is not in that queue at all — and a
/// staff member who got an <c>order_expired</c> refusal there and pressed Refresh lost the only route to
/// the transaction for good. The order detail, reached by searching the number the customer quotes, is the
/// route that cannot be destroyed by a reload.
/// </para>
/// <para>
/// <b>A bank payment in <c>Cancelled</c> counts, which W3-04b's first version missed.</b> Cancelling an
/// unpaid order closes its open payment to <c>Cancelled</c> (doc 08 §4.3), so after a cancel the order's
/// only bank row is in that status — and doc 08 §4.1's <c>Cancelled → Paid</c> edge would have had no
/// control anywhere, leaving a shop that cancelled an order unable to accept the transfer that arrived
/// the next day. <c>AcceptLateAsync</c> already accepted it: the flag was the only thing in the way.
/// </para>
/// </param>
/// <param name="CanCancel">
/// Whether this <b>unpaid</b> order can be cancelled right now (doc 11 P10 flow A, W3-04b).
/// <para>
/// <b>It is doc 11 P10 step 2's four guards, not just the status</b>: <c>PendingPayment</c>, stock
/// <c>Reserved</c>, no active capture lock, and no payment that has already settled the order — plus doc
/// 08 §4.1 R1's PayPal <c>PENDING</c> rule, which gives that one decision to <c>Orders.Approve</c> alone
/// and so takes it away from the <c>Orders.Edit</c> holder this screen is for.
/// </para>
/// <para>
/// <b>False on a paid order, and the screen must not offer an alternative.</b> Doc 11 P10's table says a
/// paid order is refunded and never cancelled, and <c>Orders.Refund</c> is unbuilt (W3-05) — refunds are
/// manual for v1 and recorded in the order's internal note. So the honest screen offers cancel on an
/// unpaid order and nothing at all on a paid one.
/// </para>
/// </param>
/// <param name="CouriersConfigured">
/// Whether any active courier exists at all.
/// <para>
/// <b>False is the normal state of a new installation and the screen has to say so in words.</b>
/// <c>sales.Couriers</c> is deliberately unseeded — which couriers the client uses is their information —
/// so with none configured a delivery order genuinely cannot ship. The honest screen says "add a courier
/// in Settings first"; a disabled button with no explanation is the shape of defect this project has
/// already paid for.
/// </para>
/// </param>
/// <param name="BlockedReason">
/// Why the obvious next action is unavailable, in a sentence a staff member can act on, or
/// <see langword="null"/> when nothing is blocked.
/// </param>
public sealed record AdminOrderActionsDto(
    bool CanStartProcessing,
    bool CanMarkShipped,
    bool CanMarkReadyForCollection,
    bool CanMarkDelivered,
    bool CanEditDispatch,
    bool CanAcceptLatePayment,
    bool CanCancel,
    bool CouriersConfigured,
    string? BlockedReason);
