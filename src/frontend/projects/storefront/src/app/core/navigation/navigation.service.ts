import { Injectable, Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Observable, catchError, of, tap } from 'rxjs';

import { CatalogApi } from '../catalog/catalog-api';
import { ServerHandover } from '../platform/server-handover';
import { DepartmentDto } from './catalog-departments.model';
import { toNavigation } from './navigation.mapper';
import { NavItem, hasMenu } from './navigation.model';
import { PLACEHOLDER_DEPARTMENTS } from './navigation.data';

/**
 * The main navigation tree (GLB-05): `Women · Men · Kids · New Arrivals · Sale`, and the same
 * department tree the listing pages read for their breadcrumb, category filter and copy (PLP-01).
 *
 * The departments come from `GET /api/catalog/departments`. The header is on every page, so it is
 * the last thing that may break: if the call fails — the API is down, a deployment is mid-flight,
 * the visitor is offline — the menu falls back to {@link PLACEHOLDER_DEPARTMENTS}, the same seeded
 * tree in the same shape (lesson L1). The fetch runs during server-side rendering and the tree is
 * handed to the browser with the page, so the served HTML already carries the live menu and
 * hydration does not swap it for the fallback while a second request is in flight.
 */
@Injectable({ providedIn: 'root' })
export class NavigationService {
  private readonly api = inject(CatalogApi);
  private readonly handover = inject(ServerHandover);

  private readonly feed = toSignal(this.tree(), { initialValue: PLACEHOLDER_DEPARTMENTS });

  /**
   * The department tree. An answer that is not a non-empty array is treated as a failure and
   * falls back too: an empty menu is not a menu, and an empty tree would leave every listing
   * page without a breadcrumb or a category filter.
   */
  readonly departments: Signal<readonly DepartmentDto[]> = computed(() => {
    const departments = this.feed();
    return Array.isArray(departments) && departments.length > 0
      ? departments
      : PLACEHOLDER_DEPARTMENTS;
  });

  /** All five entries, in order. */
  readonly items: Signal<readonly NavItem[]> = computed(() => toNavigation(this.departments()));

  /** Only the entries that open a menu, for tests and for menu-specific rendering. */
  readonly itemsWithMenu: Signal<readonly NavItem[]> = computed(() => this.items().filter(hasMenu));

  private tree(): Observable<readonly DepartmentDto[]> {
    const handedOver = this.handover.take<readonly DepartmentDto[]>(DEPARTMENTS_HANDOVER);
    if (handedOver) {
      return of(handedOver);
    }

    return this.api.departments().pipe(
      tap((departments) => this.handover.give(DEPARTMENTS_HANDOVER, departments)),
      catchError(() => of(PLACEHOLDER_DEPARTMENTS)),
    );
  }
}

/** Where the server leaves the department tree for the browser. */
const DEPARTMENTS_HANDOVER = 'catalog:departments';
