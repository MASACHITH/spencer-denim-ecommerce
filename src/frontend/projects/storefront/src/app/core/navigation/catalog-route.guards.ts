import { CanMatchFn, UrlSegment } from '@angular/router';
import { inject } from '@angular/core';
import { Observable, map, take } from 'rxjs';

import { CatalogTree, NavigationService, findDepartment, trailTo } from './navigation.service';

/**
 * Route guards that stop `/:department` from swallowing the rest of the site, and that keep the
 * storefront from calling a page gone when it simply could not find out.
 *
 * `/:department` matches any single segment, so before these guards `/bag`, `/wishlist`,
 * `/account`, `/contact`, `/shipping`, `/returns`, `/about`, `/privacy` and `/terms` — every
 * header and footer link that is not built yet — rendered the listing's not-found state, and told
 * the shopper that a *department* no longer exists when they had clicked "Bag". A `canMatch` that
 * answers "no" lets the router carry on to the next route, so those fall through to `**` and the
 * home page exactly as they did before this page existed.
 *
 * They read {@link NavigationService.tree$} rather than the signal because a guard has to wait
 * for a real answer: the signal starts on the offline fallback, and a department the client adds
 * later would be refused for as long as the feed is in flight. The observable is shared and
 * replayed, so this costs one request for the whole navigation, and the feed's own timeout bounds
 * how long a slow catalogue can hold a page up.
 */

/**
 * What the catalogue can say about the slugs in a URL. Three answers, not two — that is the whole
 * of #30.
 *
 * - `known`: the tree in hand has them. A real page, served.
 * - `missing`: the **live** tree was in hand and does not have them. The page is genuinely not in
 *   the catalogue, so a 404 with `noindex` is the honest answer.
 * - `unconfirmed`: the tree in hand was the frozen fallback, so the catalogue was never asked —
 *   or never answered. The page may be perfectly real: anything an admin added after the fallback
 *   was frozen is absent from it (ADM-11). The honest answer is **503 with `Retry-After`**, which
 *   a crawler retries and which keeps the URL, where a 404 drops it.
 *
 * The third answer is the one the storefront used to collapse into the second, and it had to be
 * named before it could be routed differently.
 */
export type CatalogVerdict = 'known' | 'missing' | 'unconfirmed';

/** True when the first segment names a department the catalogue knows. */
export const isKnownDepartment: CanMatchFn = (_route, segments) =>
  tree().pipe(map((catalogue) => departmentVerdict(catalogue, segments) === 'known'));

/**
 * True when the first segment names a department *and* the last one names a category inside it.
 * An unknown category is not this route's business: a later route renders the 404 or the 503.
 */
export const isKnownCategory: CanMatchFn = (_route, segments) =>
  tree().pipe(map((catalogue) => categoryVerdict(catalogue, segments) === 'known'));

/**
 * True when the **live** catalogue knows the department and has no such category — `/women/shirts`
 * names a Men's category. A page that is genuinely not there, so a real 404.
 *
 * It answers "no" when the department itself is unknown, which leaves `/bogus/thing` falling
 * through to `**` and the home page exactly as it did before. That is a soft 404 and it is #28's
 * to fix, not this guard's to change quietly.
 */
export const isMissingCategory: CanMatchFn = (_route, segments) =>
  tree().pipe(
    map(
      (catalogue) =>
        departmentVerdict(catalogue, segments) === 'known' &&
        categoryVerdict(catalogue, segments) === 'missing',
    ),
  );

/**
 * The mirror of {@link isMissingCategory}: a department the tree in hand knows, with a category it
 * has never heard of, read off the **frozen fallback** — so the storefront cannot say whether the
 * page exists.
 *
 * This is what must not answer 404: the page may be a listing an admin added, live and indexed, and
 * the departments feed merely blinked. The route it guards answers 503 with `Retry-After`.
 *
 * **The department must be one the tree already holds, and that condition is the whole safety
 * property.** `canMatch` runs on `:department/:category`, which matches any two segments, so
 * without it every two- and three-segment address on the origin answered 503 for the length of an
 * outage. Measured on the unguarded version: `/api/catalog/departments`, `/api/catalog/products`,
 * `/en/women`, `/si/women`, `/assets/x`, `/static/x`, `/account/orders`, `/bag/checkout` and
 * `/wp-admin/setup-config.php` all 503, where `main` answers 302 to the home page — and `/api/...`
 * is the sharp one, because `server.ts` does not proxy `/api` (Caddy does, in production), so a
 * direct container hit, a proxy misconfiguration or a monitor would be handed a 503 HTML page.
 *
 * It is an allow-list and it is the catalogue's own, so there is nothing to keep correct by hand: a
 * page added to the header tomorrow, a locale prefix, a scanner's probe and `/.well-known/...` are
 * all outside it by construction. The cost is that a **department** added in W1-12 and absent from
 * the fallback still falls through to `**` and the home page during an outage; that is the same
 * soft 404 as #28, it is recorded as W1-12's to answer with an allow-list of its own, and it is a
 * far smaller hazard than a 503 on everything.
 */
export const isUnconfirmedCatalogue: CanMatchFn = (_route, segments) =>
  tree().pipe(
    map(
      (catalogue) =>
        departmentVerdict(catalogue, segments) === 'known' &&
        categoryVerdict(catalogue, segments) === 'unconfirmed',
    ),
  );

/** What the catalogue says about the first segment on its own. */
export function departmentVerdict(
  catalogue: CatalogTree,
  segments: readonly UrlSegment[],
): CatalogVerdict {
  return findDepartment(catalogue.departments, slugAt(segments, 0)) !== undefined
    ? 'known'
    : unknown(catalogue);
}

/**
 * What the catalogue says about the department plus the deepest segment.
 *
 * The middle segments are not checked, deliberately — `trailTo` searches the whole department, so
 * `/women/anything/jeans` still resolves to Women › Jeans rather than drawing a breadcrumb out of a
 * segment that names nothing.
 */
export function categoryVerdict(
  catalogue: CatalogTree,
  segments: readonly UrlSegment[],
): CatalogVerdict {
  const department = findDepartment(catalogue.departments, slugAt(segments, 0));
  if (department === undefined) {
    return unknown(catalogue);
  }

  const category = slugAt(segments, segments.length - 1);
  const found = category !== null && trailTo(department.categories, category).length > 0;

  return found ? 'known' : unknown(catalogue);
}

/**
 * What "the tree does not have it" means, which depends entirely on which tree it was.
 *
 * One line, and it is the fix: a live tree that does not have a slug has settled the question, and
 * the fallback was never asked it.
 */
function unknown(catalogue: CatalogTree): CatalogVerdict {
  return catalogue.source === 'live' ? 'missing' : 'unconfirmed';
}

/** The shared tree, once. `take(1)` because a guard has to settle, not subscribe. */
function tree(): Observable<CatalogTree> {
  return inject(NavigationService).tree$.pipe(take(1));
}

/** A path segment as the catalogue spells its slugs, or `null` when it cannot be one. */
function slugAt(segments: readonly UrlSegment[], index: number): string | null {
  const value = segments[index]?.path?.trim().toLowerCase();
  return value ? value : null;
}
