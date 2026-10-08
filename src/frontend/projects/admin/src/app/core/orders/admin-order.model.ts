/**
 * `/api/admin/orders` and `/api/admin/couriers` (ADM-09, doc 11 P9).
 *
 * A hand-written copy of `SpencerDenim.Application.Sales.Orders.Contracts`. The copy is a hazard rather
 * than a convenience — W1-12's blocker was a client model that had never listed three fields, and #126
 * records that a dropped payload field left all 860 browser tests green because Playwright never starts
 * the .NET API — so `AdminOrderEndpointTests` asserts every field of a real row against real SQL Server
 * rather than leaving the pairing to review.
 */

/** The eight order statuses of doc 08 §4.1. The names, never the numbers: see `AdminOrderListRow.status`. */
export type OrderStatusName =
  | 'PendingPayment'
  | 'Paid'
  | 'Processing'
  | 'Shipped'
  | 'Delivered'
  | 'Cancelled'
  | 'Expired'
  | 'Refunded';

/**
 * The statuses the list offers as tabs, in the order a staff member works them.
 *
 * **Paid first, and that is doc 11 P9 step 1 rather than alphabetical order**: the paid orders nobody has
 * started are the queue, and everything else is reference. `PendingPayment` is near the end because its
 * own screen is the bank-transfer queue, which has the deadline countdown on it.
 */
export const ORDER_STATUS_TABS: readonly OrderStatusName[] = [
  'Paid',
  'Processing',
  'Shipped',
  'Delivered',
  'PendingPayment',
  'Expired',
  'Cancelled',
  'Refunded',
];

/** Which end of the list comes first. The value is the query-string value. */
export type AdminOrderSort = 'Newest' | 'Oldest';

/** One row of the order list. */
export interface AdminOrderListRow {
  /**
   * The order's public token. Every action and link uses it; the internal id never leaves the server
   * (GLB-11), and this is the same token the shopper's own order link carries.
   */
  readonly orderPublicId: string;

  /** What the customer quotes on the telephone, which is why the search matches it first. */
  readonly orderNumber: string;

  /** The status name. A client must not branch on a number — see the C# contract. */
  readonly status: OrderStatusName;

  /**
   * What to show, which is **not always the status name**: a pickup order in `Shipped` reads as "Ready for
   * collection" and in `Delivered` as "Collected" (doc 11 P9's pickup variant). Rendered on the server so
   * the list and the detail cannot disagree about one order's wording.
   */
  readonly statusLabel: string;

  readonly customerName: string;
  readonly customerEmail: string;
  readonly grandTotal: number;
  readonly currency: string;
  readonly paymentProviderCode: string;
  readonly deliveryMethodName: string;

  /** Whether this is a store pickup order. It changes the wording and the available actions. */
  readonly isPickup: boolean;

  readonly courierName: string | null;
  readonly trackingNumber: string | null;
  readonly invoiceNumber: string | null;
  readonly placedAtUtc: string;
  readonly paidAtUtc: string | null;

  /**
   * Time left before the payment deadline, from the server's clock, negative once it has passed — and
   * `null` for every status but `PendingPayment`, because a paid or closed order has no deadline left.
   *
   * Not computed in the browser from a due date, because a shop computer with a wrong clock would then
   * show a list whose deadlines are all wrong with nothing on the page saying so.
   */
  readonly secondsUntilExpiry: number | null;
}

export interface AdminOrderListPage {
  readonly rows: readonly AdminOrderListRow[];
  readonly page: number;
  readonly pageSize: number;

  /** Every order matching the filter, not only this page's. */
  readonly totalCount: number;

  /**
   * Which order the rows are actually in.
   *
   * **Echoed by the server rather than assumed by the screen**, because the server chooses it: the Paid
   * filter defaults to oldest first (doc 11 P9 step 1). A screen that drew its own default would be
   * labelling the list wrongly on exactly the tab that matters.
   */
  readonly sort: AdminOrderSort;

  /**
   * How many orders sit in each status, over the whole table — **ignoring the search term and the
   * filter**, so a tab's count does not change when somebody types in the search box.
   */
  readonly statusCounts: Readonly<Record<string, number>>;
}

/** Who placed the order. */
export interface AdminOrderCustomer {
  readonly name: string;
  readonly email: string;

