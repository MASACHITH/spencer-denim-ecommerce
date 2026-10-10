/**
 * What an order page says about money that has already arrived but could not settle the order, picked by the
 * `paymentNotice` code the server derives from the order's payments (doc 11 P8 step 6 and P8b step 4).
 *
 * **Provider-neutral, and a message rather than a fact.** The code says only that a payment landed and
 * staff have it; which provider took it, how much, and why it could not be applied are for the shop and are
 * never sent. A code this page does not know shows nothing - a newer server must not be able to make an
 * older page print something unreviewed - and the sentences below are the only ones that can appear.
 *
 * It does not say the order is paid and it never invites another payment: "you do not need to pay again" is
 * the point of it, and the server sends no way to pay (no bank details, no redirect) while it is set.
 */
export const PAYMENT_NOTICE_RECEIVED = 'received';

/** Statuses on which the money that arrived is the only payment, so "we received your payment" is the whole story. */
const UNPAID_STATUSES: ReadonlySet<string> = new Set(['PendingPayment', 'Expired', 'Cancelled']);

/**
 * The sentence for a notice, or null when there is none to show.
 *
 * @param notice the server's `paymentNotice`, or null/undefined when there is nothing to say
 * @param orderStatus the order's own status, as the server reports it
 */
export function paymentNoticeMessage(
  notice: string | null | undefined,
  orderStatus: string,
): string | null {
  if (notice !== PAYMENT_NOTICE_RECEIVED) {
    return null;
  }

  // An order that is already paid (or on its way) and still has a payment waiting for staff received a second
  // one: telling its customer "we received your payment" would read as if the first had not counted.
  if (!UNPAID_STATUSES.has(orderStatus)) {
    return (
      'We received a second payment for this order. Our team will contact you about it, '
      + 'and you do not need to pay again.'
    );
  }

  // Doc 11 P8 step 6, verbatim, followed by the one thing a customer must know.
  return 'We received your payment, our team will contact you. You do not need to pay again.';
}
