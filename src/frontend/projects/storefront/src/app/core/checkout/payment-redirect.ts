import { OrderPaymentInstructionsDto, OrderPaymentRedirectFieldDto } from './checkout.model';

/**
 * How the order page hands a shopper over to an online payment provider's own page.
 *
 * **Provider-neutral by construction.** Nothing here knows which provider, or even that it is a gateway:
 * the server describes *how to get there* (a link, or a form to POST) and this decides whether the
 * storefront is willing to do it. A new redirect-style provider therefore needs no change to the page.
 *
 * `amountLkr` rides along on every variant so the panel can say what is about to be paid without reaching
 * back into the payload.
 */
export type PaymentRedirect = { readonly amountLkr: number } & (
  /** A real `<form method="post">`, submitted natively by the browser. */
  | {
      readonly kind: 'form';
      readonly action: string;
      readonly fields: readonly OrderPaymentRedirectFieldDto[];
    }
  /** A real `<a href>`, followed in the same tab. */
  | { readonly kind: 'link'; readonly href: string }
  /** The server asked for a redirect that this page will not perform. Says so, and offers nothing to click. */
  | { readonly kind: 'unavailable' }
  /**
   * The provider has something to say and nothing for the shopper to press - a payment it is still holding.
   * The words are the server's, rendered as text; there is deliberately no control, so a shopper is not
   * invited to pay twice.
   */
  | { readonly kind: 'notice'; readonly notice: string }
);

/** The only hosts that may be reached over plain `http:`, so a local build can talk to a local stub. */
const PLAIN_HTTP_HOSTS: ReadonlySet<string> = new Set(['localhost', '127.0.0.1']);

/**
 * The URL, normalised, if it is one this page is willing to send a shopper's browser to — otherwise null.
 *
 * **An allow-list, not a block-list.** The server's value is put in front of a browser as a link target or
 * a form action, and a block-list of bad schemes (`javascript:`, `data:`, `vbscript:` ...) is the kind of
 * list that is always one scheme short. So the rule is the short one: an **absolute `https:` URL**, or
 * `http:` for `localhost` and `127.0.0.1` only. Anything else — a relative URL, a protocol-relative one, an
 * `http:` address on the open internet, a script scheme, text that is not a URL at all — is refused.
 *
 * Angular's own sanitiser would neutralise `javascript:` in `[href]` and `[action]` too. It is not relied
 * on: it rewrites an unsafe value into `unsafe:...` and still renders the control, which would leave the
 * shopper a payment button that goes nowhere. This refuses first, so the page can say so instead.
 *
 * **URLs carrying a user name or password are refused as well.** `https://trusted.example@evil.example/`
 * is a well-known way of making an address read as somebody else's, and no payment page needs one.
 *
 * What is returned is `URL.href`, the form the browser itself will parse — so the thing checked is exactly
 * the thing rendered, with no second parse that could disagree.
 */
export function safeRedirectUrl(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') {
    return null;
  }

  const candidate = raw.trim();

  if (candidate.length === 0) {
    return null;
  }

  let url: URL;

  try {
    // No base: a relative or protocol-relative value throws, which is the refusal we want.
    url = new URL(candidate);
  } catch {
    return null;
  }

  if (url.username !== '' || url.password !== '') {
    return null;
  }

  if (url.protocol === 'https:') {
    return url.href;
  }

  if (url.protocol === 'http:' && PLAIN_HTTP_HOSTS.has(url.hostname)) {
    return url.href;
  }

  return null;
}

/**
 * What the pay panel should do about an order's payment instructions, or null when there is no redirect
 * and the bank-transfer rendering applies.
 *
 * - `redirectForm` wins over `redirectUrl` when both are set. When the form is set but unusable (an unsafe
 *   action, or a method other than POST) the link is **not** used instead: a GET to the bare address would
 *   open the provider's page without the fields it needs, which is worse than saying it is unavailable.
 * - No redirect at all, but a note and nothing a bank transfer has (no reference, no fields, no proof
 *   upload), is a `notice`: the provider telling the shopper where their payment stands.
 * - A redirect that is asked for and refused becomes `unavailable` — **unless the payload also carries real
 *   bank content** (`acceptsProof` or bank fields), in which case the bank rendering is still true and is
 *   used. Showing "transfer to the account below" with nothing below it, for a provider that has no bank
 *   account, would tell a shopper something false.
 */
export function paymentRedirect(how: OrderPaymentInstructionsDto): PaymentRedirect | null {
  const amountLkr = how.amountLkr;
  const form = how.redirectForm ?? null;

  if (form !== null) {
    const action = String(form.method).toUpperCase() === 'POST' ? safeRedirectUrl(form.action) : null;

    return action === null
      ? refused(how)
      : { amountLkr, kind: 'form', action, fields: Array.isArray(form.fields) ? form.fields : [] };
  }

  const url = typeof how.redirectUrl === 'string' ? how.redirectUrl.trim() : '';

  if (url.length === 0) {
    return noticeOnly(how);
  }

  const href = safeRedirectUrl(url);

  return href === null ? refused(how) : { amountLkr, kind: 'link', href };
}

/**
 * Instructions with no redirect, no reference, no fields and no proof upload, but a note: an online
 * provider telling the shopper where their payment stands. A bank transfer always has fields (or is null),
 * so it can never match, and this is how the page shows "your payment is pending" without knowing whose it is.
 */
function noticeOnly(how: OrderPaymentInstructionsDto): PaymentRedirect | null {
  const notice = typeof how.notes === 'string' ? how.notes.trim() : '';

  return !how.acceptsProof && how.reference === null && how.fields.length === 0 && notice.length > 0
    ? { amountLkr: how.amountLkr, kind: 'notice', notice }
    : null;
}

function refused(how: OrderPaymentInstructionsDto): PaymentRedirect | null {
  const hasBankContent = how.acceptsProof || how.fields.length > 0;

  return hasBankContent ? null : { amountLkr: how.amountLkr, kind: 'unavailable' };
}
