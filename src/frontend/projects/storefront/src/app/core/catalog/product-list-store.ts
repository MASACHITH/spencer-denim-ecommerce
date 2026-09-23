import { isPlatformBrowser } from '@angular/common';
import {
  DestroyRef,
  Injectable,
  PLATFORM_ID,
  Signal,
  TransferState,
  computed,
  inject,
  makeStateKey,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, map } from 'rxjs';

import { CatalogApi, listingErrorMessage } from './catalog-api';
import { PagedResult, ProductCardDto } from './catalog.model';
import {
  ListingState,
  PAGE_SIZE,
  requestKey,
  toRequestParams,
  toRestoreRequests,
} from './listing-state';

/** One page of a listing, as it travels from the server render to the browser. */
type ListingPage = Pick<PagedResult<ProductCardDto>, 'items' | 'totalCount'>;

/** What the listing is doing, which is what the page renders around. */
export type ListingPhase = 'loading' | 'refreshing' | 'appending' | 'ready' | 'error';

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
  private readonly transferState = inject(TransferState);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

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

  /** True only when there is nothing on screen yet, which is the one case worth a placeholder. */
  readonly isLoading = computed(() => this.phase() === 'loading');

  /** True whenever a request is in flight, so the results can be dimmed and marked `aria-busy`. */
  readonly isBusy = computed(() => this.phase() !== 'ready' && this.phase() !== 'error');

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
    this.error.set(null);

    const handedOver = this.takeFromServer(state);
    if (handedOver) {
      this.accept(ticket, handedOver.items, handedOver.totalCount);
      return;
    }

    // Rows already on screen stay there while the new ones are fetched. Blanking the grid on
    // every filter change throws the page around under the shopper, and it is what turned a
    // hydration-time refetch into a visible flash of nothing.
    this.phase.set(this.products().length === 0 ? 'loading' : 'refreshing');

    const wanted = state.loadedPages * PAGE_SIZE;

    forkJoin(toRestoreRequests(state).map((params) => this.api.products(params)))
      .pipe(
        map((pages) => merge(pages, wanted)),
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

  /**
   * Hands the server's page to the browser through the hydration state, and takes it there.
   *
   * Angular's own HTTP transfer cache cannot do this for us here: it keys a response on the
   * request URL as it sees it, and it runs *after* the application's interceptors
   * (`HttpInterceptorHandler`: `[...HTTP_INTERCEPTOR_FNS, ...HTTP_ROOT_INTERCEPTOR_FNS]`), so the
   * absolute internal address `apiBaseUrlInterceptor` writes during SSR is stored under a key the
   * browser — which asks for the relative URL — can never look up. Without this, the browser
   * fetched every page of the catalogue a second time during hydration and replaced the grid it
   * had just been served with a loading message.
   *
   * The key covers the whole request, so a different filter, sort or page count is a different
   * entry, and it is consumed once: a later navigation back to the same listing fetches fresh
   * stock and prices rather than replaying the first render.
   */
  private takeFromServer(state: ListingState): ListingPage | null {
    if (!this.isBrowser) {
      return null;
    }

    const key = makeStateKey<ListingPage | null>(handoverKey(state));
    const page = this.transferState.get(key, null);
    if (page) {
      this.transferState.remove(key);
    }

    return page;
  }

  private handOver(state: ListingState, page: ListingPage): void {
    if (!this.isBrowser) {
      this.transferState.set(makeStateKey<ListingPage | null>(handoverKey(state)), page);
    }
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

/** Where one listing's server-rendered page waits for the browser. */
function handoverKey(state: ListingState): string {
  return `plp:${requestKey(state)}`;
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
