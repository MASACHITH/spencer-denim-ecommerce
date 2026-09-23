import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Meta, Title } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Drawer, Icon } from '@spencer-denim/shared';

import { ListingFilters } from './listing-filters';
import { ProductCard } from '../../ui/product-card/product-card';
import { ProductListStore } from '../../core/catalog/product-list-store';
import { SORT_OPTIONS, SortValue, readSort } from '../../core/catalog/product-sort';
import {
  ListingState,
  clearFilters,
  hasActiveFilters,
  readListingState,
  sameFilters,
  toQueryParams,
  withFilter,
} from '../../core/catalog/listing-state';
import { FilterChip, activeChips, removeChip } from '../../core/catalog/listing-chips';
import { CategoryNodeDto, DepartmentDto } from '../../core/navigation/catalog-departments.model';
import { NavigationService } from '../../core/navigation/navigation.service';

/** One breadcrumb step. The last one is the page itself and is not a link. */
interface Crumb {
  readonly label: string;
  readonly path: string;
}

/** The department and the category tree branch the URL names, resolved once. */
interface Placement {
  readonly department: DepartmentDto | null;
  /** The category and its ancestors, nearest last. Empty on a department landing page. */
  readonly trail: readonly CategoryNodeDto[];
}

/**
 * The department and category listing (DEPT-01, PLP-01..07, CARD-01).
 *
 * The URL is the state. Every filter, the sort and how much has been loaded are read out of it
 * and written back into it, which is what makes a filtered listing shareable, bookmarkable,
 * server-rendered and reversible with the browser's Back button. Nothing is kept in a service
 * that outlives the page.
 *
 * The first request is issued from the constructor rather than from an effect, so it is already
 * in flight while the server renders: this is the page search engines read, and it has to arrive
 * with the products in the HTML rather than with an empty grid and a promise to fetch some.
 */
