import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Params, Router, RouterLink } from '@angular/router';
import { BRAND, Drawer, Icon, brandPageTitle } from '@spencer-denim/shared';

import { LISTING_MODES, ListingMode, readListingMode } from './listing-mode';
import { ListingFilters } from './listing-filters';
import { ProductCard } from '../../ui/product-card/product-card';
import { ProductListStore } from '../../core/catalog/product-list-store';
import { SORT_OPTIONS, SortValue, readSort } from '../../core/catalog/product-sort';
import {
  ListingState,
  MAX_LOADED_PAGES,
  clampLoadedPages,
  clearFilters,
  readListingState,
  sameFilters,
  toQueryParams,
  withFilter,
} from '../../core/catalog/listing-state';
import { FilterChip, activeChips, removeChip } from '../../core/catalog/listing-chips';
import { CategoryNodeDto, DepartmentDto } from '../../core/navigation/catalog-departments.model';
import { NavigationService, findDepartment, trailTo } from '../../core/navigation/navigation.service';
import { PageMeta } from '../../core/platform/page-metadata';
import { Viewport } from '../../core/platform/viewport';

/**
 * The PLP-01 promo banner runs the full width of the container, which caps at 1440 px and has
 * gutters of 16 / 32 / 48 px. It sits above the sidebar split, so no filter column is taken off
 * it — the cards below are the narrow ones, not this.
 */
