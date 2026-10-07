import { CartLineStatus } from '../cart/cart.model';

/**
 * Why checkout cannot be completed as it stands.
 *
 * Mirrors `SpencerDenim.Application.Sales.Checkout.Contracts.CheckoutBlocker`. The API sends the member
 * name, so this is a union of those names rather than numbers — the same contract `CartLineStatus`
 * travels under. The *wording* is this application's job: see `checkoutBlockerMessage`.
 */
export type CheckoutBlocker =
  | 'EmptyBag'
  | 'LineNotPurchasable'
  | 'NoDeliveryMethod'
  | 'NoPaymentMethod';

/** A district a delivery address can name (CHK-01). */
export interface DistrictDto {
  readonly id: number;
  readonly name: string;
  /** 1 Colombo, 2 Western, 3 Island-wide. Sent so a rate can be explained without a second request. */
  readonly deliveryZone: number;
}

/**
 * One delivery option as the chosen district's zone prices it (CHK-02).
 *
 * `fee` is **null** when the option is unavailable here, and null is not zero: "we do not know what this
 * would cost" and "this costs nothing" are different answers and the page draws them differently. Store
 * pickup genuinely costs nothing and sends `0`.
 */
export interface DeliveryOptionDto {
  readonly id: number;
  readonly code: string;
  readonly name: string;
  readonly description: string | null;
  readonly requiresAddress: boolean;
  readonly isAvailable: boolean;
  readonly fee: number | null;
  readonly freeAboveAmount: number | null;
  /** What this option would add to the total for the bag as it stands — the fee, or 0 once free. */
  readonly feeAfterDiscount: number | null;
  readonly estimatedDaysMin: number | null;
  readonly estimatedDaysMax: number | null;
}

/** A store to collect from (CHK-02). Empty until the client gives us an address. */
export interface PickupLocationDto {
  readonly id: number;
  readonly name: string;
  readonly addressLine: string;
  readonly city: string;
  readonly districtId: number;
  readonly phone: string | null;
  readonly openingHours: string | null;
}

/** An enabled payment provider (PAY-01/PAY-03). */
export interface PaymentMethodDto {
  readonly code: string;
  readonly displayName: string;
  readonly checkoutDescription: string | null;
}

/** One line as checkout shows it (CHK-01). */
export interface CheckoutLineDto {
  readonly variantId: number;
  readonly productId: number;
  readonly productName: string;
  readonly slug: string;
  readonly colourName: string;
  readonly sizeLabel: string;
  readonly imageUrl: string | null;
  readonly imageAltText: string | null;
  readonly quantity: number;
  readonly availableQuantity: number;
  readonly unitOriginalPrice: number;
  readonly unitPrice: number;
  readonly lineSubtotal: number;
  readonly status: CartLineStatus;
}

/**
 * The order summary's money ladder (CHK-01).
 *
 * **It has to add up on screen**, and the identity is
 * `itemsOriginalTotal − savedTotal − promoDiscountAmount + deliveryFeeAmount − deliveryDiscountAmount
 * = grandTotal`. The labels are the bag's, deliberately: Subtotal is the "was" total, the deduction row
 * is **Savings**, and Total is what is paid — settled when the bag shipped a ladder whose subtraction
 * was false.
 *
 * `deliveryFeeAmount` and `grandTotal` are **null until a delivery option is chosen**, and null is not
 * zero: doc 08 §6 step 10 needs a district and a method before a fee exists, and printing `0.00` would
 * be a promise of free delivery.
 */
export interface CheckoutTotalsDto {
  /** Σ quantity over **every** line, including the ones that cannot be bought. */
  readonly itemCount: number;
  /**
   * Σ quantity over the lines the subtotal sums — the bag's `buyableItemCount`, and the figure the
   * Subtotal label prints. It used to print `itemCount`, so a bag holding one deactivated line said
   * "Subtotal (3 items)" above a sum of two.
   */
  readonly buyableItemCount: number;
  readonly lineCount: number;
  readonly itemsOriginalTotal: number;
  readonly itemsSubtotal: number;
  readonly savedTotal: number;
  readonly promoDiscountAmount: number;
  readonly deliveryFeeAmount: number | null;
  readonly deliveryDiscountAmount: number;
  readonly grandTotal: number | null;
  readonly promoDiscountClamped: boolean;
  readonly currency: string;
}