@Component({
  selector: 'sd-listing-page',
  imports: [Drawer, Icon, ListingFilters, ProductCard, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ProductListStore],
  template: `
    @if (department()) {
      <div class="container plp">
        <nav class="breadcrumb" aria-label="Breadcrumb">
          <ol>
            @for (crumb of breadcrumb(); track crumb.path; let last = $last) {
              <li>
                @if (last) {
                  <span aria-current="page">{{ crumb.label }}</span>
                } @else {
                  <a [routerLink]="crumb.path">{{ crumb.label }}</a>
                }
              </li>
            }
          </ol>
        </nav>

        <header class="plp__head">
          <h1 class="plp__title">{{ heading() }}</h1>
          @if (description(); as intro) {
            <p class="plp__intro">{{ intro }}</p>
          }
        </header>

        <!-- PLP-01 promo slot. Admin-managed artwork arrives with ADM-17; until a department or
             category has one, the slot renders nothing rather than an empty framed box. -->
        @if (promoImage(); as promo) {
          <p class="plp__promo">
            <img [src]="promo" alt="" />
          </p>
        }

        <div class="plp__bar">
          <p class="plp__count">
            <strong>{{ totalCount() }}</strong>
            {{ totalCount() === 1 ? 'product' : 'products' }}
          </p>

          <div class="plp__controls">
            <button
              class="btn btn--secondary plp__filters-btn"
              type="button"
              aria-controls="listing-filters"
              [attr.aria-expanded]="filtersOpen()"
              (click)="openFilters()"
            >
              <sd-icon name="filter" size="sm" />
              Filters
              @if (chips().length > 0) {
                <span class="plp__pill" aria-hidden="true">{{ chips().length }}</span>
                <span class="sr-only">, {{ chips().length }} applied</span>
              }
            </button>

            <div class="plp__sort">
              <label class="plp__sort-label" for="listing-sort">Sort by</label>
              <select
                id="listing-sort"
                class="plp__select"
                [value]="state().sort"
                (change)="onSortChange($event)"
              >
                @for (option of sortOptions; track option.value) {
                  <option [value]="option.value" [selected]="option.value === state().sort">
                    {{ option.label }}
                  </option>
                }
              </select>
            </div>
          </div>
        </div>

        @if (chips().length > 0) {
          <div class="plp__chips">
            <ul class="chips" aria-label="Active filters">
              @for (chip of chips(); track chip.id) {
                <li>
                  <button
                    class="chip"
                    type="button"
                    [attr.aria-label]="chip.removeLabel"
                    (click)="onRemoveChip(chip)"
                  >
                    {{ chip.label }}
                    <sd-icon name="close" size="xs" />
                  </button>
                </li>
              }
            </ul>
            <button class="plp__clear" type="button" (click)="onClearFilters()">
              Clear filters
            </button>
          </div>
        }

        <div class="plp__layout">
          <aside class="plp__sidebar" aria-label="Filters">
            <sd-listing-filters
              [state]="state()"
              [departments]="departments()"
              (changed)="apply($event)"
            />
          </aside>

          <section class="plp__results" aria-labelledby="listing-results-title">
            <h2 class="sr-only" id="listing-results-title">Products</h2>

            <!-- One live region for the whole listing: the visible count updates on its own, and
                 a second live element would make a screen reader say everything twice. It also
                 carries the count after "Load More", which is otherwise a silent change. -->
            <p class="sr-only" role="status">{{ announcement() }}</p>

            @if (store.hasError()) {
              <div class="plp__state">
                <p class="plp__state-title">We could not show these products</p>
                <p>{{ store.errorMessage() }}</p>
                <div class="plp__state-actions">
                  <button class="btn btn--primary" type="button" (click)="retry()">Try again</button>
                  @if (filtersActive()) {
                    <button class="btn btn--secondary" type="button" (click)="onClearFilters()">
                      Clear filters
                    </button>
                  }
                </div>
              </div>
            } @else if (store.isLoading()) {
              <p class="plp__state">Loading products…</p>
            } @else if (products().length === 0) {
              <div class="plp__state">
                <p class="plp__state-title">No products found</p>
                @if (filtersActive()) {
                  <p>
                    Nothing matches all {{ chips().length }} of your filters. Take one off to widen
                    the search:
                  </p>
                  <ul class="chips plp__state-chips" aria-label="Filters you can remove">
                    @for (chip of chips(); track chip.id) {
                      <li>
                        <button
                          class="chip"
                          type="button"
                          [attr.aria-label]="chip.removeLabel"
                          (click)="onRemoveChip(chip)"
                        >
                          {{ chip.label }}
                          <sd-icon name="close" size="xs" />
                        </button>
                      </li>
                    }
                  </ul>
                  <div class="plp__state-actions">
                    <button class="btn btn--primary" type="button" (click)="onClearFilters()">
                      Clear all filters
                    </button>
                  </div>
                } @else {
                  <p>
                    There is nothing in {{ heading() }} yet. Try another category from the menu, or
                    browse everything in {{ departmentName() }}.
                  </p>
                  <div class="plp__state-actions">
                    <a class="btn btn--primary" [routerLink]="departmentPath()">
                      Shop all {{ departmentName() }}
                    </a>
                  </div>
                }
              </div>
            } @else {
              <ul class="product-grid">
                @for (product of products(); track product.id) {
                  <li>
                    <sd-product-card [product]="product" />
                  </li>
                }
              </ul>

              <div class="plp__more">
                @if (store.hasMore()) {
                  <p class="plp__more-count">{{ shownSummary() }}</p>
                  <button
                    class="btn btn--secondary"
                    type="button"
                    [disabled]="store.isAppending()"
                    (click)="loadMore()"
                  >
                    {{ store.isAppending() ? 'Loading…' : 'Load More' }}
                  </button>
                } @else if (products().length > 0) {
                  <p class="plp__more-count">{{ shownSummary() }}</p>
                }
              </div>
            }
          </section>
        </div>
      </div>

      <sd-drawer
        [open]="filtersOpen()"
        panelId="listing-filters"
        heading="Filters"
        side="end"
        closeLabel="Close filters"
        (closed)="closeFilters()"
      >
        <sd-listing-filters
          [state]="state()"
          [departments]="departments()"
          (changed)="apply($event)"
        />
        <div class="plp__drawer-actions">
          <button class="btn btn--secondary" type="button" (click)="onClearFilters()">
            Clear filters
          </button>
          <button class="btn btn--primary" type="button" (click)="closeFilters()">
            Show {{ totalCount() }} {{ totalCount() === 1 ? 'product' : 'products' }}
          </button>
        </div>
      </sd-drawer>
    } @else {
      <div class="container plp plp--missing">
        <h1 class="plp__title">We could not find that page</h1>
        <p class="plp__intro">
          The address may have changed, or the department may no longer exist. Everything we sell
          is one of these:
        </p>
        <ul class="plp__missing-links">
          @for (candidate of departments(); track candidate.slug) {
            <li><a class="btn btn--secondary" [routerLink]="'/' + candidate.slug">{{ candidate.name }}</a></li>
          }
        </ul>
      </div>
    }
  `,
  // The breadcrumb, the chips and the product grid are storefront-wide design-system pieces and
  // live in projects/storefront/src/styles.css; only this page's own layout is scoped here.
  styles: `
    :host {
      display: block;
    }
    .plp {
      padding-block: var(--space-3) var(--space-8);
    }


    .plp__head {
      display: grid;
      gap: var(--space-1);
      padding-block: var(--space-1) var(--space-3);
    }
    .plp__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .plp__intro {
      max-width: 62ch;
      color: var(--color-text-muted);
    }
    .plp__promo {
      margin-bottom: var(--space-3);
    }
    .plp__promo img {
      width: 100%;
      aspect-ratio: 16 / 5;
      object-fit: cover;
    }

    .plp__bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1-5) var(--space-2);
      padding-block: var(--space-1-5);
      border-block: 1px solid var(--color-border);
    }
    .plp__count {
      font-size: var(--text-small);
    }
    .plp__controls {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: flex-end;
      gap: var(--space-1-5);
      flex: 1 1 auto;
    }
    .plp__filters-btn {
      min-height: var(--tap);
      padding-inline: var(--space-2);
    }
    .plp__pill {
      display: inline-grid;
      place-items: center;
      min-width: 20px;
      height: 20px;
      padding-inline: 5px;
      background: var(--color-primary);
      color: var(--color-on-primary);
      font-size: 0.6875rem;
      font-variant-numeric: tabular-nums;
    }
    /* The sidebar replaces the button from 1024 px up, where the panel is always on screen. */
    @media (min-width: 1024px) {
      .plp__filters-btn {
        display: none;
      }
    }
    .plp__sort {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      flex: 1 1 220px;
      max-width: 320px;
    }
    .plp__sort-label {
      font-size: var(--text-small);
      white-space: nowrap;
    }
    .plp__select {
      flex: 1;
      min-width: 0;
      min-height: var(--tap);
      padding: 0 var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-surface);
      font-size: var(--text-small);
      cursor: pointer;
    }

    .plp__chips {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1);
      padding-top: var(--space-2);
    }

    .plp__clear {
      min-height: var(--tap);
      padding-inline: var(--space-1);
      border: 0;
      background: none;
      font-size: var(--text-small);
      text-decoration: underline;
      text-underline-offset: 4px;
      cursor: pointer;
    }

    .plp__layout {
      display: grid;
      gap: var(--space-4);
      padding-top: var(--space-3);
    }
    .plp__sidebar {
      display: none;
    }
    @media (min-width: 1024px) {
      .plp__layout {
        grid-template-columns: 240px minmax(0, 1fr);
        gap: var(--space-6);
      }
      .plp__sidebar {
        display: block;
        align-self: start;
        min-width: 0;
      }
    }
    @media (min-width: 1440px) {
      .plp__layout {
        grid-template-columns: 264px minmax(0, 1fr);
      }
    }
    .plp__results {
      min-width: 0;
    }


    .plp__state {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      max-width: 62ch;
      padding-block: var(--space-4);
      color: var(--color-text-muted);
    }
    .plp__state-title {
      font-size: var(--text-h3);
      color: var(--color-text);
    }
    .plp__state-actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
    }
    .plp__state-chips {
      padding: 0;
    }

    .plp__more {
      display: grid;
      justify-items: center;
      gap: var(--space-1-5);
      padding-top: var(--space-6);
    }
    .plp__more-count {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }

    .plp__drawer-actions {
      display: grid;
      gap: var(--space-1);
      padding-top: var(--space-3);
    }

    .plp--missing {
      display: grid;
      gap: var(--space-2);
      justify-items: start;
      padding-block: var(--space-8);
    }
    .plp__missing-links {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
      list-style: none;
      padding-top: var(--space-1);
    }
  `,
})
export class ListingPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly navigation = inject(NavigationService);
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);

  protected readonly store = inject(ProductListStore);
  protected readonly sortOptions = SORT_OPTIONS;

  private readonly routeParams = toSignal(this.route.params, {
    initialValue: this.route.snapshot.params,
  });

  private readonly queryParams = toSignal(this.route.queryParams, {
    initialValue: this.route.snapshot.queryParams,
  });

  /**
   * The listing the URL describes. The deepest path segment is the category the API filters on:
   * the mega menu links three levels deep for Kids (`/kids/girls/girls-dresses`), and the API
   * returns a category's whole subtree, so the last segment is the only one it needs.
   */
  protected readonly state = computed<ListingState>(() => {
    const params = this.routeParams();
    return readListingState(
      params['department'],
      params['subcategory'] ?? params['category'],
      this.queryParams(),
    );
  });

  protected readonly departments = this.navigation.departments;

  private readonly placement = computed<Placement>(() => {
    const state = this.state();
    const department =
      this.departments().find((candidate) => candidate.slug === state.department) ?? null;

    return {
      department,
      trail: department && state.category ? trailTo(department.categories, state.category) : [],
    };
  });

  protected readonly department = computed(() => this.placement().department);
  protected readonly departmentName = computed(() => this.department()?.name ?? '');
  protected readonly departmentPath = computed(() => `/${this.state().department ?? ''}`);

  /** The category the URL names, when the tree knows it. */
  private readonly category = computed(() => this.placement().trail.at(-1) ?? null);

  protected readonly heading = computed(
    () => this.category()?.name ?? this.departmentName() ?? 'Products',
  );

  protected readonly description = computed(
    () => this.category()?.description ?? this.department()?.description ?? null,
  );

  protected readonly promoImage = computed(
    () => this.category()?.imageUrl ?? this.department()?.heroImageUrl ?? null,
  );

  protected readonly breadcrumb = computed<readonly Crumb[]>(() => {
    const department = this.department();
    if (!department) {
      return [];
    }

    const crumbs: Crumb[] = [
      { label: 'Home', path: '/' },
      { label: department.name, path: `/${department.slug}` },
    ];

    let path = `/${department.slug}`;
    for (const node of this.placement().trail) {
      path = `${path}/${node.slug}`;
      crumbs.push({ label: node.name, path });
    }

    return crumbs;
  });

  protected readonly products = this.store.items;
  protected readonly totalCount = this.store.totalCount;
  protected readonly chips = computed<readonly FilterChip[]>(() => activeChips(this.state()));
  protected readonly filtersActive = computed(() => hasActiveFilters(this.state()));

  protected readonly shownSummary = computed(
    () => `Showing ${this.products().length} of ${this.totalCount()} products`,
  );

  /** What a screen reader hears whenever the result set changes (PLP-06, PLP-07). */
  protected readonly announcement = computed(() => {
    if (this.store.isLoading()) {
      return 'Loading products';
    }
    if (this.store.hasError()) {
      return this.store.errorMessage() ?? '';
    }
    if (this.totalCount() === 0) {
      return 'No products found';
    }

    return `${this.shownSummary()}.`;
  });

  private readonly documentTitle = computed(() => {
    const department = this.department();
    if (!department) {
      return 'Page not found | Spencer Denim';
    }

    const category = this.category();
    if (category) {
      return `${category.name} | ${department.name} | Spencer Denim`;
    }

    return `${department.seoTitle ?? department.name} | Spencer Denim`;
  });

  private readonly metaDescription = computed(() => {
    const department = this.department();
    if (!department) {
      return 'The page you asked for is not part of the Spencer Denim store.';
    }

    const category = this.category();
    return (
      category?.description ??
      department.seoDescription ??
      department.description ??
      `Shop ${this.heading()} at Spencer Denim.`
    );
  });

  private readonly drawerOpen = signal(false);
  protected readonly filtersOpen = this.drawerOpen.asReadonly();

  /** What the store was last asked for, so an added page appends instead of reloading. */
  private applied: ListingState | null = null;

  constructor() {
    // Before the first change detection, so the request is already in flight while the server
    // renders and the HTML a crawler receives has products in it.
    this.request(this.state());

    effect(() => {
      const next = this.state();
      untracked(() => this.request(next));
    });

    effect(() => {
      const title = this.documentTitle();
      const description = this.metaDescription();
      untracked(() => {
        this.title.setTitle(title);
        this.meta.updateTag({ name: 'description', content: description });
      });
    });
  }

  protected apply(next: ListingState): void {
    this.router.navigate(this.commandsFor(next), { queryParams: toQueryParams(next) });
  }

  protected onSortChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.apply(withFilter(this.state(), { sort: readSort(value) satisfies SortValue }));
  }

  protected onRemoveChip(chip: FilterChip): void {
    this.apply(removeChip(this.state(), chip));
  }

  protected onClearFilters(): void {
    this.apply(clearFilters(this.state()));
  }

  /** PLP-07: one more page, recorded in the URL so the depth survives a share and a reload. */
  protected loadMore(): void {
    const state = this.state();
    this.apply({ ...state, loadedPages: state.loadedPages + 1 });
  }

  protected retry(): void {
    this.applied = null;
    this.request(this.state());
  }

  protected openFilters(): void {
    this.drawerOpen.set(true);
  }

  protected closeFilters(): void {
    this.drawerOpen.set(false);
  }

  /**
   * Asks the store for the new state, appending rather than reloading when the only difference is
   * one more page — which is what keeps the shopper's scroll position after "Load More".
   */
  private request(next: ListingState): void {
    const previous = this.applied;
    this.applied = next;

    if (previous && sameFilters(previous, next)) {
      if (next.loadedPages === previous.loadedPages) {
        return;
      }
      if (next.loadedPages === previous.loadedPages + 1) {
        this.store.loadMore(next);
        return;
      }
    }

    this.store.load(next);
  }

  /** The route for a state: department, then the category's full path through the tree. */
  private commandsFor(next: ListingState): unknown[] {
    const department = next.department;
    if (!department) {
      return ['/'];
    }

    const tree =
      this.departments().find((candidate) => candidate.slug === department)?.categories ?? [];
    const trail = next.category ? trailTo(tree, next.category) : [];

    return ['/', department, ...trail.map((node) => node.slug)];
  }

}

/**
 * The category and its ancestors, nearest last. Searching the whole tree rather than trusting the
 * URL's middle segments means `/women/anything/jeans` still resolves to Women › Jeans instead of
 * drawing a breadcrumb out of a segment that names nothing.
 */
function trailTo(
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