  /** `null` after a PDPA removal, which is a real state — see `isAnonymised`. */
  readonly phone: string | null;
  readonly hasAccount: boolean;

  /**
   * Whether a PDPA removal has overwritten the personal columns (doc 08 §3.1 N5). The screen says so,
   * rather than presenting blanks as missing data.
   */
  readonly isAnonymised: boolean;
}

/** Where the order is going. Every address field is null throughout for a pickup order. */
export interface AdminOrderDelivery {
  readonly methodCode: string;
  readonly methodName: string;
  readonly isPickup: boolean;
  readonly pickupLocationName: string | null;
  readonly districtName: string;
  readonly zone: string;
  readonly estimatedDaysMin: number;
  readonly estimatedDaysMax: number;
  readonly recipientName: string | null;
  readonly recipientPhone: string | null;
  readonly line1: string | null;
  readonly line2: string | null;
  readonly city: string | null;
  readonly postalCode: string | null;
  readonly landmark: string | null;
}

/**
 * The money, exactly as the order holds it.
 *
 * **Nothing here is recomputed in the browser.** `CK_Orders_Totals` proves the stored set adds up, so a
 * screen that re-derived the grand total from the lines would be a second arithmetic that could disagree
 * with what the customer was charged — and with the invoice.
 */
export interface AdminOrderMoney {
  readonly currency: string;
  readonly itemsOriginalTotal: number;
  readonly itemsListTotal: number;
  readonly itemsSubtotal: number;
  readonly markdownDiscountTotal: number;
  readonly campaignDiscountTotal: number;
  readonly promoCodeText: string | null;
  readonly promoDiscountAmount: number;
  readonly deliveryFeeAmount: number;
  readonly deliveryDiscountAmount: number;
  readonly grandTotal: number;
  readonly refundedAmount: number;
}

/** One line of the order, as it was bought. Every descriptive field is a placement-time snapshot. */
export interface AdminOrderLine {
  readonly lineNumber: number;
  readonly sku: string;
  readonly productName: string;
  readonly productCode: string;
  readonly colourName: string;
  readonly sizeLabel: string;
  readonly quantity: number;
  readonly unitOriginalPrice: number;
  readonly unitListPrice: number;
  readonly unitPrice: number;
  readonly lineSubtotal: number;
  readonly promoDiscountAllocated: number;
  readonly lineTotal: number;
  readonly refundedQuantity: number;
}

/** One attempt to pay. Several rows per order are normal, not an error state (doc 08 §3.5). */
export interface AdminOrderPayment {
  readonly providerCode: string;
  readonly status: string;
  readonly isOrderSettlement: boolean;
  readonly amount: number;

  /** `LKR`, pinned by `CK_Payments_Currency`. Carried so no screen hard-codes a symbol beside a figure. */
  readonly currency: string;

  /**
   * What the provider was actually asked for, in `providerCurrency`, or null.
   *
   * **This is the field that genuinely differs from LKR.** `currency` is LKR by check constraint; doc 08
   * §3.5 puts the foreign figure here (USD for PayPal, D1) and doc 11 P10 step 6 has staff reading it out
   * as "USD 53.55". Null for every payment this build can create, so the screen shows it only when set.
   */
  readonly providerAmount: number | null;
  readonly providerCurrency: string | null;

  readonly payerReference: string | null;

  /** The slip's token, or null. It identifies the file; the download route is keyed on the order. */
  readonly slipPublicId: string | null;
  readonly slipContentType: string | null;
  readonly slipFileName: string | null;
  readonly failureCode: string | null;
  readonly failureMessage: string | null;
  readonly approvedByUserEmail: string | null;
  readonly approvedAtUtc: string | null;
  readonly approvalNote: string | null;
  readonly createdAtUtc: string;
  readonly completedAtUtc: string | null;
}

/** One refund recorded against the order. */
export interface AdminOrderRefund {
  readonly amount: number;
  readonly currency: string;

  /** The foreign amount that actually went back (doc 08 §3.5, D1), or null. */
  readonly providerAmount: number | null;

  readonly method: string;
  readonly status: string;
  readonly reason: string;
  readonly restockItems: boolean;

  /** Null for a refund made outside the system and learnt from a webhook (G-23), not a missing lookup. */
  readonly createdByUserEmail: string | null;
  readonly createdAtUtc: string;
  readonly completedAtUtc: string | null;
}

