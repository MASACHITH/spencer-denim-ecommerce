import { CanMatchFn, UrlSegment } from '@angular/router';
import { inject } from '@angular/core';
import { Observable, map, take } from 'rxjs';

import { ContentPageIndex } from './content-page.model';
import { ContentPageService } from './content-page.service';

/**
 * Route guards for the content pages (GLB-10), and the reason they exist rather than letting the page
 * resolve itself.
 *
 * `/:slug` matches **any** single segment, so a content-page route placed before `**` claims every
 * mistyped address, every scanner probe and every stale deep link on the origin. Letting the page
 * component resolve those would be one API request per probe on a public route. The index is fetched
 * once per render and shared (`ContentPageService.index$`), so the guard answers from memory.
 *
 * They read `index$` rather than the signal for the reason `catalog-route.guards.ts` gives: a guard has
 * to wait for a real answer, and the signal starts on "unknown" — so a published page would be refused
 * for as long as the feed was in flight.
 */

/**
 * What the content feed can say about a single-segment address. Three answers, not two, which is #30.
 *
 * - `known`: the live index has it. A real page, served.
 * - `missing`: the **live** index was in hand and does not have it. The shop genuinely has no such
 *   page — a draft, a removed page and a typo all land here — so a 404 with `noindex` is honest.
 * - `unconfirmed`: the index was never read. The address may be a published policy page, and nothing
 *   here can tell. **503 with `Retry-After`**, which a crawler retries and which keeps the URL.
 *
 * The third is the one a single `pages.includes(slug)` collapses into the second, and it had to be named
 * before it could be routed differently.
 */
export type ContentPageVerdict = 'known' | 'missing' | 'unconfirmed';

/** True when the live index has a published page at this address. */
export const isKnownContentPage: CanMatchFn = (_route, segments) =>
  index().pipe(map((pages) => verdict(pages, segments) === 'known'));

/**
 * True when the index could not be read, so this address cannot be called missing.
 *
 * <b>Only for a single segment, and that bound is the safety property.</b> `canMatch` runs on `:slug`,
 * which matches any one segment, so without the length check this would have claimed nothing extra —
 * but the lesson `isUnconfirmedCatalogue` records the hard way is that a 503 route with a loose matcher
 * answers 503 for every address on the origin during an outage, including `/api/...` reached directly
 * and `/.well-known/...`. Those are deeper than one segment and so are outside this by construction;
 * the check is here so that stays true if the route is ever widened.
 *
 * What it does cost: during a content-feed outage a genuinely mistyped single segment answers 503
 * instead of 404. That is the right trade — a crawler retries a 503 and keeps the URL, and the
 * alternative drops a live policy page out of the index for the length of an API wobble.
 */
export const isUnconfirmedContentPage: CanMatchFn = (_route, segments) =>
  index().pipe(map((pages) => verdict(pages, segments) === 'unconfirmed'));

/** What the index says about the first segment. */
export function contentPageVerdict(
  pages: ContentPageIndex,
  segments: readonly UrlSegment[],
): ContentPageVerdict {
  return verdict(pages, segments);
}

function verdict(pages: ContentPageIndex, segments: readonly UrlSegment[]): ContentPageVerdict {
  if (segments.length !== 1) {
    return 'missing';
  }

  const slug = segments[0]?.path?.trim().toLowerCase();
  if (!slug) {
    return 'missing';
  }

  if (pages.pages.some((page) => page.slug === slug)) {
    return 'known';
  }

  // One line, and it is the fix: a live index that does not have a slug has settled the question, and
  // an index that was never read was never asked it.
  return pages.source === 'live' ? 'missing' : 'unconfirmed';
}

/** The shared index, once. `take(1)` because a guard has to settle, not subscribe. */
function index(): Observable<ContentPageIndex> {
  return inject(ContentPageService).index$.pipe(take(1));
}
