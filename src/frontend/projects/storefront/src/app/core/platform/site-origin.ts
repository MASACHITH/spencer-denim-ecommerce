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

/**
 * `path` as an absolute URL on this origin, leaving a URL that is already absolute alone.
 *
 * Media addresses arrive from the catalogue as site-relative keys (`/media/public/...`,
 * `CatalogOptions.MediaBaseUrl`), and will arrive as a CDN's absolute URLs the day that option points
 * at one — so both have to work, and neither may be concatenated blindly onto the other.
 */
export function absoluteUrl(origin: string, path: string): string {
  if (/^https?:\/\//i.test(path)) {
    return path;
  }

  return `${origin}${path.startsWith('/') ? '' : '/'}${path}`;
}
