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
import { Meta, Title } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BRAND, Drawer, Icon, brandPageTitle } from '@spencer-denim/shared';

import { HeaderSearch } from '../../layout/header-search/header-search';
import { ListingFilters } from '../listing/listing-filters';
import { ProductCard } from '../../ui/product-card/product-card';
import { CanonicalLink } from '../../core/platform/canonical-link';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { Viewport } from '../../core/platform/viewport';
import { FilterChip, activeChips, removeChip } from '../../core/catalog/listing-chips';
import { ListingState, MAX_LOADED_PAGES, clampLoadedPages } from '../../core/catalog/listing-state';
import { SORT_OPTIONS, SortValue, readSort } from '../../core/catalog/product-sort';
import { NavigationService, findDepartment, trailTo } from '../../core/navigation/navigation.service';
import { ProductSearchStore } from '../../core/search/product-search-store';
import { RecentSearches } from '../../core/search/recent-searches.service';
import {
  SearchState,
  clearSearchFilters,
  hasActiveSearchFilters,
  readSearchState,
  sameSearch,
  toSearchQueryParams,
  withSearchFilters,
} from '../../core/search/search-state';
import { PopularSearches, SearchSubmissions } from '../../core/search/popular-searches';
import { MIN_TERM_LENGTH, isSearchable } from '../../core/search/search-term';

/**
 * The search results page (SRCH-02).
 *
 * **The URL is the state**, exactly as on a listing: the term, every filter, the sort and how much
 * has been loaded are read out of the query string and written back into it. That is what makes a
 * search shareable — a link pasted into WhatsApp resolves to a server-rendered page with the
 * products in the HTML — bookmarkable, and reversible with the browser's Back button.
 *
 * **It answers 200 for everything**, including a term that matches nothing and a term that is too
 * short. It deliberately acquires neither of the two behaviours `app.routes.ts` grew for listings
 * (#30): a search with no hits is not a missing page, so a 404 with `noindex` would be a lie, and
 * nothing about a search is "unconfirmed", so there is no 503 either. What it does set is
 * `noindex` — an internal search results page is unbounded crawl space over content that is already
 * indexed at its own addresses, and it carries no canonical address of its own for the same reason.
 *
 * **The filters are the listing's own component and the listing's own state mapping.** The one
 * difference is that the department and the category are query parameters here rather than path
 * segments, so they are filters: they get chips, "Clear filters" takes them off, and the panel
 * offers "All departments" (see `search-state.ts`).
 */
