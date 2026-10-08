/**
 * The handful of formatting rules every orders screen shares (ADM-09).
 *
 * **One module rather than a copy per component**, because the alternative is already in the tree: the
 * bank-transfer queue grew its own `money()` and its own countdown, and the order list, the order detail
 * and the invoice would have made three more. Two screens rendering one shop's currency differently is the
 * kind of difference nobody notices until a client does.
 */

/**
 * `Rs. 15,596.60`, or `USD 53.55` for anything that is not the shop's own currency.
 *
 * **The currency is a parameter and not a constant, which was a defect rather than a precaution.** The
 * first version wrote `Rs.` unconditionally while every contract it renders carries a `currency` field
 * — `AdminOrderMoney`, `AdminOrderPayment` and `AdminOrderRefund` all have one, and doc 08 §3.5 gives
 * them one *because* a PayPal capture is taken in USD (D1). A hard-coded symbol beside a field whose
 * whole purpose is to say which currency it is, is a wrong number waiting for a schema change.
 *
 * **Today every one of those fields is `LKR` by check constraint** (`CK_Payments_Currency`,
 * `CK_Refunds_Currency`, `CK_Orders_Currency`), so nothing on screen changes. The figure that actually
 * differs is `providerAmount` / `providerCurrency` — the USD PayPal was asked for and the USD a refund
 * returned — and those are rendered separately, where they exist. See `AdminOrderPayment`.
 *
 * `USD 53.55` rather than `$53.55` is doc 11 P10 step 6's own wording, which is what a staff member
 * reads out to a customer.
 */
export function money(amount: number, currency: string): string {
  const figure = amount.toLocaleString('en-LK', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });

  return currency === LKR ? `Rs. ${figure}` : `${currency} ${figure}`;
}

/** The shop's own currency. `SalesCurrency.Lkr` on the server; `char(3)` on every money row. */
const LKR = 'LKR';

/**
 * "4 h 12 min left", or "overdue by 11 min" once the deadline has passed.
 *
 * **Negative is a different sentence rather than a clamp to zero**, because "overdue by eleven minutes"
 * and "due now" are different facts to somebody deciding whether to chase a payment — which is why the
 * server sends a signed number of seconds instead of a rendered string in the first place.
 */
export function timeLeft(seconds: number): string {
  if (seconds <= 0) {
    const overdue = Math.abs(seconds);
    const overdueHours = Math.floor(overdue / 3600);
    const overdueMinutes = Math.floor((overdue % 3600) / 60);

    return overdueHours === 0
      ? `overdue by ${Math.max(1, overdueMinutes)} min`
      : `overdue by ${overdueHours} h ${overdueMinutes} min`;
  }

  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);

  // `Math.max(1, …)` so a deadline 20 seconds away reads "1 min left" rather than "0 min left", which
  // would be indistinguishable from overdue.
  return hours === 0 ? `${Math.max(1, minutes)} min left` : `${hours} h ${minutes} min left`;
}

/**
 * A UTC timestamp as a staff member reads it, in **Colombo** time.
 *
 * **The time zone is named rather than left to the browser**, and that is not pedantry: a shop computer
 * set to another zone — or a staff member working from abroad — would otherwise read an order's history in
 * their own time while the customer on the telephone reads it in theirs. The shop has one clock.
 *
 * `null` in, empty string out, so a template can interpolate it without a second check. The caller decides
 * whether an absent timestamp is worth a row at all.
 */
export function colomboTime(utc: string | null | undefined): string {
  if (!utc) {
    return '';
  }

  const parsed = new Date(utc.endsWith('Z') ? utc : `${utc}Z`);

  if (Number.isNaN(parsed.getTime())) {
    // An unparseable value is shown as it arrived rather than as "Invalid Date". It can only come from a
    // contract change, and the raw string is the thing that would let somebody diagnose one.
    return utc;
  }

  return parsed.toLocaleString('en-LK', {
    timeZone: 'Asia/Colombo',
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
}

/** The same, date only — for an invoice, where the minute an order was paid is noise. */
export function colomboDate(utc: string | null | undefined): string {
  if (!utc) {
    return '';
  }

  const parsed = new Date(utc.endsWith('Z') ? utc : `${utc}Z`);

  if (Number.isNaN(parsed.getTime())) {
    return utc;
  }

  return parsed.toLocaleDateString('en-LK', {
    timeZone: 'Asia/Colombo',
    year: 'numeric',
    month: 'short',
    day: '2-digit',
  });
}
