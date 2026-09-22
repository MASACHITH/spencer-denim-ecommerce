import { Injectable, Signal, computed, signal } from '@angular/core';

import { DepartmentDto } from './catalog-departments.model';
import { toNavigation } from './navigation.mapper';
import { NavItem, hasMenu } from './navigation.model';
import { PLACEHOLDER_DEPARTMENTS } from './navigation.data';

/**
 * The main navigation tree (GLB-05): `Women · Men · Kids · New Arrivals · Sale`.
 *
 * The departments come from a hard-coded tree in the exact shape of
 * `GET /api/catalog/departments`, so no page has to change when the live feed arrives.
 */
@Injectable({ providedIn: 'root' })
export class NavigationService {
  /**
   * TODO(W1-02): replace this one line with the live catalogue feed once
   * `feature/W1-02-catalogue-foundation` merges:
   *
   * ```ts
   * private readonly departments = toSignal(
   *   inject(HttpClient).get<DepartmentDto[]>('/api/catalog/departments').pipe(
   *     catchError(() => of(PLACEHOLDER_DEPARTMENTS)), // lesson L1: never break the header
   *   ),
   *   { initialValue: PLACEHOLDER_DEPARTMENTS },
   * );
   * ```
   *
   * Nothing below this line, and no component, needs to change: the response type is already
   * `DepartmentDto[]` and `toNavigation` already maps it.
   */
  private readonly departments = signal<readonly DepartmentDto[]>(PLACEHOLDER_DEPARTMENTS);

  /** All five entries, in order. */
  readonly items: Signal<readonly NavItem[]> = computed(() => toNavigation(this.departments()));

  /** Only the entries that open a menu, for tests and for menu-specific rendering. */
  readonly itemsWithMenu: Signal<readonly NavItem[]> = computed(() => this.items().filter(hasMenu));
}