const PROMO_SIZES =
  '(min-width: 1440px) 1344px,' +
  ' (min-width: 1280px) calc(100vw - 96px),' +
  ' (min-width: 768px) calc(100vw - 64px),' +
  ' calc(100vw - 32px)';

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
            <img
              [src]="promo"
              [attr.srcset]="promoSrcSet()"
              [attr.sizes]="promoSrcSet() ? promoSizes : null"
              alt=""
            />
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
              [attr.aria-controls]="isDesktop() ? null : 'listing-filters'"
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
          <!-- One panel, not two. The sidebar and the drawer hold the same controls, and
               rendering both shipped every control twice in the server HTML and kept two copies
               of them in the DOM at every width. The Viewport service says why the server picks the
               sidebar and a phone swaps to the drawer a frame after hydration. -->
          @if (isDesktop()) {
            <aside class="plp__sidebar" aria-label="Filters">
              <sd-listing-filters
                [state]="state()"
                [departments]="departments()"
                [fixedFilter]="fixedFilter()"
                (changed)="apply($event)"
              />
            </aside>
          }

          <section
            class="plp__results"
            aria-labelledby="listing-results-title"
            [class.plp__results--busy]="store.isBusy()"
            [attr.aria-busy]="store.isBusy()"
          >
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
                } @else if (department(); as current) {
                  <p>
                    There is nothing in {{ heading() }} yet. Try another category from the menu, or
                    browse everything in {{ current.name }}.
                  </p>
                  <div class="plp__state-actions">
                    <a class="btn btn--primary" [routerLink]="'/' + current.slug">
                      Shop all {{ current.name }}
                    </a>
                  </div>
                } @else {
                  <p>There is nothing in {{ heading() }} just now. Try a department instead:</p>
                  <div class="plp__state-actions">
                    @for (candidate of departments(); track candidate.slug) {
                      <a class="btn btn--secondary" [routerLink]="'/' + candidate.slug">
                        {{ candidate.name }}
                      </a>
                    }
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
                <p class="plp__more-count">{{ shownSummary() }}</p>
                @if (canLoadMore()) {
                  <button
                    class="btn btn--secondary"
                    type="button"
                    [disabled]="store.isAppending()"
                    (click)="loadMore()"
                  >
                    {{ store.isAppending() ? 'Loading…' : 'Load More' }}
                  </button>
                } @else if (store.hasMore()) {
                  <!-- At the ceiling. Offering the button here would ask for a page the listing
                       will not load and leave the shopper wondering why nothing happened. -->
                  <p class="plp__more-count">
                    That is as many as one page shows. Narrow the filters to reach the rest.
                  </p>
                }
              </div>
            }
          </section>
        </div>
      </div>

      @if (!isDesktop()) {
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
            [fixedFilter]="fixedFilter()"
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
    /* A listing that is being refetched keeps its rows and says so, rather than emptying. */
    .plp__results--busy {
      opacity: 0.6;
      transition: opacity var(--duration) var(--ease);
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
  private readonly pageMeta = inject(PageMeta);

  /** Which container the single filter panel goes in. See {@link Viewport}. */
  protected readonly isDesktop = inject(Viewport).isDesktop;

  protected readonly promoSizes = PROMO_SIZES;

  protected readonly store = inject(ProductListStore);
  protected readonly sortOptions = SORT_OPTIONS;

  private readonly routeParams = toSignal(this.route.params, {
    initialValue: this.route.snapshot.params,
  });

  private readonly queryParams = toSignal(this.route.queryParams, {
    initialValue: this.route.snapshot.queryParams,
  });

  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  /** `new-arrivals` or `sale` when the route is one of those, otherwise `null` (DEPT-02, DEPT-03). */
  protected readonly mode = computed<ListingMode | null>(() =>
    readListingMode(this.routeData()['listing']),
  );

  /** The filter this listing's address fixes, which the panel does not offer as a choice. */
  protected readonly fixedFilter = computed(() => {
    const mode = this.mode();
    return mode === null ? null : LISTING_MODES[mode].fixedFilter;
  });

  /**
   * The listing the URL describes. The deepest path segment is the category the API filters on:
   * the mega menu links three levels deep for Kids (`/kids/girls/girls-dresses`), and the API
   * returns a category's whole subtree, so the last segment is the only one it needs.
   */
  protected readonly state = computed<ListingState>(() => {
    const params = this.routeParams();
    const state = readListingState(
      params['department'],
      params['subcategory'] ?? params['category'],
      this.queryParams(),
    );

    const mode = this.mode();
    return mode === null ? state : LISTING_MODES[mode].apply(state);
  });

  protected readonly departments = this.navigation.departments;

  private readonly placement = computed<Placement>(() => {
    const state = this.state();
    const department = findDepartment(this.departments(), state.department) ?? null;

    return {
      department,
      trail: department && state.category ? trailTo(department.categories, state.category) : [],
    };
  });

  protected readonly department = computed(() => this.placement().department);

  /** The category the URL names, when the tree knows it. */
  private readonly category = computed(() => this.placement().trail.at(-1) ?? null);

  protected readonly heading = computed(() => {
    const mode = this.mode();
    if (mode !== null) {
      return LISTING_MODES[mode].heading;
    }

    return this.category()?.name ?? this.department()?.name ?? 'Products';
  });

  protected readonly description = computed(() => {
    const mode = this.mode();
    if (mode !== null) {
      return LISTING_MODES[mode].description;
    }

    return this.category()?.description ?? this.department()?.description ?? null;
  });

  protected readonly promoImage = computed(
    () => this.category()?.imageUrl ?? this.department()?.heroImageUrl ?? null,
  );

  /**
   * The candidate list of whichever picture {@link promoImage} settled on — a category's own
   * artwork and its department's hero are different assets, so the two must not be mixed.
   */
  protected readonly promoSrcSet = computed(() => {
    const category = this.category();
    return category?.imageUrl ? category.imageSrcSet : (this.department()?.heroImageSrcSet ?? null);
  });

  protected readonly breadcrumb = computed<readonly Crumb[]>(() => {
    const mode = this.mode();
    if (mode !== null) {
      return [
        { label: 'Home', path: '/' },
        { label: LISTING_MODES[mode].heading, path: LISTING_MODES[mode].path },
      ];
    }

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
  /**
   * The chips a shopper may take off. On New Arrivals and Sale the page's own filter is not one
   * of them — it is the address — but a discount the shopper chose *on top* of Sale still is, and
   * removing it drops back to the plain sale listing.
   */
  protected readonly chips = computed<readonly FilterChip[]>(() => {
    const chips = activeChips(this.state());
    const fixed = this.fixedFilter();
    if (fixed === null) {
      return chips;
    }

    const narrowed = this.state().minDiscountPercent !== null;
    return chips.filter(
      (chip) => chip.kind !== fixed || (fixed === 'discount' && narrowed),
    );
  });

  /** What the shopper can undo, which is exactly what the chips offer. */
  protected readonly filtersActive = computed(() => this.chips().length > 0);

  /** PLP-07: there is more to load, and the listing has not reached its ceiling. */
  protected readonly canLoadMore = computed(
    () => this.store.hasMore() && this.state().loadedPages < MAX_LOADED_PAGES,
  );

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
    const mode = this.mode();
    if (mode !== null) {
      return LISTING_MODES[mode].title;
    }

    const department = this.department();
    if (!department) {
      return brandPageTitle('Products');
    }

    const category = this.category();
    if (category) {
      return brandPageTitle(`${category.name} | ${department.name}`);
    }

    // An admin-written SEO title is the whole title, brand included — the seeded Women department's is
    // brandPageTitle("Women's clothing") already — so appending the brand would say it twice.
    return department.seoTitle ?? brandPageTitle(department.name);
  });

  private readonly metaDescription = computed(() => {
    const mode = this.mode();
    if (mode !== null) {
      return LISTING_MODES[mode].metaDescription;
    }

    const department = this.department();
    if (!department) {
      return `Shop ${BRAND.name}.`;
    }

    const category = this.category();
    return (
      category?.description ??
      department.seoDescription ??
      department.description ??
      `Shop ${this.heading()} at ${BRAND.name}.`
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
      const canonical = this.canonicalUrl();
      // The same picture the page draws at the top of itself — a category's own artwork, or its
      // department's hero. A listing has no other image that is about the listing.
      const image = this.promoImage();
      untracked(() => {
        this.pageMeta.set({
          title,
          description,
          path: canonical,
          imageUrl: image,
          imageAlt: null,
        });
      });
    });

    // The next page owns its own canonical address and card, or has none. Leaving the canonical behind
    // made the home page claim to be the listing the shopper had just come from.
    inject(DestroyRef).onDestroy(() => this.pageMeta.clear());
  }

  protected apply(next: ListingState): void {
    this.router.navigate(this.commandsFor(next), { queryParams: this.queryParamsFor(next) });
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

  /**
   * PLP-07: one more page, recorded in the URL so the depth survives a share and a reload.
   *
   * Clamped, because asking for one past the ceiling used to be read back as page 1 and collapsed
   * a listing of 480 products to 24. The button is hidden at the ceiling too; this is the guard
   * behind it.
   */
  protected loadMore(): void {
    const state = this.state();
    const loadedPages = clampLoadedPages(state.loadedPages + 1);
    if (loadedPages === state.loadedPages) {
      return;
    }

    this.apply({ ...state, loadedPages });
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

  /**
   * The query string for a state. On New Arrivals and Sale the page's own filter is the address,
   * so writing it into the query as well would only make the URL longer and less shareable.
   */
  private queryParamsFor(next: ListingState): Params {
    const params = toQueryParams(next);
    const mode = this.mode();

    if (mode === 'new-arrivals') {
      params['newArrivals'] = null;
    }
    if (mode === 'sale') {
      params['onSale'] = null;
    }

    return params;
  }

  /** The route for a state: department, then the category's full path through the tree. */
  private commandsFor(next: ListingState): unknown[] {
    const mode = this.mode();
    if (mode !== null && next.department === null) {
      return [LISTING_MODES[mode].path];
    }

    const department = next.department;
    if (!department) {
      return ['/'];
    }

    const tree = findDepartment(this.departments(), department)?.categories ?? [];
    const trail = next.category ? trailTo(tree, next.category) : [];

    return ['/', department, ...trail.map((node) => node.slug)];
  }

  /**
   * The one address this listing should be indexed at.
   *
   * Relative on purpose: the storefront sits behind a reverse proxy and the render has no
   * reliable idea of the public origin, and a relative `canonical` is resolved against the
   * document just as well. It drops `pages`, because "Load More" shows the same products further
   * down rather than different ones, and it rebuilds the path from the catalogue's own slugs, so
   * `/WOMEN/jeans` and `/women/jeans` stop being two pages with the same contents.
   */
  private readonly canonicalUrl = computed(() => {
    const state = this.state();
    // `commandsFor` speaks the router's language — a leading '/' segment, and a whole path for
    // the fixed listings — so the segments are flattened back into one before they are joined.
    const path = this.commandsFor(state)
      .map((segment) => String(segment))
      .flatMap((segment) => segment.split('/'))
      .filter((segment) => segment.length > 0)
      .join('/');
    const query = new URLSearchParams();

    for (const [key, value] of Object.entries(this.queryParamsFor({ ...state, loadedPages: 1 }))) {
      if (value === null || value === undefined) {
        continue;
      }
      for (const entry of Array.isArray(value) ? value : [value]) {
        query.append(key, String(entry));
      }
    }

    const search = query.toString();
    return `/${path}${search ? `?${search}` : ''}`;
  });


}