/** Everything the checkout page needs for one visitor (CHK-01..03). */
export interface CheckoutSessionDto {
  /**
   * The idempotency token for this attempt. Echoed back on Place order so a double-clicked button or a
   * retried request produces one order, never two with two stock reservations.
   */
  readonly checkoutKey: string;
  readonly lines: readonly CheckoutLineDto[];
  readonly totals: CheckoutTotalsDto;
  readonly districts: readonly DistrictDto[];
  readonly deliveryOptions: readonly DeliveryOptionDto[];
  readonly pickupLocations: readonly PickupLocationDto[];
  readonly paymentMethods: readonly PaymentMethodDto[];
  readonly selectedDistrictId: number | null;
  readonly selectedDeliveryMethodId: number | null;
  readonly blockers: readonly CheckoutBlocker[];
}

/** One line of a placed order (CHK-04), read back from the order's own snapshot columns. */
export interface OrderLineDto {
  readonly lineNumber: number;
  readonly productName: string;
  readonly productCode: string;
  readonly sku: string;
  readonly colourName: string;
  readonly sizeLabel: string;
  readonly imageUrl: string | null;
  readonly quantity: number;
  readonly unitOriginalPrice: number;
  readonly unitPrice: number;
  readonly lineSubtotal: number;
  readonly promoDiscountAllocated: number;
  readonly lineTotal: number;
}

/** Where a placed order is going (CHK-04). */
export interface OrderDeliveryDto {
  readonly methodCode: string;
  readonly methodName: string;
  readonly pickupLocationName: string | null;
  readonly recipientName: string | null;
  readonly phone: string | null;
  readonly line1: string | null;
  readonly line2: string | null;
  readonly city: string | null;
  readonly districtName: string;
  readonly postalCode: string | null;
  readonly landmark: string | null;
  readonly estimatedDaysMin: number;
  readonly estimatedDaysMax: number;
}

/** A placed order (CHK-04). */
export interface OrderConfirmationDto {
  readonly orderNumber: string;
  readonly publicId: string;
  readonly status: string;
  readonly email: string;
  readonly firstName: string;
  readonly lastName: string | null;
  readonly phone: string | null;
  readonly lines: readonly OrderLineDto[];
  readonly delivery: OrderDeliveryDto;
  readonly itemsOriginalTotal: number;
  readonly itemsSubtotal: number;
  readonly savedTotal: number;
  readonly promoDiscountAmount: number;
  readonly deliveryFeeAmount: number;
  readonly deliveryDiscountAmount: number;
  readonly grandTotal: number;
  readonly currency: string;
  readonly paymentMethodCode: string;
  readonly paymentMethodName: string;
  readonly customerNote: string | null;
  readonly placedAtUtc: string;
  readonly paymentDueAtUtc: string;
  /** True when this response replayed an earlier identical submission rather than placing anything. */
  readonly isDuplicateSubmission: boolean;

  /**
   * How to pay, as the order's own payment provider describes it (PAY-03, doc 11 P7 step 1).
   *
   * **Null for three ordinary reasons, and the page has to tell them apart by looking at `status`**: the
   * order is no longer awaiting payment, so there is nothing to do; the provider does not describe itself;
   * or — the one worth knowing about — the shop has not filled its bank details in yet. Doc 11 P7 makes
   * "bank details set by a Super Admin" a precondition of the whole process, and the shop can be
   * deployed before they arrive.
   */
  readonly payment: OrderPaymentInstructionsDto | null;

  /** What the customer has already sent about this payment, or null when there is nothing to attach to. */
  readonly proof: OrderPaymentProofDto | null;
}

/** The customer-facing "how to pay" (doc 11 P7 step 1). */
export interface OrderPaymentInstructionsDto {
  readonly providerCode: string;

  /**
   * The reference the customer must quote — the order number, for a bank transfer.
   *
   * Its own field rather than something the page infers from `orderNumber`, because it is the only thing
   * tying an anonymous credit on a bank statement to an order, and a provider that identified the payment
   * itself would send null.
   */
  readonly reference: string | null;
  readonly amountLkr: number;
  readonly dueAtUtc: string;

  /** Bank, branch, account name, account number — in the order to print them. */
  readonly fields: readonly OrderPaymentFieldDto[];

