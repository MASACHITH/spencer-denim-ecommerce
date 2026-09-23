import { DestroyRef, Injectable, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, map } from 'rxjs';

import { CatalogApi, listingErrorMessage } from './catalog-api';
import { PagedResult, ProductCardDto } from './catalog.model';
import {
  ListingState,
  PAGE_SIZE,
  toRequestParams,
  toRestoreRequests,
} from './listing-state';

/** What the listing is doing, which is what the page renders around. */
export type ListingPhase = 'loading' | 'appending' | 'ready' | 'error';

/**
 * The products a listing is showing, and how they got there (PLP-06, PLP-07).
 *
 * Provided by the listing page rather than in root: two listings never share a result set, and a
 * page that is torn down should take its state with it.
 *
 * Requests are issued synchronously from {@link load}, so the first one is already in flight
 * while the server renders — `HttpClient` holds the application "unstable" until it answers, and
 * the HTML a crawler receives has the products in it. A stale response can never overwrite a
 * newer one: every load takes a ticket, and a response without the current ticket is dropped.
 */
@Injectable()
export class ProductListStore {
  private readonly api = inject(CatalogApi);
  private readonly destroyRef = inject(DestroyRef);

  private readonly products = signal<readonly ProductCardDto[]>([]);
  private readonly total = signal(0);
  private readonly phase = signal<ListingPhase>('loading');
  private readonly error = signal<string | null>(null);

  private ticket = 0;

  readonly items: Signal<readonly ProductCardDto[]> = this.products.asReadonly();

  /** Rows the filter matches in total, which is the number PLP-06 shows. */
  readonly totalCount: Signal<number> = this.total.asReadonly();

  readonly status: Signal<ListingPhase> = this.phase.asReadonly();

  /** A sentence for the shopper, never a status code (PLP-06, error handling). */
  readonly errorMessage: Signal<string | null> = this.error.asReadonly();

  /** True while the first page of a new filter set is on its way. */
  readonly isLoading = computed(() => this.phase() === 'loading');

  /** True while "Load More" is fetching, so the button can say so without blanking the grid. */
  readonly isAppending = computed(() => this.phase() === 'appending');

  readonly hasError = computed(() => this.phase() === 'error');

  /** Whether "Load More" has anything left to add (PLP-07). */
  readonly hasMore = computed(() => this.products().length < this.total());

  /**
   * Replaces the listing with the products `state` describes, restoring every page the URL says
   * has been loaded.
   */
  load(state: ListingState): void {
    const ticket = ++this.ticket;
    this.phase.set('loading');
    this.error.set(null);

    const wanted = state.loadedPages * PAGE_SIZE;

    forkJoin(toRestoreRequests(state).map((params) => this.api.products(params)))
      .pipe(
        map((pages) => merge(pages, wanted)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (page) => this.accept(ticket, page.items, page.totalCount),
        error: (failure: unknown) => this.reject(ticket, failure),
      });
  }

  /**
   * Appends the next page without disturbing what is already on screen, so the scroll position
   * survives (PLP-07). The rows already held are an exact multiple of {@link PAGE_SIZE}, which is
   * what makes plain offset paging line up.
   */
  loadMore(state: ListingState): void {
    if (!this.hasMore() || this.phase() === 'appending') {
      return;
    }

    const ticket = ++this.ticket;
    this.phase.set('appending');
    this.error.set(null);

    this.api
      .products(toRequestParams(state, state.loadedPages, PAGE_SIZE))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => this.accept(ticket, [...this.products(), ...page.items], page.totalCount),
        error: (failure: unknown) => this.reject(ticket, failure),
      });
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

    this.products.set([]);
    this.total.set(0);
    this.error.set(listingErrorMessage(failure));
    this.phase.set('error');
  }
}

/** Joins the restore requests back into the one page the URL asked for. */
function merge(
  pages: readonly PagedResult<ProductCardDto>[],
  wanted: number,
): { items: readonly ProductCardDto[]; totalCount: number } {
  return {
    items: pages.flatMap((page) => [...page.items]).slice(0, wanted),
    totalCount: pages[0]?.totalCount ?? 0,
  };
}
