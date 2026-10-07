/**
 * `GET /api/admin/orders/bank-transfers` (PAY-01, ADM-09, doc 11 P7 step 4).
 *
 * A hand-written copy of `SpencerDenim.Application.Sales.Payments.Contracts.BankTransferQueuePage`. The
 * copy is a hazard rather than a convenience — W1-12's blocker was a client model that had never listed
 * three fields — so `AdminBankTransferEndpointTests` asserts every field of a real row rather than leaving
 * the pairing to review.
 */

/** One transfer awaiting approval. Doc 11 P7 step 4 lists exactly these. */
export interface BankTransferQueueRow {
  /**
   * The order's public token. Every action and link uses it; the internal id never leaves the server
   * (GLB-11 keeps ids out of URLs, and this is the same token the shopper's own order link carries).
   */
  readonly orderPublicId: string;

  /** Also the reference the customer was told to quote. */
  readonly orderNumber: string;
  readonly customerName: string;
  readonly customerEmail: string;
  readonly amountLkr: number;
  readonly currency: string;

  /** What the customer typed, or null. P7's exceptions cover matching by amount, name and date instead. */
  readonly payerReference: string | null;

  /** The slip's token, or null. It identifies the file; the download route is keyed on the order. */
  readonly slipAssetPublicId: string | null;

  /** So the row knows whether to offer "View photo" or "Open PDF" without fetching the file. */
  readonly slipContentType: string | null;

  readonly placedAtUtc: string;
  readonly paymentDueAtUtc: string;

  /**
   * Time left, **from the server's clock**, and negative once the deadline has passed.
   *
   * Not rendered server-side, because a sentence is stale the moment it is serialised; not computed in the
   * browser from `paymentDueAtUtc`, because a shop computer with a wrong clock would then show a queue
   * whose deadlines are all wrong and nothing on the page would say so. Negative rather than clamped,
   * because "overdue by eleven minutes" and "due now" are different facts to somebody deciding whether to
   * approve.
   */
  readonly secondsUntilExpiry: number;
}

export interface BankTransferQueuePage {
  readonly rows: readonly BankTransferQueueRow[];
  readonly page: number;
  readonly pageSize: number;

  /** Every transfer awaiting approval, not only this page's — the figure that says if the queue is under control. */
  readonly totalCount: number;
}

/** What a decision answers. */
export interface BankTransferDecisionResult {
  readonly orderNumber: string;

  /** The gapless invoice number issued inside the transaction, or null for a rejection. */
  readonly invoiceNumber: string | null;
}

/** Which of doc 11 P7's three staff decisions. The value is the URL segment. */
export type BankTransferDecision = 'approve' | 'reject' | 'accept-late';

/**
 * Why a decision was refused, as the API's problem document carries it.
 *
 * **The code is the point, not the message.** Doc 11 P7's business rule is that "if the expiry job or
 * another staff member acted first, the approver is told and the screen reloads" — and what the screen has
 * to do differs by code: `order_expired` means offer Accept late payment, everything else means reload and
 * look again.
 */
export interface BankTransferRefusal {
  readonly code: string;
  readonly message: string;
  readonly orderStatus: string | null;
  readonly paymentStatus: string | null;

  /** For a refused late acceptance: every line that could not be re-acquired, named. */
  readonly shortLines: readonly BankTransferShortLine[] | null;
}

/**
 * One line a late acceptance could not re-acquire.
 *
 * **Named rather than numbered.** The API used to send variant id to quantity available, and the screen
 * rendered "variant 470 (0 left)" — an internal id, to a staff member deciding whether to refund somebody,
 * on a screen with no order-detail page to resolve it in. `needed` matters as much as `available`: one of
 * two short is a problem and one of one is not.
 */
export interface BankTransferShortLine {
  readonly variantId: number;
  readonly sku: string;
  readonly productName: string;
  readonly colourName: string;
  readonly sizeLabel: string;
  readonly needed: number;
  readonly available: number;
}

/** The refusal codes the screen branches on. */
export const BANK_TRANSFER_REFUSALS = {
  orderExpired: 'order_expired',
  shortStock: 'short_stock',

  /**
   * Accept late payment was pressed on an order that is still open.
   *
   * Named here rather than left to the generic path because it is the code that says **expiry has not
   * won**: the order is still `PendingPayment` with its stock reserved, so Approve is the action and the
   * screen has to offer it again. A refusal that only the generic path knows about cannot move a button.
   */
  orderNotClosed: 'order_not_closed',
} as const;