/**
 * One entry in the order's timeline.
 *
 * **An entry whose `fromStatus` equals its `toStatus` is an event that did not move the order**, and the
 * note says which — a rejected bank transfer and a dispatch correction are the two. A screen that assumed
 * every row was a transition would render "Awaiting payment → Awaiting payment" and look broken.
 */
export interface AdminOrderHistoryEntry {
  /** Null on the first row: placement has no previous status. */
  readonly fromStatus: string | null;
  readonly toStatus: string;
  readonly fromStatusLabel: string | null;
  readonly toStatusLabel: string;

  /** `Customer`, `Staff`, `SystemJob` or `PaymentProvider`. */
  readonly source: string;

  /** Null for a job or a provider event. Not a failed lookup — the screen says "by the system". */
  readonly changedByUserEmail: string | null;
  readonly note: string | null;
  readonly occurredAtUtc: string;
}

/** The dispatch, once there is one. */
export interface AdminOrderDispatch {
  /**
   * Which courier row the order points at, or null when that row has been deleted.
   *
   * **The correction form selects by this, not by the name.** Matching on the name worked until somebody
   * renamed a carrier, and then the form came up with nothing selected on an order that plainly had a
   * courier — inviting a staff member to pick whichever one looked right.
   */
  readonly courierId: number | null;

  /** The snapshot taken when the order shipped, so a rename does not rewrite a past dispatch note. */
  readonly courierName: string;
  readonly trackingNumber: string;

  /**
   * Built from the courier's **current** template, or null when the courier has no online tracking or has
   * been deleted. Doc 08 §3.5 is explicit that editing a template changes the link on older orders too.
   */
  readonly trackingUrl: string | null;
  readonly shippedAtUtc: string;
}

/**
 * Which transitions the order can take right now.
 *
 * **A convenience, never the boundary**: every flag is re-decided inside the transition's own conditional
 * UPDATE, so a client that ignored them all would simply be refused with the current state. What they buy
 * is a screen that offers the action that will work — and `blockedReason`, which is the sentence that
 * stops the screen offering a dead control with no explanation.
 */
export interface AdminOrderActions {
  readonly canStartProcessing: boolean;
  readonly canMarkShipped: boolean;
  readonly canMarkReadyForCollection: boolean;
  readonly canMarkDelivered: boolean;
  readonly canEditDispatch: boolean;

  /**
   * Whether this expired or cancelled order still has a bank payment that could settle it.
   *
   * **This flag is issue #124.** The bank queue lists `Pending` payments and expiry closes the payment in
   * the same transaction as the order, so an expired order is not in that queue at all — and a staff
   * member who got an `order_expired` refusal there and pressed Refresh lost the only route to the
   * transaction for good. Searching the order number the customer quotes reaches this screen, and a
   * reload cannot destroy it.
   */
  readonly canAcceptLatePayment: boolean;

  /**
   * Whether this **unpaid** order can be cancelled right now (doc 11 P10 flow A).
   *
   * **Doc 11 P10 step 2's four guards, not just the status**: `PendingPayment`, stock `Reserved`, no
   * active capture lock, and no payment that has already settled the order — plus doc 08 §4.1 R1's PayPal
   * `PENDING` rule, which gives that one case to `Orders.Approve` alone and so takes it away from the
   * `Orders.Edit` holder this screen is for.
   *
   * **False on a paid order, and the screen offers no alternative.** Doc 11 P10's table says a paid order
   * is refunded and never cancelled; `Orders.Refund` is unbuilt (W3-05) and refunds are manual for v1,
   * recorded in the order's internal note.
   */
  readonly canCancel: boolean;

  /**
   * Whether any active courier exists.
   *
   * **False is the normal state of a new installation.** `sales.Couriers` is deliberately unseeded, so a
   * delivery order genuinely cannot ship until the client says which carriers they use — and the screen
   * says that in words rather than showing a disabled button.
   */
  readonly couriersConfigured: boolean;

  /** Why the obvious next action is unavailable, in a sentence a staff member can act on. */
  readonly blockedReason: string | null;
}

/** One order in full. */
export interface AdminOrderDetail {
  readonly orderPublicId: string;
  readonly orderNumber: string;
  readonly status: OrderStatusName;
  readonly statusLabel: string;

