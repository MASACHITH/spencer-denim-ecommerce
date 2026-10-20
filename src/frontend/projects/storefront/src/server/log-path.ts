/**
 * The path a **log** may print for a request (issue #201, follow-up to #179): the address with every part that
 * could be a credential replaced by `*`, so a log line can say which kind of page failed and never which order
 * or whose reset link.
 *
 * **One rule, three implementations that must agree.** This is the TypeScript port of
 * `RequestPathRedaction.ForLog` (`SpencerDenim.Application.Privacy`, which the API's request log uses and which
 * builds on `PagePath`, the rule W4-03's traffic recording stores addresses by) and of the gateway's `$sd_log_path`
 * map (`deploy/gateway/nginx.conf`). All three run the rows of `tests/Shared/log-path-vectors.json`
 * (`log-path.spec.ts` here), so a rule changed in one place and not the others turns a test red.
 *
 * 1. **Routes that carry a credential in the path are starred whatever follows** ({@link CREDENTIAL_ROUTES}).
 *    The generic rule below recognises a token only by its shape, so a token whose shape changed would pass it;
 *    naming the routes means it cannot. Matching is case-insensitive, and `;` ends a segment as `/` does
 *    (the Angular router ignores matrix parameters, so `/order;x=1/{token}` opens the order page).
 * 2. **Everything else is an allow-list.** The leading segments that are safe slugs are kept, up to
 *    {@link MAX_SEGMENTS} deep; the first that is not, and everything after it, becomes `*`. A route added later
 *    cannot leak by default. It is a net for shapes, not a proof about tokens: a secret of fifteen lower-case
 *    letters would pass it, which is why rule 1 names routes.
 *
 * **A segment is kept** when it is lower-case letters and digits separated by single hyphens, at most 100
 * characters, not a GUID (spelled with its hyphens anywhere), without a run of seven digits (hyphens ignored,
 * which is a telephone number or an id), and without a word of {@link LONG_WORD} or more letters and digits
 * (which a catalogue slug does not have and a random token does).
 *
 * **The query string and fragment are cut first.** A credential in a query string (`?token=` on a password
 * reset or email confirmation link, the PayPal return's `PayerID`) must not survive because a caller handed
 * over a whole request target, which is exactly what `req.originalUrl` is.
 *
 * **Segments are not percent-decoded**, unlike in the API, which logs a path its server already decoded:
 * Express's `originalUrl` is the raw target, and a `%` is not a slug character, so an escaped segment is
 * starred rather than judged. That can only star more than the decoded rule would, never less.
 *
 * Not ported: the gateway also keeps a static file name as the last segment (`main-AB12CD.js`) so a missing asset
 * can be told from a missing page. A render failure is never about an asset.
 */

/** What an unsafe segment, and everything after it, is printed as. */
export const REDACTED = '*';

/** How many leading slug segments are kept. */
export const MAX_SEGMENTS = 7;

/** A run of this many letters and digits is a token, not a word in a slug. */
export const LONG_WORD = 16;

/** A route whose path holds a credential, and what a log prints in place of everything after its prefix. */
export interface CredentialRoute {
  /** The gateway's pattern for the route, matched case-insensitively. */
  readonly pattern: RegExp;
  /** What is printed on a match. A `$<sd_a>` is the captured PayPal action, as in the gateway's map. */
  readonly redacted: string;
}

/** The same five routes, in the same order, as `RequestPathRedaction.CredentialRoutes` and the gateway's map. */
export const CREDENTIAL_ROUTES: readonly CredentialRoute[] = [
  // The storefront's guest order link. The address IS the credential (docs/08 3.5 Orders.PublicId).
  { pattern: /^\/order(?:[;/]|$)/i, redacted: '/order/*' },
  // The order page's own API calls.
  { pattern: /^\/api\/orders(?:[;/]|$)/i, redacted: '/api/orders/*' },
  // The PayPal round trip; the action is kept as the request spelled it.
  {
    pattern: /^\/api\/payments\/paypal\/(?<sd_a>start|return|cancel)(?:[;/]|$)/i,
    redacted: '/api/payments/paypal/$<sd_a>/*',
  },
  // Staff views of an order. The queue itself (with or without a trailing slash) stays readable.
  { pattern: /^\/api\/admin\/orders\/bank-transfers\/[^/]/i, redacted: '/api/admin/orders/bank-transfers/*' },
  { pattern: /^\/api\/admin\/orders\/(?!bank-transfers(?:[;/]|$))[^/]/i, redacted: '/api/admin/orders/*' },
];

const SLUG = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const LOOSE_GUID = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/;
const LONG_WORD_RUN = new RegExp(`[a-z0-9]{${LONG_WORD}}`);

/** The seven-digit run of `PersonalDataPatterns.HasPhoneDigitRun`; in a slug the only separator is the hyphen. */
function hasPhoneDigitRun(value: string): boolean {
  let run = 0;

  for (const character of value) {
    if (character >= '0' && character <= '9') {
      run += 1;
      if (run >= 7) {
        return true;
      }
    } else if (character !== '-') {
      run = 0;
    }
  }

  return false;
}

/** True when `segment` is a catalogue-slug-shaped word that cannot be a secret (see the file comment). */
export function isSafeLogSegment(segment: string): boolean {
  return (
    segment.length > 0 &&
    segment.length <= 100 &&
    SLUG.test(segment) &&
    !LOOSE_GUID.test(segment) &&
    !LONG_WORD_RUN.test(segment) &&
    !hasPhoneDigitRun(segment)
  );
}

/**
 * `target` (a request path, with or without its query string and fragment) as a log may print it. Never
 * throws.
 */
export function logPath(target: string | null | undefined): string {
  if (!target) {
    return '/';
  }

  const cut = target.search(/[?#]/);
  const path = cut >= 0 ? target.slice(0, cut) : target;

  if (path.length === 0) {
    return '/';
  }

  for (const route of CREDENTIAL_ROUTES) {
    const match = route.pattern.exec(path);

    if (match) {
      return route.redacted.replace('$<sd_a>', match.groups?.['sd_a'] ?? '');
    }
  }

  if (path[0] !== '/') {
    return `/${REDACTED}`;
  }

  // path starts with "/", so the first element is the empty string before it.
  const segments = path.split('/');
  const kept: string[] = [];

  for (let index = 1; index < segments.length; index += 1) {
    const segment = segments[index];

    if (segment.length === 0 && index === segments.length - 1) {
      break;
    }

    if (kept.length === MAX_SEGMENTS || !isSafeLogSegment(segment)) {
      return `${kept.length === 0 ? '' : join(kept)}/${REDACTED}`;
    }

    kept.push(segment);
  }

  return kept.length === 0 ? '/' : join(kept);
}

function join(kept: readonly string[]): string {
  return `/${kept.join('/')}`;
}