  /** The shop's own extra note. **Plain text, never markup** — it is rendered as text. */
  readonly notes: string | null;

  /** Where to go to pay, for a provider that redirects. Always null for a bank transfer. */
  readonly redirectUrl: string | null;

  /** Whether this provider takes a customer-entered reference and a slip. */
  readonly acceptsProof: boolean;
}

/** One labelled payment detail. */
export interface OrderPaymentFieldDto {
  readonly label: string;
  readonly value: string;

  /** True for the values a shopper retypes into online banking, where a typo costs them money. */
  readonly copyable: boolean;
}

/** What the customer has already sent about their transfer (doc 11 P7 step 3). */
export interface OrderPaymentProofDto {
  readonly reference: string | null;

  /** The slip's token. It identifies the file; the download route is keyed on the order. */
  readonly slipPublicId: string | null;
  readonly slipFileName: string | null;
  readonly slipContentType: string | null;
  readonly slipUploadedAtUtc: string | null;
}

/** What `POST /api/orders/{token}/payment-slip` answers. */
export interface SlipUploadedDto {
  readonly slipPublicId: string;
  readonly fileName: string;
  readonly contentType: string;
  readonly uploadedAtUtc: string;
}

/** The delivery address a shopper typed. */
export interface CheckoutAddressInput {
  readonly recipientName: string | null;
  readonly phone: string | null;
  readonly line1: string | null;
  readonly line2: string | null;
  readonly city: string | null;
  readonly postalCode: string | null;
  readonly landmark: string | null;
}

/** Place order's body. */
export interface PlaceOrderRequest {
  readonly checkoutKey: string;
  readonly firstName: string | null;
  readonly lastName: string | null;
  readonly email: string | null;
  readonly phone: string | null;
  readonly deliveryMethodId: number | null;
  readonly districtId: number | null;
  readonly pickupLocationId: number | null;
  readonly address: CheckoutAddressInput | null;
  readonly paymentMethodCode: string | null;
  /** The total shown on the page. The order is refused if the freshly priced total differs (G-10). */
  readonly expectedGrandTotal: number | null;
  readonly customerNote: string | null;
}

/** A checkout page with nothing in it, so the page renders a form rather than nothing at all. */
export const EMPTY_CHECKOUT: CheckoutSessionDto = {
  checkoutKey: '',
  lines: [],
  totals: {
    itemCount: 0,
    buyableItemCount: 0,
    lineCount: 0,
    itemsOriginalTotal: 0,
    itemsSubtotal: 0,
    savedTotal: 0,
    promoDiscountAmount: 0,
    deliveryFeeAmount: null,
    deliveryDiscountAmount: 0,
    grandTotal: null,
    promoDiscountClamped: false,
    currency: 'LKR',
  },
  districts: [],
  deliveryOptions: [],
  pickupLocations: [],
  paymentMethods: [],
  selectedDistrictId: null,
  selectedDeliveryMethodId: null,
  blockers: ['EmptyBag'],
};

/**
 * What a blocker means to a shopper.
 *
 * The copy lives here rather than in the template so that there is one sentence per state and a unit
 * test can assert on it, and rather than on the server because it is wording, not a rule — the same
 * split `cartLineNotice` makes.
 */
export function checkoutBlockerMessage(blocker: CheckoutBlocker): string {
  switch (blocker) {
    case 'EmptyBag':
      return 'Your bag is empty, so there is nothing to check out.';
    case 'LineNotPurchasable':
      return 'Something in your bag cannot be bought right now. Go back to your bag to adjust it.';
    case 'NoDeliveryMethod':
      return 'We cannot deliver to that district at the moment. Choose another district.';
    case 'NoPaymentMethod':
      return 'No payment method is available at the moment. Please try again shortly.';
    default:
      return 'Checkout is not available at the moment.';
  }
}

/**
 * The delivery estimate in words, or null when the option has no rate.
 *
 * "1 day" rather than "1–1 days" when the two bounds are equal, because a range of one is not a range.
 */
export function deliveryEstimate(option: DeliveryOptionDto): string | null {
  const { estimatedDaysMin: min, estimatedDaysMax: max } = option;

  if (min === null || max === null) {
    return null;
  }

  return min === max ? `${min} ${min === 1 ? 'day' : 'days'}` : `${min}–${max} days`;
}