@Component({
  selector: 'sd-search-page',
  imports: [Drawer, HeaderSearch, Icon, ListingFilters, ProductCard, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ProductSearchStore],
  template: `
    <div class="container search">
      <nav class="breadcrumb" aria-label="Breadcrumb">
        <ol>
          <li><a routerLink="/">Home</a></li>
          <li><span aria-current="page">Search</span></li>
        </ol>
      </nav>

      <header class="search__head">
        <h1 class="search__title">
          @if (hasTerm()) {
            Results for “{{ term() }}”
          } @else {
            Search
          }
        </h1>

        <!-- The box is on the page as well as in the header, and on a phone that is the point: the
             header's field is behind a toggle below 1280 px, so a shopper who opened a shared link
             would otherwise have no way to see or change what was searched for. -->
        <sd-header-search
          class="search__field"
          formLabel="Search the shop"
          placeholder="Search for products"
          [initialTerm]="term()"
        />
      </header>

      @if (!hasTerm()) {
        <div class="search__state">
          <p class="search__state-title">What are you looking for?</p>
          <p>
            Type at least {{ minTermLength }} characters in the box above — a product name, a
            category, a colour or a material.
          </p>
          <!-- What other shoppers search for most (#78). Drawn only once the list has arrived in the
               browser, and not at all in a shop that has none yet: no heading over nothing. -->
          @if (popularTerms().length > 0) {
            <p class="search__popular-title" id="search-popular-heading">Popular searches</p>
            <ul class="chips search__popular" aria-labelledby="search-popular-heading">
              @for (popular of popularTerms(); track popular) {
                <li>
                  <a class="chip" routerLink="/search" [queryParams]="{ q: popular }">{{ popular }}</a>
                </li>
              }
            </ul>
          }
          <div class="search__state-actions">
            @for (department of departments(); track department.slug) {
              <a class="btn btn--secondary" [routerLink]="'/' + department.slug">
                {{ department.name }}
              </a>
            }
            <a class="btn btn--secondary" routerLink="/new-arrivals">New Arrivals</a>
            <a class="btn btn--secondary" routerLink="/sale">Sale</a>
          </div>
        </div>
      } @else {
        <div class="search__bar">
          <p class="search__count">
            <strong>{{ totalCount() }}</strong>
            {{ totalCount() === 1 ? 'product' : 'products' }}
          </p>

          <div class="search__controls">
            <button
              class="btn btn--secondary search__filters-btn"
              type="button"
              [attr.aria-controls]="isDesktop() ? null : 'search-filters'"
              [attr.aria-expanded]="filtersOpen()"
              (click)="openFilters()"
            >
              <sd-icon name="filter" size="sm" />
              Filters
              @if (chips().length > 0) {
                <span class="search__pill" aria-hidden="true">{{ chips().length }}</span>
                <span class="sr-only">, {{ chips().length }} applied</span>
              }
            </button>

            <div class="search__sort">
              <label class="search__sort-label" for="search-sort">Sort by</label>
              <select
                id="search-sort"
                class="search__select"
                [value]="state().listing.sort"
                (change)="onSortChange($event)"
              >
                @for (option of sortOptions; track option.value) {
                  <option [value]="option.value" [selected]="option.value === state().listing.sort">
                    {{ option.label }}
                  </option>
                }
              </select>
            </div>
          </div>
        </div>

        @if (chips().length > 0) {
          <div class="search__chips">
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
            <button class="search__clear" type="button" (click)="onClearFilters()">
              Clear filters
            </button>
          </div>
        }

        <div class="search__layout">
          <!-- One panel, not two: the sidebar and the drawer hold the same controls, and rendering
               both shipped every control twice in the server HTML on the listing. -->
          @if (isDesktop()) {
            <aside class="search__sidebar" aria-label="Filters">
              <sd-listing-filters
                [state]="state().listing"
                [departments]="departments()"
                [allowAnyDepartment]="true"
                (changed)="applyFilters($event)"
              />
            </aside>
          }

          <section
            class="search__results"
            aria-labelledby="search-results-title"
            [class.search__results--busy]="store.isBusy()"
            [attr.aria-busy]="store.isBusy()"
          >
            <h2 class="sr-only" id="search-results-title">Results</h2>

            <p class="sr-only" role="status">{{ announcement() }}</p>

            @if (store.hasError() && store.errorMessage(); as message) {
              <div class="search__state">
                <p class="search__state-title">We could not run that search</p>
                <p>{{ message }}</p>
                <div class="search__state-actions">
                  <button class="btn btn--primary" type="button" (click)="retry()">Try again</button>
                  @if (filtersActive()) {
                    <button class="btn btn--secondary" type="button" (click)="onClearFilters()">
                      Clear filters
                    </button>
                  }
                </div>
              </div>
            } @else if (store.isLoading()) {
              <p class="search__state">Searching…</p>
            } @else if (products().length === 0) {
              <div class="search__state">
                <p class="search__state-title">We found nothing for “{{ term() }}”</p>
                @if (filtersActive()) {
                  <p>
                    Nothing matches that search and all {{ chips().length }} of your filters. Take
                    one off, or search the whole shop:
                  </p>
                  <ul class="chips search__state-chips" aria-label="Filters you can remove">
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
                  <div class="search__state-actions">
                    <button class="btn btn--primary" type="button" (click)="onClearFilters()">
                      Search all products
                    </button>
                  </div>
                } @else {
                  <ul class="search__tips">
                    <li>Check the spelling.</li>
                    <li>Try fewer words — every word has to match.</li>
                    <li>Try a more general word, like “dress” instead of “midi dress”.</li>
                  </ul>
                  <p>Or start from a department:</p>
                  <div class="search__state-actions">
                    @for (department of departments(); track department.slug) {
                      <a class="btn btn--secondary" [routerLink]="'/' + department.slug">
                        {{ department.name }}
                      </a>
                    }
                    <a class="btn btn--secondary" routerLink="/new-arrivals">New Arrivals</a>
                    <a class="btn btn--secondary" routerLink="/sale">Sale</a>
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

              <div class="search__more">
                <p class="search__more-count">{{ shownSummary() }}</p>
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
                  <p class="search__more-count">
                    That is as many as one page shows. Narrow the filters to reach the rest.
                  </p>
                }
              </div>
            }
          </section>
        </div>
      }
    </div>

    @if (!isDesktop()) {
      <sd-drawer
        [open]="filtersOpen()"
        panelId="search-filters"
        heading="Filters"
        side="end"
        closeLabel="Close filters"
        (closed)="closeFilters()"
      >
        <sd-listing-filters
          [state]="state().listing"
          [departments]="departments()"
          [allowAnyDepartment]="true"
          (changed)="applyFilters($event)"
        />
        <div class="search__drawer-actions">
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
  styles: `
    :host {
      display: block;
    }
    .search {
      padding-block: var(--space-3) var(--space-8);
    }
    .search__popular-title {
      margin-block-start: var(--space-3);
      color: var(--color-text-muted);
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
    }
    .search__popular {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      margin-block-start: var(--space-1);
      padding: 0;
      list-style: none;
    }
    .search__popular .chip {
      max-width: 100%;
      overflow-wrap: anywhere;
    }
    .search__head {
      display: grid;
      gap: var(--space-2);
      padding-block: var(--space-1) var(--space-3);
    }
    .search__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
      /* A pasted term can be one hundred characters with no spaces in it. */
      overflow-wrap: anywhere;
    }
    .search__field {
      max-width: 520px;
    }
    .search__bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1-5) var(--space-2);
      padding-block: var(--space-1-5);
      border-block: 1px solid var(--color-border);
    }
    .search__count {
      font-size: var(--text-small);
    }
    .search__controls {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: flex-end;
      gap: var(--space-1-5);
      flex: 1 1 auto;
    }
    .search__filters-btn {
      min-height: var(--tap);
      padding-inline: var(--space-2);
    }
    .search__pill {
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
    @media (min-width: 1024px) {
      .search__filters-btn {
        display: none;
      }
    }
    .search__sort {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      flex: 1 1 220px;
      max-width: 320px;
    }
    .search__sort-label {
      font-size: var(--text-small);
      white-space: nowrap;
    }
    .search__select {
      flex: 1;
      min-width: 0;
      min-height: var(--tap);
      padding: 0 var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-surface);
      color: var(--color-text);
      font: inherit;
      font-size: var(--text-small);
    }
    .search__chips {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1);
      padding-block: var(--space-1-5);
    }
    .search__clear {
      min-height: var(--tap);
      padding-inline: var(--space-1);
      border: 0;
      background: transparent;
      color: var(--color-text);
      font-size: var(--text-small);
      text-decoration: underline;
      cursor: pointer;
    }
    .search__layout {
      display: grid;
      gap: var(--space-3);
      padding-top: var(--space-2);
    }
    @media (min-width: 1024px) {
      .search__layout {
        grid-template-columns: 240px minmax(0, 1fr);
        gap: var(--space-4);
      }
    }
    @media (min-width: 1440px) {
      .search__layout {
        grid-template-columns: 280px minmax(0, 1fr);
      }
    }
    .search__sidebar {
      min-width: 0;
    }
    .search__results {
      min-width: 0;
    }
    .search__results--busy {
      opacity: 0.6;
    }
    @media (prefers-reduced-motion: no-preference) {
      .search__results {
        transition: opacity var(--duration) var(--ease);
      }
    }
    .search__state {
      display: grid;
      justify-items: start;
      gap: var(--space-1-5);
      max-width: 62ch;
      padding-block: var(--space-4) var(--space-6);
      color: var(--color-text-muted);
    }
    .search__state-title {
      color: var(--color-text);
      font-size: var(--text-h3);
    }
    .search__tips {
      display: grid;
      gap: var(--space-0-5);
      padding-inline-start: var(--space-3);
      list-style: disc;
    }
    .search__state-chips {
      padding-block: var(--space-0-5);
    }
    .search__state-actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
      padding-top: var(--space-1);
    }
    .search__more {
      display: grid;
      justify-items: center;
      gap: var(--space-1-5);
      padding-top: var(--space-4);
    }
    .search__more-count {
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }
    .search__drawer-actions {
      display: grid;
      gap: var(--space-1);
      padding-top: var(--space-2);
    }
  `,
})
export class SearchPage {
  protected readonly store = inject(ProductSearchStore);
  protected readonly sortOptions = SORT_OPTIONS;
  protected readonly minTermLength = MIN_TERM_LENGTH;

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly navigation = inject(NavigationService);
  private readonly recentSearches = inject(RecentSearches);
  private readonly submissions = inject(SearchSubmissions);
  private readonly popularSearches = inject(PopularSearches);

  /** The shop's popular searches (#78), for the page with no term. */
  protected readonly popularTerms = this.popularSearches.terms;
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);
  private readonly viewport = inject(Viewport);

  protected readonly isDesktop = this.viewport.isDesktop;
  protected readonly departments = this.navigation.departments;

  private readonly queryParams = toSignal(this.route.queryParams, {
    initialValue: this.route.snapshot.queryParams,
  });

  protected readonly state = computed<SearchState>(() => readSearchState(this.queryParams()));

  protected readonly term = computed(() => this.state().term);

  /** Whether there is a term the API would accept. Nothing is requested without one. */
  protected readonly hasTerm = computed(() => isSearchable(this.term()));

  protected readonly products = this.store.items;
  protected readonly totalCount = this.store.totalCount;

  /** The department and the category are filters here, so they are chips (see `search-state.ts`). */
  protected readonly chips = computed<readonly FilterChip[]>(() => {
    const listing = this.state().listing;
    const department = findDepartment(this.departments(), listing.department);
    const category =
      department && listing.category ? trailTo(department.categories, listing.category).at(-1) : null;

    return activeChips(listing, {
      placement: true,
      departmentName: department?.name ?? null,
      categoryName: category?.name ?? null,
    });
  });

  protected readonly filtersActive = computed(() => hasActiveSearchFilters(this.state()));

  protected readonly canLoadMore = computed(
    () => this.store.hasMore() && this.state().listing.loadedPages < MAX_LOADED_PAGES,
  );

  protected readonly shownSummary = computed(
    () => `Showing ${this.products().length} of ${this.totalCount()} products`,
  );

  /** What a screen reader hears whenever the result set changes (SRCH-01/02). */
  protected readonly announcement = computed(() => {
    if (!this.hasTerm()) {
      return '';
    }
    if (this.store.isLoading()) {
      return 'Searching';
    }
    if (this.store.hasError()) {
      return this.store.errorMessage() ?? '';
    }
    if (this.totalCount() === 0) {
      return `No products match ${this.term()}.`;
    }

    return `${this.shownSummary()} for ${this.term()}.`;
  });

  // Keyed on hasTerm(), not on the term being non-empty: a one-character term is a page that has
  // not searched for anything yet, and titling it "Search: a" would describe results it never asked
  // for.
  private readonly documentTitle = computed(() =>
    this.hasTerm() ? brandPageTitle(`Search: ${this.term()}`) : brandPageTitle('Search'),
  );

  private readonly metaDescription = computed(() =>
    this.hasTerm()
      ? `Products matching “${this.term()}” at ${BRAND.name}.`
      : `Search the ${BRAND.name} shop.`,
  );

  private readonly drawerOpen = signal(false);
  protected readonly filtersOpen = this.drawerOpen.asReadonly();

  /** What the store was last asked for, so an added page appends instead of reloading. */
  private applied: SearchState | null = null;

  constructor() {
    // Before the first change detection, so the request is already in flight while the server
    // renders: a shared /search?q=... link has to resolve to HTML with the products in it.
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

    // A results page is unbounded crawl space over products that are already indexed at their own
    // addresses, so it asks not to be indexed — and it has no canonical address of its own, so any
    // tag the previous page left behind is removed rather than inherited.
    const robots = inject(RobotsMeta);
    const canonical = inject(CanonicalLink);
    robots.noindex();
    canonical.clear();
    inject(DestroyRef).onDestroy(() => robots.clear());

    // Remembered here rather than only in the search box, so a search reached by any route — a
    // shared link, Back, a suggestion — is in the history the overlay offers. The service drops
    // anything that looks like an email address or a telephone number.
    //
    // And counted, once per term this page shows, for popular searches (#78; doc 08 DB25 "results-page
    // requests"). A filter, the sort or Load More changes the state and not the term, so they are not
    // counted again; `SearchSubmissions` does nothing on the server, so a server render and the browser
    // that hydrates it are one count, not two.
    effect(() => {
      const term = this.term();
      untracked(() => {
        if (isSearchable(term)) {
          this.recentSearches.remember(term);
          this.submissions.record(term);
        } else {
          this.popularSearches.load();
        }
      });
    });
  }

  protected applyFilters(listing: ListingState): void {
    this.apply(withSearchFilters(this.state(), listing));
  }

  protected onSortChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const listing = this.state().listing;
    this.apply(
      withSearchFilters(this.state(), { ...listing, sort: readSort(value) satisfies SortValue }),
    );
  }

  protected onRemoveChip(chip: FilterChip): void {
    this.apply(withSearchFilters(this.state(), removeChip(this.state().listing, chip)));
  }

  protected onClearFilters(): void {
    this.apply(clearSearchFilters(this.state()));
  }

  /**
   * One more page, recorded in the URL so the depth survives a share and a reload. Clamped: asking
   * for one past the ceiling used to be read back as page 1 on the listing and collapsed it.
   */
  protected loadMore(): void {
    const state = this.state();
    const loadedPages = clampLoadedPages(state.listing.loadedPages + 1);
    if (loadedPages === state.listing.loadedPages) {
      return;
    }

    this.apply({ term: state.term, listing: { ...state.listing, loadedPages } });
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

  private apply(next: SearchState): void {
    void this.router.navigate(['/search'], { queryParams: toSearchQueryParams(next) });
  }

  /**
   * Asks the store for the new state, appending rather than reloading when the only difference is
   * one more page — which is what keeps the shopper's scroll position after "Load More".
   */
  private request(next: SearchState): void {
    if (!isSearchable(next.term)) {
      // No request at all: the API would answer 400, and the page has a prompt for this.
      this.applied = null;
      this.store.reset();
      return;
    }

    const previous = this.applied;
    this.applied = next;

    if (previous && sameSearch(previous, next)) {
      if (next.listing.loadedPages === previous.listing.loadedPages) {
        return;
      }
      if (next.listing.loadedPages === previous.listing.loadedPages + 1) {
        this.store.loadMore(next);
        return;
      }
    }

    this.store.load(next);
  }
}
