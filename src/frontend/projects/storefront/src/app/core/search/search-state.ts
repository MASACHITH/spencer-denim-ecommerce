import { Params } from '@angular/router';

import {
  EMPTY_LISTING_STATE,
  ListingState,
  RawParams,
  hasActiveFilters,
  readListingState,
  sameFilters,
  toQueryParams,
  toRestoreRequests,
} from '../catalog/listing-state';
import { normaliseTerm } from './search-term';

/**
 * The search results page's whole state, and the mapping between it and the URL (SRCH-02).
 *
 * The same shape and the same reasoning as `listing-state.ts`: everything lives in the URL, so a
 * search can be shared, bookmarked, rendered on the server and walked back through with Back.
 * Nothing here touches the DOM or the network.
 *
 * **The one structural difference from a listing is where the department and the category live.**
 * On a listing they are path segments and therefore the page itself, which is why `toQueryParams`
 * leaves them out and `clearFilters` keeps them. A search has no department: `/search?q=linen` is
 * the page, and "Women" is a filter on it like any other, so it is written into the query string,
 * it gets a chip, and "Clear filters" takes it off.
 */
export interface SearchState {
  /** Already normalised (see {@link normaliseTerm}); empty when the URL names no usable term. */
  readonly term: string;
  /** The filters, sort and page count, reusing the listing's own mapping. */
  readonly listing: ListingState;
}

export const EMPTY_SEARCH_STATE: SearchState = {
  term: '',
  listing: EMPTY_LISTING_STATE,
};

/** Reads `/search?...` into state, keeping whatever is usable and dropping whatever is not. */
export function readSearchState(query: RawParams): SearchState {
  return {
    term: normaliseTerm(first(query['q'])),
    // The department and the category are query parameters here, not route segments, so they are
    // handed to the listing reader from the same place as every other filter. It still vets them
    // against the slug pattern and the column width.
    listing: readListingState(first(query['department']), first(query['category']), query),
  };
}

/**
 * The query string for a state. A `null` removes the parameter, which is how the router merges it
 * away, so a search with no filters stays a clean `/search?q=linen`.
 */
export function toSearchQueryParams(state: SearchState): Params {
  const params = toQueryParams(state.listing);

  params['q'] = state.term === '' ? null : state.term;
  // `toQueryParams` never writes these two, because on a listing they are the address. Here they
  // are filters, so they are the search page's to write.
  params['department'] = state.listing.department;
  params['category'] = state.listing.category;

  return params;
}

/**
 * Everything one search asks `GET /api/catalog/search` for, as one page of
 * `loadedPages * PAGE_SIZE` rows.
 *
 * It is `toRestoreRequests` with the term added, deliberately rather than a second
 * implementation: the paging arithmetic it does is the one that was got wrong before — a restore
 * that shrank its last request moved that request's window instead of trimming it, and a
 * bookmarked `?pages=5` rendered 120 cards of which 96 were distinct. A search page with "Load
 * More" has exactly the same ceiling, the same offset rule and the same Id tiebreaker behind it,
 * so it has the same code.
 */
export function toSearchRequests(state: SearchState): Params[] {
  return toRestoreRequests(state.listing).map((params) => ({ ...params, q: state.term }));
}

/** True when two states would ask the API for the same rows in the same order. */
export function sameSearch(left: SearchState, right: SearchState): boolean {
  return left.term === right.term && sameFilters(left.listing, right.listing);
}

/** True when anything is narrowing the results, the department and category included. */
export function hasActiveSearchFilters(state: SearchState): boolean {
  return (
    hasActiveFilters(state.listing) ||
    state.listing.department !== null ||
    state.listing.category !== null
  );
}

/**
 * Every filter off, the department and the category with them, keeping the term and the sort.
 *
 * `clearFilters` in `listing-state.ts` deliberately keeps the department and the category because
 * on a listing they are the page. Here they are not, so this is a different operation rather than
 * a flag on that one.
 */
export function clearSearchFilters(state: SearchState): SearchState {
  return {
    term: state.term,
    listing: { ...EMPTY_LISTING_STATE, sort: state.listing.sort },
  };
}

/** Applies a change to the filters and returns to the first page, as every filter change must. */
export function withSearchFilters(state: SearchState, listing: ListingState): SearchState {
  return { term: state.term, listing: { ...listing, loadedPages: 1 } };
}

/**
 * A stable identity for everything one search asks the API for, so the server can hand its page to
 * the browser instead of the browser fetching it again.
 */
export function searchRequestKey(state: SearchState): string {
  return JSON.stringify(toSearchRequests(state));
}

function first(raw: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(raw) ? raw[0] : (raw as string | undefined);
}
