/**
 * The sentence an order page shows after a customer comes back from an online payment page, picked by the
 * `pay` hint on the address the server redirected them to (`/order/{token}?pay=…`).
 *
 * **A message, never a fact.** The hint is in the address bar, so the customer controls it: it only ever
 * selects one of the fixed sentences below, nothing in it is displayed, and none of them says the order is
 * paid - the order's own status, read from the server, is what says that. A hint that contradicts the order
 * (a "closed" hint on a paid order) shows nothing.
 *
 * **Provider-neutral wording.** The page does not know whose payment page the customer came from, so these
 * sentences name none. The sentences that are about a particular provider's state - pending, being
 * processed, declined - come from the provider itself, in the order's payment instructions, and are shown
 * for as long as they are true rather than once.
 */
export const PAY_HINT_PARAMETER = 'pay';

/** How often the order is re-read after a "being processed" return, and how many times before giving up. */
export const PAY_POLL_INTERVAL_MS = 5_000;
export const PAY_POLL_MAX_ATTEMPTS = 12;

const MESSAGES: Readonly<Record<string, { text: string; whilePending: boolean }>> = {
  // Doc 11 P8 step 6: "We received your payment, our team will contact you".
  received: {
    text:
      'We received your payment, but could not match it to your order automatically. Our team will '
      + 'contact you, and you do not need to pay again.',
    whilePending: false,
  },
  notapproved: {
    text:
      'You came back without finishing the payment, so nothing was charged. You can try again below.',
    whilePending: true,
  },
  cancelled: {
    text:
      'You left the payment page without paying, so nothing was charged. You can pay again below.',
    whilePending: true,
  },
  // Doc 11 P8 step 4: "This order can no longer be paid."
  closed: { text: 'This order can no longer be paid.', whilePending: false },
  unavailable: {
    text: 'Paying online is not available right now. Please try again in a little while.',
    whilePending: true,
  },
};

/**
 * The sentence for a hint, or null when there is none to show.
 *
 * @param hint the raw `pay` query value, or null
 * @param orderStatus the order's own status, as the server reports it
 */
export function payHintMessage(hint: string | null | undefined, orderStatus: string): string | null {
  if (typeof hint !== 'string' || !Object.hasOwn(MESSAGES, hint)) {
    return null;
  }

  // A paid order has nothing to explain: whatever the address says, the order says it is paid.
  if (orderStatus === 'Paid') {
    return null;
  }

  const message = MESSAGES[hint];

  // "You can try again below" about an order that has since expired would point at a panel that is gone.
  if (message.whilePending && orderStatus !== 'PendingPayment') {
    return null;
  }

  return message.text;
}

/** Whether the hint says the result is not known yet, so the page should keep asking. */
export function payHintIsProcessing(hint: string | null | undefined): boolean {
  return hint === 'processing';
}