  /** `Reserved`, `Committed`, `Released` or `Restocked` — the other half of every guard in doc 08 §4.1. */
  readonly stockState: string;
  readonly customer: AdminOrderCustomer;
  readonly delivery: AdminOrderDelivery;
  readonly money: AdminOrderMoney;
  readonly lines: readonly AdminOrderLine[];
  readonly payments: readonly AdminOrderPayment[];
  readonly refunds: readonly AdminOrderRefund[];
  readonly history: readonly AdminOrderHistoryEntry[];
  readonly dispatch: AdminOrderDispatch | null;
  readonly actions: AdminOrderActions;
  readonly customerNote: string | null;
  readonly internalNote: string | null;
  readonly cancelReason: string | null;
  readonly invoiceNumber: string | null;
  readonly invoiceIssuedAtUtc: string | null;
  readonly placedAtUtc: string;
  readonly paymentDueAtUtc: string;
  readonly secondsUntilExpiry: number | null;
  readonly paidAtUtc: string | null;
  readonly shippedAtUtc: string | null;
  readonly deliveredAtUtc: string | null;
  readonly closedAtUtc: string | null;
}

/**
 * The seller's legal details on an invoice.
 *
 * **Every field may be null and an unset field prints nothing.** These values are the client's, and this
 * project has twice shipped invented client data as a Major finding. An invoice is a legal document: an
 * invented registered address on one is worse than a blank somebody can see and ask about.
 */
export interface InvoiceSeller {
  readonly legalName: string | null;

  /** Already split into lines, and empty when unset — so a renderer loops over nothing. */
  readonly addressLines: readonly string[];
  readonly brNumber: string | null;
  readonly vatNumber: string | null;

  /**
   * Whether the legal name and the address are both filled in — the two a printed invoice is unusable
   * without. The registration numbers are not in the test, because a company with no VAT registration has
   * nothing to print there.
   */
  readonly isComplete: boolean;
}

/** One order's invoice and packing copy. */
export interface OrderInvoice {
  readonly invoiceNumber: string;
  readonly issuedAtUtc: string;
  readonly orderNumber: string;
  readonly orderPublicId: string;
  readonly placedAtUtc: string;
  readonly seller: InvoiceSeller;
  readonly customer: AdminOrderCustomer;
  readonly delivery: AdminOrderDelivery;
  readonly money: AdminOrderMoney;
  readonly lines: readonly AdminOrderLine[];
  readonly paymentMethodLabel: string;
  readonly paymentReference: string | null;
}

/** One courier. */
export interface AdminCourier {
  readonly id: number;
  readonly name: string;

  /** Contains `{trackingNumber}`, or null for a courier with no online tracking. */
  readonly trackingUrlTemplate: string | null;
  readonly phone: string | null;
  readonly isActive: boolean;
  readonly sortOrder: number;

  /** How many orders were ever dispatched with this courier — the figure to read before deactivating one. */
  readonly ordersShipped: number;
}

/** The placeholder a tracking address must contain. The one example this screen is allowed to show. */
export const TRACKING_NUMBER_PLACEHOLDER = '{trackingNumber}';

/** Which transition a request is. The value is the URL segment. */
export type OrderTransitionSegment =
  | 'start-processing'
  | 'mark-shipped'
  | 'ready-for-collection'
  | 'mark-delivered'
  | 'dispatch'
  | 'internal-note'
  | 'cancel';

/** What a transition answered. */
export interface OrderTransitionResult {
  readonly orderNumber: string;
  readonly status: OrderStatusName;
  readonly statusLabel: string;
}

/**
 * Why a transition was refused, as the API's problem document carries it.
 *
 * **The code is the point, not the message**, and the status matters as much: a screen refused with no
 * state went back to offering an action that could never succeed, which is the defect W3-03's `ShortStock`
 * records. With the status in hand the screen can redraw from the refusal instead of guessing.
 */
export interface OrderTransitionRefusal {
  readonly code: string;
  readonly message: string;
  readonly orderStatus: string | null;
  readonly orderStatusLabel: string | null;
}

/** The refusal codes the screen branches on. */
export const ORDER_TRANSITION_REFUSALS = {
  /** The order is not in the status the transition starts from. The screen re-reads and redraws. */
  wrongStatus: 'order_wrong_status',

  /** The chosen courier has been deactivated since the form was drawn. */
  courierUnavailable: 'courier_unavailable',
} as const;
