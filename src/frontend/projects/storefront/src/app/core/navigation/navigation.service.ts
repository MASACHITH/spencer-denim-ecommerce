import { Injectable, Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Observable, catchError, map, of, shareReplay, tap, timeout } from 'rxjs';

import { CatalogApi } from '../catalog/catalog-api';
import { ServerHandover } from '../platform/server-handover';
import { CategoryNodeDto, DepartmentDto } from './catalog-departments.model';
import { toNavigation } from './navigation.mapper';
import { NavItem, hasMenu } from './navigation.model';
import { PLACEHOLDER_DEPARTMENTS } from './navigation.data';

/**
 * How long the menu waits for the catalogue before it falls back.
 *
 * The header is on every page and the route guards read the same tree, so a slow department
 * feed is a slow site: a 12 s API gave every page a 12 s time to first byte, because
 * `catchError` only catches a failure and an answer that never comes is not one.
 */
export const DEPARTMENTS_TIMEOUT_MS = 2_000;

/** Where the server leaves the department tree for the browser. */
const DEPARTMENTS_HANDOVER = 'catalog:departments';

/**
 * Where the tree in hand came from, which is the difference between "this page is gone" and "I
 * could not find out" (#30).
 *
 * - `live`: the catalogue answered with a tree that can be routed on. A slug it does not have is
 *   genuinely not in the catalogue, so a 404 is the honest answer.
 * - `fallback`: the feed failed, timed out, or answered with something no route could match, so
 *   this is {@link PLACEHOLDER_DEPARTMENTS} — a frozen copy of the seed. A slug it does not have
 *   may still be a real page an admin added, and nothing here can tell. Answering 404 with
 *   `noindex` would drop a live listing out of the index for as long as the API was unhappy.
 */
export type CatalogTreeSource = 'live' | 'fallback';

/** A department tree together with {@link CatalogTreeSource}: never one without the other. */
export interface CatalogTree {
  readonly departments: readonly DepartmentDto[];
  readonly source: CatalogTreeSource;
}

/**
 * The seeded tree, labelled as the fallback.
 *
 * Shared rather than rebuilt so every "I could not find out" is the same object, and so no caller
 * can assemble a `CatalogTree` that claims the fallback is live.
 */
export const FALLBACK_TREE: CatalogTree = Object.freeze({
  departments: PLACEHOLDER_DEPARTMENTS,
  source: 'fallback',
});

/**
 * The main navigation tree (GLB-05): `Women · Men · Kids · New Arrivals · Sale`, and the same
 * department tree the listing pages read for their breadcrumb, category filter and copy (PLP-01),
 * and the route guards read to tell a department from any other first path segment.
 *
 * The departments come from `GET /api/catalog/departments`. The header is on every page, so it is
 * the last thing that may break: if the call fails, or simply takes too long, the menu falls back
 * to {@link PLACEHOLDER_DEPARTMENTS}, the same seeded tree in the same shape (lesson L1). The
 * fetch runs during server-side rendering and a **live** tree is handed to the browser with the
 * page, so the served HTML already carries the live menu and hydration does not swap it for the
 * fallback. A render that fell back hands over nothing, so the browser asks the feed itself: a page
 * served from the fallback repairs itself on hydration if the catalogue has recovered by then.
 *
 * The fallback is good enough for a menu and not good enough to answer "that page does not exist"
 * with, so {@link tree$} says which one it is handing out ({@link CatalogTreeSource}). The route
 * guards are the readers that care: see `catalog-route.guards.ts`.
 */
@Injectable({ providedIn: 'root' })
export class NavigationService {
  private readonly api = inject(CatalogApi);
  private readonly handover = inject(ServerHandover);

