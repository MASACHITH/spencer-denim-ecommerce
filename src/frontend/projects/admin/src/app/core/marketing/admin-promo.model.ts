/**
 * What kind of discount a promo code gives (ADM-13, doc 08 §3.6).
 *
 * Mirrors `SpencerDenim.Domain.Marketing.PromoDiscountType`. The API sends and accepts the member name,
 * because `Program.cs` registers a global `JsonStringEnumConverter` — so this is a union of names rather
 * than numbers, the same contract every other admin enum travels under.
 */
export type PromoDiscountType = 'Percent' | 'FixedAmount' | 'FreeDelivery';

/**
 * What the list says about a code right now (doc 11 P3).
 *
 * **Derived from the dates and the active flag, never stored**, so the list cannot disagree with what a
 * shopper's code actually does — `PromoCodeSchedule.StatusOf` and `PromoEvaluator` apply the same window.
 */
export type PromoCodeStatus = 'Scheduled' | 'Live' | 'Ended' | 'Inactive';

/** One `marketing.PromoCodes` row as the admin screen draws and edits it (ADM-13). */
export interface AdminPromoCode {
  readonly id: number;
  readonly code: string;
  readonly description: string | null;
  readonly discountType: PromoDiscountType;
  /** The percentage, or the LKR amount. Null for `FreeDelivery`. */
  readonly discountValue: number | null;
  /** The cap on a percentage discount. Only meaningful for `Percent`. */
  readonly maxDiscountAmount: number | null;
  /** Compared with the items subtotal after sale prices (doc 08 §6 step 7). */
  readonly minSpendAmount: number | null;
  /**
   * **The shop's own time, not UTC**, as `yyyy-MM-ddTHH:mm:ss` with no offset — which is what a
   * `datetime-local` input reads and writes. The column is UTC and the server converts at its edge, so a
   * staff member who types 23:59 gets 23:59 in Colombo rather than 05:29 the next morning.
   */
  readonly startsAt: string;
  /** The same, or null for no end. */
  readonly endsAt: string | null;
  readonly totalUsageLimit: number | null;
  readonly perCustomerLimit: number | null;
  /** Held uses (doc 08 §3.6). Not editable: only a placement and the return of a use move it. */
  readonly usageCount: number;
  readonly isActive: boolean;
  readonly status: PromoCodeStatus;
  /** Base64 `rowversion`, round-tripped as an ETag so two staff members cannot overwrite each other. */
  readonly rowVersion: string;
}

/** What a save answers: the row, plus anything to tell the staff member although it worked. */
export interface AdminPromoCodeSaved {
  readonly promoCode: AdminPromoCode;
  /**
   * Sentences about a save that **succeeded** — doc 08 §3.6 DB19's "lowering the total limit below the
   * uses already held is allowed with a warning", and a code saved with an end date in the past. Shown on
   * the success path on purpose: a silent save that did something other than what the staff member
   * expected is the shape of defect this project has shipped three times.
   */
  readonly warnings: readonly string[];
}

/** The body a save sends. Every field, every time — see `AdminPromoApi.save`. */
export interface AdminPromoCodeRequest {
  readonly code: string;
  readonly description: string | null;
  readonly discountType: PromoDiscountType;
  readonly discountValue: number | null;
  readonly maxDiscountAmount: number | null;
  readonly minSpendAmount: number | null;
  readonly startsAt: string;
  readonly endsAt: string | null;
  readonly totalUsageLimit: number | null;
  readonly perCustomerLimit: number | null;
  readonly isActive: boolean;
}

/** What a discount is worth, in words, for the list. */
export function promoDiscountLabel(promoCode: AdminPromoCode): string {
  switch (promoCode.discountType) {
    case 'Percent': {
      const capped =
        promoCode.maxDiscountAmount === null
          ? ''
          : `, up to ${formatAmount(promoCode.maxDiscountAmount)}`;

      // `String` already drops trailing zeros for a JS number, so 10 prints "10" and 12.5 prints "12.5".
      return `${promoCode.discountValue ?? 0}% off${capped}`;
    }
    case 'FixedAmount':
      return `${formatAmount(promoCode.discountValue ?? 0)} off`;
    case 'FreeDelivery':
      return 'Free delivery';
    default:
      return 'Unknown';
  }
}

/** What the list says about the limits — doc 11 §6.4 item 17's weekly "check … code limits". */
export function promoLimitLabel(promoCode: AdminPromoCode): string {
  const total =
    promoCode.totalUsageLimit === null
      ? `${promoCode.usageCount} used, no limit`
      : `${promoCode.usageCount} of ${promoCode.totalUsageLimit} used`;

  return promoCode.perCustomerLimit === null
    ? total
    : `${total} · ${promoCode.perCustomerLimit} per customer`;
}

/**
 * `Rs. 1,000` / `Rs. 1,000.50`.
 *
 * Hand-rolled rather than `Intl.NumberFormat` for the reason the storefront's `formatLkr` gives: this app
 * is not server-rendered, but two formatters in one repository that disagree by a separator is a figure
 * that reads differently on two screens showing the same row.
 */
function formatAmount(amount: number): string {
  const whole = Math.trunc(Math.abs(amount));
  const cents = Math.round((Math.abs(amount) - whole) * 100);
  const grouped = whole.toLocaleString('en-US');

  return `Rs. ${grouped}${cents > 0 ? `.${String(cents).padStart(2, '0')}` : ''}`;
}

