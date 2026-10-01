import { DestroyRef, Injectable, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, map } from 'rxjs';

import { CatalogApi, SearchFailure, searchErrorMessage, searchFailure } from '../catalog/catalog-api';
import { PagedResult, ProductCardDto } from '../catalog/catalog.model';
import { PAGE_SIZE, toRequestParams } from '../catalog/listing-state';
import { ServerHandover } from '../platform/server-handover';
import {
  SearchState,
  searchRequestKey,
  toSearchRequests,
} from './search-state';

/** One page of results, as it travels from the server render to the browser. */
type SearchPage = Pick<PagedResult<ProductCardDto>, 'items' | 'totalCount'>;

/** What the search is doing, which is what the page renders around. */
export type SearchPhase = 'idle' | 'loading' | 'refreshing' | 'appending' | 'ready' | 'error';

/**
 * The results one search is showing, and how they got there (SRCH-02).
 *
 * Deliberately a near-twin of `ProductListStore` rather than a shared base class: the two differ in
 * the endpoint, in what an error means (a rejected term is a prompt, not a failure) and in having an
 * "idle" state at all, and the one thing worth sharing — the paging arithmetic — already is shared,
 * through `toSearchRequests`. A base class would have had to be parameterised on all three.
 *
 * Provided by the search page, not in root: a torn-down page should take its results with it.
 *
 * The first request is issued synchronously, so it is already in flight while the server renders
 * and the HTML a shopper's WhatsApp link resolves to has the products in it. A stale response can
 * never overwrite a newer one: every load takes a ticket, and a response without the current ticket
 * is dropped.
 */
@Injectable()
export class ProductSearchStore {
  private readonly api = inject(CatalogApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly handover = inject(ServerHandover);

  private readonly products = signal<readonly ProductCardDto[]>([]);
  private readonly total = signal(0);
  private readonly phase = signal<SearchPhase>('idle');
  private readonly error = signal<string | null>(null);
  private readonly failure = signal<SearchFailure | null>(null);

  private ticket = 0;

  readonly items: Signal<readonly ProductCardDto[]> = this.products.asReadonly();

  /** Rows the term and the filters match in total, which is the number the page shows. */
  readonly totalCount: Signal<number> = this.total.asReadonly();

  readonly status: Signal<SearchPhase> = this.phase.asReadonly();

  /** A sentence for the shopper, never a status code. Null when the term itself was refused. */
  readonly errorMessage: Signal<string | null> = this.error.asReadonly();

  /** Why the last request failed, so the page can tell a refused term from a broken store. */
  readonly failureKind: Signal<SearchFailure | null> = this.failure.asReadonly();

  /** True before any term has been searched for: the page shows a prompt, not "no results". */
  readonly isIdle = computed(() => this.phase() === 'idle');

  /** True only when there is nothing on screen yet, which is the one case worth a placeholder. */
  readonly isLoading = computed(() => this.phase() === 'loading');

  /** True whenever a request is in flight, so the results can be dimmed and marked `aria-busy`. */
  readonly isBusy = computed(() => this.phase() === 'loading' || this.phase() === 'refreshing' || this.phase() === 'appending');

  /** True while "Load More" is fetching, so the button can say so without blanking the grid. */
  readonly isAppending = computed(() => this.phase() === 'appending');

  readonly hasError = computed(() => this.phase() === 'error');

  /** Whether "Load More" has anything left to add. */
  readonly hasMore = computed(() => this.products().length < this.total());

  /** Clears the results without asking for anything: what an unusable term leaves on screen. */
  reset(): void {
    this.ticket++;
    this.products.set([]);
    this.total.set(0);
    this.error.set(null);
    this.failure.set(null);
    this.phase.set('idle');
  }

  /** Replaces the results with the ones `state` describes, restoring every page the URL claims. */
  load(state: SearchState): void {
    const ticket = ++this.ticket;
    this.error.set(null);
    this.failure.set(null);

    const handedOver = this.takeFromServer(state);
    if (handedOver) {
      this.accept(ticket, handedOver.items, handedOver.totalCount);
      return;
    }

    // Rows already on screen stay there while the new ones are fetched: blanking the grid on every
    // filter change throws the page around under the shopper.
    this.phase.set(this.products().length === 0 ? 'loading' : 'refreshing');

    const wanted = state.listing.loadedPages * PAGE_SIZE;

    forkJoin(toSearchRequests(state).map((params) => this.api.search(params)))
      .pipe(
        map((pages) => merge(pages.map((page) => page.results), wanted)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (page) => {
          this.handOver(state, page);
          this.accept(ticket, page.items, page.totalCount);
        },
        error: (failure: unknown) => this.reject(ticket, failure),
      });
  }

  /**
   * Appends the next page without disturbing what is already on screen, so the scroll position
   * survives. The rows already held are an exact multiple of `PAGE_SIZE`, which is what makes plain
   * offset paging line up.
   */
  loadMore(state: SearchState): void {
    if (!this.hasMore() || this.phase() === 'appending') {
      return;
    }

    const ticket = ++this.ticket;
    this.phase.set('appending');
    this.error.set(null);
    this.failure.set(null);

    this.api
      .search({ ...toRequestParams(state.listing, state.listing.loadedPages, PAGE_SIZE), q: state.term })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) =>
          this.accept(ticket, [...this.products(), ...page.results.items], page.results.totalCount),
        error: (failure: unknown) => this.reject(ticket, failure),
      });
  }

  /**
   * The page the server rendered, handed over so the browser does not fetch it again. The key
   * covers the whole request, so a different term, filter, sort or page count is a different entry.
   */
  private takeFromServer(state: SearchState): SearchPage | null {
    return this.handover.take<SearchPage>(handoverKey(state));
  }

  private handOver(state: SearchState, page: SearchPage): void {
    this.handover.give(handoverKey(state), page);
  }

  private accept(ticket: number, items: readonly ProductCardDto[], totalCount: number): void {
    if (ticket !== this.ticket) {
      return;
    }

    this.products.set(items);
    this.total.set(totalCount);
    this.phase.set('ready');
  }

  private reject(ticket: number, failure: unknown): void {
    if (ticket !== this.ticket) {
      return;
    }

    const kind = searchFailure(failure);

    this.products.set([]);
    this.total.set(0);
    this.failure.set(kind);
    // A refused term is a prompt the page already knows how to draw, not a sentence about a fault.
    this.error.set(kind === 'term' ? null : searchErrorMessage(failure));
    this.phase.set('error');
  }
}

/** Where one search's server-rendered page waits for the browser. */
function handoverKey(state: SearchState): string {
  return `srch:${searchRequestKey(state)}`;
}

/**
 * Joins the restore requests back into the one page the URL asked for.
 *
 * The slice is what stops a restored `?pages=5` showing a product twice. It is the listing's own
 * lesson: the requests all use one page size, so the last one over-fetches by less than a page and
 * the tail is cut off here rather than by moving that request's window.
 */
function merge(pages: readonly PagedResult<ProductCardDto>[], wanted: number): SearchPage {
  return {
    items: pages.flatMap((page) => [...page.items]).slice(0, wanted),
    totalCount: pages[0]?.totalCount ?? 0,
  };
}