  /**
   * The tree, resolved once and replayed — already vetted, so every reader sees the same one, and
   * carrying {@link CatalogTree.source} so a reader can tell a live answer from the fallback.
   *
   * {@link toTree} sits inside the pipeline rather than on the signal that reads it. The vetting it
   * does used to be applied only to {@link departments}, which left the route guards reading the
   * raw response: a `200 []` — what the API legitimately answers for a freshly migrated database
   * before anyone runs the seeder, and for a site whose departments have all been deactivated —
   * sent every listing URL to the home page while the mega menu still listed all three
   * departments, and a `200 {"oops":true}` threw inside a guard and served Express's own error
   * page. `catchError` cannot help with either: a useless body is still a successful response.
   *
   * The route guards subscribe to this rather than reading {@link departments}, because a signal
   * cannot tell "still loading" from "the fallback".
   */
  readonly tree$: Observable<CatalogTree> = this.load().pipe(
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  /** The tree in hand and where it came from. The fallback until the first answer arrives. */
  readonly tree: Signal<CatalogTree> = toSignal(this.tree$, { initialValue: FALLBACK_TREE });

  /** The department tree, live or fallback, and never empty. */
  readonly departments: Signal<readonly DepartmentDto[]> = computed(() => this.tree().departments);

  /** All five entries, in order. */
  readonly items: Signal<readonly NavItem[]> = computed(() => toNavigation(this.departments()));

  /** Only the entries that open a menu, for tests and for menu-specific rendering. */
  readonly itemsWithMenu: Signal<readonly NavItem[]> = computed(() => this.items().filter(hasMenu));

  private load(): Observable<CatalogTree> {
    const handedOver = this.handover.take<readonly DepartmentDto[]>(DEPARTMENTS_HANDOVER);
    if (handedOver) {
      // Vetted again rather than trusted, because the handover crosses a process boundary.
      return of(toTree(handedOver));
    }

    return this.api.departments().pipe(
      timeout(DEPARTMENTS_TIMEOUT_MS),
      map(toTree),
      // **Only a live tree is handed over, and the order of these two operators is the point.**
      // `tap` used to sit above `timeout`, so it handed over whatever the API said before anything
      // vetted it — and a `200 []` is a successful response. The browser then took `[]` out of
      // `TransferState`, found it non-null, and never made a request at all: the page stayed on the
      // 503 for the rest of the session, where a 500, a destroyed connection and a timeout all
      // recovered on hydration because those hand over nothing. A transient empty catalogue is the
      // one failure that stranded the shopper, and it is one of #30's four modes.
      tap((tree) => {
        if (tree.source === 'live') {
          this.handover.give(DEPARTMENTS_HANDOVER, tree.departments);
        }
      }),
      catchError(() => of(FALLBACK_TREE)),
    );
  }
}

/**
 * Pairs an answer with where it came from, substituting the fallback for anything no reader could
 * use — without ever calling the fallback live.
 *
 * An empty menu is not a menu, and an empty tree leaves every listing page without a breadcrumb, a
 * category filter, or a route the guards will match (lesson L1). What is new in #30 is the label:
 * substituting the fallback silently is what let a route guard answer "that page is gone" about a
 * page it had simply never been told about.
 */
export function toTree(departments: readonly DepartmentDto[] | null): CatalogTree {
  return isRoutable(departments) ? { departments, source: 'live' } : FALLBACK_TREE;
}

/** Whether an answer has at least one department a route could match. */
function isRoutable(
  departments: readonly DepartmentDto[] | null,
): departments is readonly DepartmentDto[] {
  // Checking only that the array is non-empty let `[{"nope":1}]` through, and then every
  // department URL went home while the menu rendered perfectly — the same split as before, on a
  // narrower trigger. It is not far-fetched either: turning off the JSON naming policy would serve
  // `[{"Slug":"women"}]`, where every `slug` is `undefined`.
  return (
    Array.isArray(departments) &&
    departments.some(
      (department) => typeof department?.slug === 'string' && department.slug.length > 0,
    )
  );
}

/** The department a slug names, or `undefined`. */
export function findDepartment(
  departments: readonly DepartmentDto[],
  slug: string | null,
): DepartmentDto | undefined {
  return slug === null ? undefined : departments.find((candidate) => candidate.slug === slug);
}

/**
 * The category and its ancestors, nearest last. Searching the whole tree rather than trusting the
 * URL's middle segments means `/women/anything/jeans` still resolves to Women › Jeans instead of
 * drawing a breadcrumb out of a segment that names nothing.
 */
export function trailTo(
  categories: readonly CategoryNodeDto[],
  slug: string,
): readonly CategoryNodeDto[] {
  // A department object without a `categories` array would otherwise throw here, and this runs
  // inside a route guard, where a throw costs the whole page rather than one menu column.
  if (!Array.isArray(categories)) {
    return [];
  }

  for (const category of categories) {
    if (category.slug === slug) {
      return [category];
    }

    const nested = trailTo(category.children, slug);
    if (nested.length > 0) {
      return [category, ...nested];
    }
  }

  return [];
}
