import { isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Observable, catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';

import { CatalogApi } from '../catalog/catalog-api';
import { ProductCardDto, ProductSearchResponse } from '../catalog/catalog.model';
import { SUGGESTION_PAGE_SIZE } from './search-suggestions';
import { isSearchable, normaliseTerm } from './search-term';

/**
 * How long the box waits after the last keystroke (SRCH-01, "debounced suggestions").
 *
 * 200 ms is about one keystroke at an ordinary typing speed, so a word typed straight through costs
 * one request rather than six, and the list still arrives while the shopper is still looking at it.
 */
export const SUGGESTION_DEBOUNCE_MS = 200;

/** What the suggestion request is doing. */
export type SuggestionPhase = 'idle' | 'loading' | 'ready' | 'failed';

/**
 * The product suggestions for whatever is in the search box (SRCH-01).
 *
 * **It uses `GET /api/catalog/search`, the endpoint W1-02 already shipped, and no new one.** Doc 08
 * §7.6 describes suggestions as "`TOP 6` products by `NameText` prefix or word prefix"; the search
 * endpoint's own relevance score puts a name prefix first (100), then a name match (50), then a
 * category or colour match (20) — so asking it for six rows by relevance returns the same products
 * in the same order as a dedicated endpoint would, plus the ones a shopper would also expect to see.
 * The other two blocks need no server at all: categories come out of the department tree the
 * storefront already holds, and recent searches out of this browser's own storage.
 *
 * **Nothing is fetched on the server.** A suggestion list only exists in response to typing, and an
 * outstanding `HttpClient` call holds the SSR render "unstable" — so a server that subscribed here
 * would delay the first byte of a page for a list that cannot be on it.
 *
 * Provided by the combobox component, not in root: two boxes on one page (the header and the one on
 * the results page) each want their own, and a torn-down box should take its request with it.
 */
@Injectable()
export class SuggestionStore {
  private readonly api = inject(CatalogApi);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly term = signal('');
  private readonly cards = signal<readonly ProductCardDto[]>([]);
  private readonly phase = signal<SuggestionPhase>('idle');

  /** The term the suggestions describe, normalised. */
  readonly searchedTerm: Signal<string> = this.term.asReadonly();

  readonly products: Signal<readonly ProductCardDto[]> = this.cards.asReadonly();

  readonly status: Signal<SuggestionPhase> = this.phase.asReadonly();

  readonly isLoading = computed(() => this.phase() === 'loading');

  constructor() {
    if (!this.isBrowser) {
      return;
    }

    toObservable(this.term)
      .pipe(
        debounceTime(SUGGESTION_DEBOUNCE_MS),
        distinctUntilChanged(),
        switchMap((term) => this.fetch(term)),
        takeUntilDestroyed(),
      )
      .subscribe((response) => {
        // A request that was superseded never arrives here: `switchMap` unsubscribes from it.
        if (response === null) {
          this.cards.set([]);
          this.phase.set('failed');
          return;
        }

        this.cards.set(response.results.items);
        this.phase.set('ready');
      });
  }

  /**
   * The term to suggest for. Called on every keystroke; the debounce is inside.
   *
   * A term the API would refuse clears the list **synchronously**, so a shopper who deletes back to
   * one character does not keep looking at suggestions for a word they have removed while a timer
   * runs down.
   */
  suggestFor(raw: string): void {
    const term = normaliseTerm(raw);
    this.term.set(term);

    if (!isSearchable(term)) {
      this.cards.set([]);
      this.phase.set('idle');
      return;
    }

    this.phase.set('loading');
  }

  /** Forgets the term and the list, for a box that has been closed and reset. */
  reset(): void {
    this.term.set('');
    this.cards.set([]);
    this.phase.set('idle');
  }

  /** `null` means the request failed; an unusable term is answered with an empty list. */
  private fetch(term: string): Observable<ProductSearchResponse | null> {
    if (!isSearchable(term)) {
      return of(EMPTY_RESPONSE);
    }

    return this.api
      .search({ q: term, pageSize: String(SUGGESTION_PAGE_SIZE), sort: 'recommended' })
      .pipe(catchError(() => of(null)));
  }
}

const EMPTY_RESPONSE: ProductSearchResponse = {
  term: '',
  tokens: [],
  results: { items: [], page: 1, pageSize: SUGGESTION_PAGE_SIZE, totalCount: 0 },
};