/**
 * Whether something looks enough like an email address to send an order to.
 *
 * **Deliberately the same permissive rule as the server's `PlaceOrderValidator.LooksLikeEmail`**: one
 * `@` with something either side, a dot in the domain, no whitespace. Two rules that disagree would mean
 * either a shopper blocked here from an address the shop would have accepted — the expensive direction —
 * or a 400 they could have been spared. Where they cannot be kept identical by sharing code, the client's
 * job is to be **no stricter** than the server, and that is what this comment is for: if the server's rule
 * is ever tightened, this one does not have to follow, but if it is loosened, this one must.
 */
export function looksLikeEmail(candidate: string): boolean {
  if (/\s/.test(candidate)) {
    return false;
  }

  const at = candidate.indexOf('@');

  if (at <= 0 || at !== candidate.lastIndexOf('@')) {
    return false;
  }

  const domain = candidate.slice(at + 1);
  const dot = domain.indexOf('.');

  return dot > 0 && dot < domain.length - 1;
}

/**
 * Whether a phone number is one somebody could dial.
 *
 * **Deliberately the same rule as the server's `PlaceOrderValidator.LooksLikePhone`**, and the same
 * contract as `looksLikeEmail` above: the client may be no stricter than the server. Separators people
 * type are ignored (`077 123 4567`, `011-2345678`, `(077) 1234567`, `+94 77 123 4567`), a `+` is allowed
 * only at the front, and what is left has to be nine to fifteen digits — nine is Sri Lanka's national
 * significant number, fifteen is E.164's maximum. `phone: "not-a-phone"` placed an order before this
 * existed, and the field is required precisely because a courier has to be able to call it.
 */
export function looksLikePhone(candidate: string): boolean {
  let digits = 0;

  for (let index = 0; index < candidate.length; index++) {
    const character = candidate[index];

    if (character >= '0' && character <= '9') {
      digits++;
    } else if (character === '+') {
      if (index !== 0) {
        return false;
      }
    } else if (!' -./()'.includes(character)) {
      return false;
    }
  }

  return digits >= 9 && digits <= 15;
}

/**
 * Whether a line is one of the ones the subtotal sums.
 *
 * **Exactly `CheckoutQuote.IsPriced` on the server, which is exactly the bag's `isBuyable`.** The
 * summary prints a price for a line only when that price is in the figures underneath it: an
 * `Unavailable` line printed `Rs. 3,990` beside a total it contributed nothing to, which reads as a
 * ladder that does not add up — the one class of defect this whole area of the code is organised to
 * prevent.
 */
export function isPricedLine(line: CheckoutLineDto): boolean {
  return line.status === 'Buyable' || line.status === 'NotEnoughStock';
}

const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
] as const;

/** Asia/Colombo is UTC+05:30 and has no daylight saving (see the server's `ColomboTime`). */
const COLOMBO_OFFSET_MINUTES = 330;

/**
 * A UTC instant as a Colombo date and time: `7 October 2026 at 2.30 pm`.
 *
 * **Hand-rolled rather than `Intl.DateTimeFormat`, for the reason `formatLkr` gives.** This page renders
 * on the server and hydrates in the browser, and the two runtimes do not have to agree on what a locale
 * or a time zone database looks like — Node can be built without full ICU. A formatter that disagreed
 * would rewrite a payment deadline during hydration, which is a visible flicker on the one figure a
 * shopper is being asked to act on.
 *
 * Returns an empty string for anything unparseable, so the view shows nothing rather than `Invalid Date`.
 */
export function formatColomboDateTime(utc: string): string {
  const parsed = Date.parse(utc.endsWith('Z') || /[+-]\d\d:?\d\d$/.test(utc) ? utc : `${utc}Z`);

  if (Number.isNaN(parsed)) {
    return '';
  }

  const local = new Date(parsed + COLOMBO_OFFSET_MINUTES * 60_000);
  const hours24 = local.getUTCHours();
  const suffix = hours24 < 12 ? 'am' : 'pm';
  const hours12 = hours24 % 12 === 0 ? 12 : hours24 % 12;
  const minutes = String(local.getUTCMinutes()).padStart(2, '0');

  return (
    `${local.getUTCDate()} ${MONTHS[local.getUTCMonth()]} ${local.getUTCFullYear()}` +
    ` at ${hours12}.${minutes} ${suffix}`
  );
}
