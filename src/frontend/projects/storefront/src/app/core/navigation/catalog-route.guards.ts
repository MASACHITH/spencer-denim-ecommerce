import { CanMatchFn, UrlSegment } from '@angular/router';
import { inject } from '@angular/core';
import { Observable, map, take } from 'rxjs';

import { NavigationService, findDepartment, trailTo } from './navigation.service';

/**
 * Route guards that stop `/:department` from swallowing the rest of the site.
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

/** True when the first segment names a department the catalogue knows. */
export const isKnownDepartment: CanMatchFn = (_route, segments) => departmentOf(segments);

/**
 * True when the first segment names a department *and* the last one names a category inside it.
 * An unknown category is not this route's business: the next route renders the 404.
 */
export const isKnownCategory: CanMatchFn = (_route, segments) =>
  inject(NavigationService).tree$.pipe(
    map((departments) => {
      const department = findDepartment(departments, slugAt(segments, 0));
      const category = slugAt(segments, segments.length - 1);

      return (
        department !== undefined &&
        category !== null &&
        trailTo(department.categories, category).length > 0
      );
    }),
    take(1),
  );

function departmentOf(segments: readonly UrlSegment[]): Observable<boolean> {
  return inject(NavigationService).tree$.pipe(
    map((departments) => findDepartment(departments, slugAt(segments, 0)) !== undefined),
    take(1),
  );
}

/** A path segment as the catalogue spells its slugs, or `null` when it cannot be one. */
function slugAt(segments: readonly UrlSegment[], index: number): string | null {
  const value = segments[index]?.path?.trim().toLowerCase();
  return value ? value : null;
}
