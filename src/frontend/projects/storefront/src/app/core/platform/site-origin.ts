import { DOCUMENT } from '@angular/common';
import { InjectionToken, inject } from '@angular/core';
import { BRAND } from '@spencer-denim/shared';

/**
 * The absolute origin this page is being served from, for the handful of tags that cannot be relative
 * (GLB-11).
 *
 * **Why a token, and why only for those tags.** `<link rel="canonical">` is deliberately relative —
 * `CanonicalLink` says why: the render has no reliable idea of the public origin, and a relative
 * canonical resolves against the document just as well. Open Graph is the exception that forces the
 * issue: `og:url` and `og:image` are read by crawlers that do **not** resolve them against the
 * document, and a relative value is simply dropped, so a shared link loses its title card. So the
 * origin is wanted in exactly one place, and it is injected rather than guessed.
 *
 * On the server this is overridden in `app.config.server.ts`, where the request and the deployment's
 * `PUBLIC_SITE_ORIGIN` are both in reach and `src/server/origins.ts` decides between them — including
 * refusing to repeat back a `Host` header it does not recognise, which is the same class of bug as
 * W1-01's forged `X-Forwarded-For`. The factory below is the browser's answer, where `location.origin`
 * is the truth by definition.
 */
export const SITE_ORIGIN = new InjectionToken<string>('SITE_ORIGIN', {
  providedIn: 'root',
  factory: () => {
    // Through DOCUMENT rather than the global `location`, so a test can supply its own document and so
    // nothing in the application reaches for a browser global directly.
    const origin = inject(DOCUMENT).defaultView?.location?.origin;

    // `"null"` is what an opaque origin stringifies to (a sandboxed frame, a `data:` document). It is
    // not an origin, and putting it in an `og:url` would be worse than the brand fallback.
    return origin && origin !== 'null' ? origin : `https://${BRAND.domain}`;
  },
});

/** An http(s) URL, or a site-relative path, and nothing else. See {@link absoluteUrl}. */
export function isFetchableUrl(value: string | null | undefined): value is string {
  if (!value) {
    return false;
  }

  // A single leading slash: `//cdn.example/x` is protocol-relative, not a path on this site, and
  // prefixing an origin to it produces `https://shop.test//cdn.example/x`.
  return /^https?:\/\//i.test(value) || /^\/(?!\/)/.test(value);
}

/**
 * `value` as an absolute URL on this origin, or `null` when it is not something a crawler could
 * fetch.
 *
 * **Why it can refuse.** Media addresses arrive from the catalogue as site-relative keys
 * (`/media/public/...`, `CatalogOptions.MediaBaseUrl`) and will arrive as a CDN's absolute URLs the
 * day that option points at one, so both have to work and neither may be concatenated onto the other.
 * But a third case exists and the first version of this concatenated it blindly: the browser test
 * stub's hero carries a `data:` URI, and the home page published
 * `og:image="http://localhost:4960/data:image/gif;base64,..."`. A `data:` URI is not a valid
 * `og:image` in any case — the Open Graph consumers fetch the URL — so the honest answer is to decline
 * it and let the caller shrink the card, rather than to publish something malformed. The same applies
 * to a protocol-relative URL and to any non-HTTP scheme an admin might one day paste in.
 */
export function absoluteUrl(origin: string, value: string): string | null {
  if (!isFetchableUrl(value)) {
    return null;
  }

  return /^https?:\/\//i.test(value) ? value : `${origin}${value}`;
}
