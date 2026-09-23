import { Injectable, Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Observable, catchError, of, shareReplay, tap, timeout } from 'rxjs';

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
 * The main navigation tree (GLB-05): `Women · Men · Kids · New Arrivals · Sale`, and the same
 * department tree the listing pages read for their breadcrumb, category filter and copy (PLP-01),
 * and the route guards read to tell a department from any other first path segment.
 *
 * The departments come from `GET /api/catalog/departments`. The header is on every page, so it is
 * the last thing that may break: if the call fails, or simply takes too long, the menu falls back
 * to {@link PLACEHOLDER_DEPARTMENTS}, the same seeded tree in the same shape (lesson L1). The
 * fetch runs during server-side rendering and the tree is handed to the browser with the page, so
 * the served HTML already carries the live menu and hydration does not swap it for the fallback.
 */
@Injectable({ providedIn: 'root' })
export class NavigationService {
  private readonly api = inject(CatalogApi);
  private readonly handover = inject(ServerHandover);

  /**
   * The tree, resolved once and replayed. The route guards subscribe to this rather than reading
   * {@link departments}, because a signal cannot tell "still loading" from "the fallback".
   */
  readonly tree$: Observable<readonly DepartmentDto[]> = this.load().pipe(
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  private readonly feed = toSignal(this.tree$, { initialValue: PLACEHOLDER_DEPARTMENTS });

  /**
   * The department tree. An answer that is not a non-empty array is treated as a failure and
   * falls back too: an empty menu is not a menu, and an empty tree would leave every listing
   * page without a breadcrumb or a category filter.
   */
  readonly departments: Signal<readonly DepartmentDto[]> = computed(() => usable(this.feed()));

  /** All five entries, in order. */
  readonly items: Signal<readonly NavItem[]> = computed(() => toNavigation(this.departments()));

  /** Only the entries that open a menu, for tests and for menu-specific rendering. */
  readonly itemsWithMenu: Signal<readonly NavItem[]> = computed(() => this.items().filter(hasMenu));

  private load(): Observable<readonly DepartmentDto[]> {
    const handedOver = this.handover.take<readonly DepartmentDto[]>(DEPARTMENTS_HANDOVER);
    if (handedOver) {
      return of(handedOver);
    }

    return this.api.departments().pipe(
      tap((departments) => this.handover.give(DEPARTMENTS_HANDOVER, departments)),
      timeout(DEPARTMENTS_TIMEOUT_MS),
      catchError(() => of(PLACEHOLDER_DEPARTMENTS)),
    );
  }
}

/** Guards the menu against an empty or malformed payload without hiding a real failure. */
export function usable(departments: readonly DepartmentDto[]): readonly DepartmentDto[] {
  return Array.isArray(departments) && departments.length > 0
    ? departments
    : PLACEHOLDER_DEPARTMENTS;
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
