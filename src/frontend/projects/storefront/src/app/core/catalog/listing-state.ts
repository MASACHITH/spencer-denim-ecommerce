import { Params } from '@angular/router';

import { PRICE_BANDS } from './filter-options';
import { DEFAULT_SORT, SortValue, readSort } from './product-sort';

/**
 * The listing's whole state, and the mapping between it and the URL (PLP-02..07).
 *
 * Every filter, the sort and how much has been loaded live in the URL, so a filtered listing can
 * be shared, bookmarked, rendered on the server and walked back through with the browser's Back
 * button. Nothing here touches the DOM or the network: it is pure, which is what lets the same
 * mapping run during SSR and be tested without a browser.
 *
 * The bounds below are the API's own (`ProductListQuery` in
 * `SpencerDenim.Application/Catalog/Products`). Reading them here is not belt-and-braces: a URL
 * arrives from outside, and a value the API would reject with a 400 is a broken page for a
 * shopper who only followed a stale link. Anything unreadable is dropped and the rest of the
 * listing still renders.
 */

/** `ProductListQuery.MaxFilterValues`: most values one repeatable filter may carry. */
export const MAX_FILTER_VALUES = 25;

/** `ProductListQuery.MaxSizeLabelLength`: `catalog.Sizes.Label` is `nvarchar(20)`. */
const MAX_SIZE_LABEL_LENGTH = 20;

/** `ProductListQuery.MaxSlugLength`: `catalog.Categories.Slug` is `varchar(100)`. */
const MAX_SLUG_LENGTH = 100;

/** `ProductListQuery.MaxPriceValue`, in LKR. Above it the number is a typo, not a filter. */
const MAX_PRICE = 10_000_000;

/** `CatalogSlug`: lower-case letters and digits, separated by single hyphens. */
const SLUG_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/** How many products one "Load More" adds, and the page size every request uses. */
export const PAGE_SIZE = 24;

/** `CatalogOptions.MaxPageSize`: the largest page the API will serve in one request. */
export const MAX_PAGE_SIZE = 96;

/**
 * How many pages of {@link PAGE_SIZE} a URL may claim to have loaded. A shared link restores all
 * of them, and without a ceiling `?pages=999` would turn one page view into forty requests.
 */
export const MAX_LOADED_PAGES = 20;

/** The listing as the URL describes it. */
export interface ListingState {
  /** From the route path, not the query string. */
  readonly department: string | null;
  /** From the route path, not the query string. */
  readonly category: string | null;
  readonly ageGroups: readonly string[];
  readonly sizes: readonly string[];
  readonly colours: readonly string[];
  readonly materials: readonly string[];
  readonly fits: readonly string[];
  readonly occasions: readonly string[];
  readonly minPrice: number | null;
  readonly maxPrice: number | null;
  readonly minRating: number | null;
  readonly inStock: boolean;
  readonly onSale: boolean;
  readonly minDiscountPercent: number | null;
  readonly newArrivals: boolean;
  readonly sort: SortValue;
  /** Pages of {@link PAGE_SIZE} the shopper has asked for, 1 on a fresh listing (PLP-07). */
  readonly loadedPages: number;
}

/** The repeatable slug filters, so they can be read, written and cleared as one. */
export const LIST_FILTER_KEYS = [
  'ageGroups',
  'sizes',
  'colours',
  'materials',
  'fits',
  'occasions',
] as const;

export type ListFilterKey = (typeof LIST_FILTER_KEYS)[number];

/** The query-string name of each repeatable filter (singular, as the API binds it). */
export const LIST_FILTER_PARAM: Readonly<Record<ListFilterKey, string>> = {
  ageGroups: 'ageGroup',
  sizes: 'size',
  colours: 'colour',
  materials: 'material',
  fits: 'fit',
  occasions: 'occasion',
};

export const EMPTY_LISTING_STATE: ListingState = {
  department: null,
  category: null,
  ageGroups: [],
  sizes: [],
  colours: [],
  materials: [],
  fits: [],
  occasions: [],
  minPrice: null,
  maxPrice: null,
  minRating: null,
  inStock: false,
  onSale: false,
  minDiscountPercent: null,
  newArrivals: false,
  sort: DEFAULT_SORT,
  loadedPages: 1,
};

/** The raw query string, however the router hands it over. */
export type RawParams = Readonly<Record<string, string | readonly string[] | undefined>>;

/**
 * Reads the URL into state, keeping whatever is usable and dropping whatever is not.
 *
 * @param department the `:department` route segment
 * @param category the `:category` route segment, when the route has one
 */
export function readListingState(
  department: string | null | undefined,
  category: string | null | undefined,
  query: RawParams,
): ListingState {
  const minPrice = readNumber(query['minPrice'], 0, MAX_PRICE);
  const maxPrice = readNumber(query['maxPrice'], 0, MAX_PRICE);
  const minDiscountPercent = readNumber(query['minDiscountPercent'], 0, 100);

  return {
    department: readSlug(department),
    category: readSlug(category),
    ageGroups: readSlugList(query[LIST_FILTER_PARAM.ageGroups]),
    sizes: readLabelList(query[LIST_FILTER_PARAM.sizes]),
    colours: readSlugList(query[LIST_FILTER_PARAM.colours]),
    materials: readSlugList(query[LIST_FILTER_PARAM.materials]),
    fits: readSlugList(query[LIST_FILTER_PARAM.fits]),
    occasions: readSlugList(query[LIST_FILTER_PARAM.occasions]),
    // A reversed range is the one combination the API answers with a 400 rather than an empty
    // page, and a shopper can produce it by hand. Dropping the upper bound keeps the listing.
    minPrice,
    maxPrice: minPrice !== null && maxPrice !== null && minPrice > maxPrice ? null : maxPrice,
    minRating: readNumber(query['minRating'], 0, 5),
    inStock: readBoolean(query['inStock']),
    // Asking for a discount is asking for the sale, which is how the API reads it too.
    onSale: readBoolean(query['onSale']) || (minDiscountPercent ?? 0) > 0,
    minDiscountPercent,
    newArrivals: readBoolean(query['newArrivals']),
    sort: readSort(first(query['sort'])),
    loadedPages: readLoadedPages(query['pages']),
  };
}

/**
 * The query string for a state: only what differs from a plain listing, so a clean URL stays
 * clean. A `null` removes the parameter, which is how the router merges it away.
 */
export function toQueryParams(state: ListingState): Params {
  const params: Params = {};

  for (const key of LIST_FILTER_KEYS) {
    const values = state[key];
    params[LIST_FILTER_PARAM[key]] = values.length > 0 ? [...values] : null;
  }

  params['minPrice'] = numberParam(state.minPrice);
  params['maxPrice'] = numberParam(state.maxPrice);
  params['minRating'] = numberParam(state.minRating);
  params['inStock'] = state.inStock ? 'true' : null;
  // The discount already implies the sale on both sides, so only one of the two is written.
  params['onSale'] = state.onSale && !hasDiscountFilter(state) ? 'true' : null;
  params['minDiscountPercent'] = hasDiscountFilter(state)
    ? String(state.minDiscountPercent)
    : null;
  params['newArrivals'] = state.newArrivals ? 'true' : null;
  params['sort'] = state.sort === DEFAULT_SORT ? null : state.sort;
  params['pages'] = state.loadedPages > 1 ? String(state.loadedPages) : null;

  return params;
}

/**
 * The parameters one request to `GET /api/catalog/products` needs. The department and category
 * travel as query parameters here even though the shopper sees them as path segments.
 */
export function toRequestParams(state: ListingState, page: number, pageSize: number): Params {
  const params: Params = {};

  if (state.department) {
    params['department'] = state.department;
  }
  if (state.category) {
    params['category'] = state.category;
  }
  for (const key of LIST_FILTER_KEYS) {
    if (state[key].length > 0) {
      params[LIST_FILTER_PARAM[key]] = [...state[key]];
    }
  }
  if (state.minPrice !== null) {
    params['minPrice'] = String(state.minPrice);
  }
  if (state.maxPrice !== null) {
    params['maxPrice'] = String(state.maxPrice);
  }
  if (state.minRating !== null) {
    params['minRating'] = String(state.minRating);
  }
  if (state.inStock) {
    params['inStock'] = 'true';
  }
  if (state.onSale) {
    params['onSale'] = 'true';
  }
  if (hasDiscountFilter(state)) {
    params['minDiscountPercent'] = String(state.minDiscountPercent);
  }
  if (state.newArrivals) {
    params['newArrivals'] = 'true';
  }

  params['sort'] = state.sort;
  params['page'] = String(page);
  params['pageSize'] = String(pageSize);

  return params;
}

/**
 * Everything the listing asks the API for, as one page of `loadedPages * PAGE_SIZE` rows.
 *
 * A restored listing is fetched in as few requests as the API's page-size cap allows, rather than
 * one request per "Load More" the original shopper clicked: five requests at most, whatever the
 * URL claims. The rows are identical either way because the API's ordering is total (doc 08 §7.1
 * adds an Id tiebreaker).
 */
export function toRestoreRequests(state: ListingState): Params[] {
  const wanted = state.loadedPages * PAGE_SIZE;
  const requests: Params[] = [];

  for (let page = 1; (page - 1) * MAX_PAGE_SIZE < wanted; page++) {
    const remaining = wanted - (page - 1) * MAX_PAGE_SIZE;
    requests.push(toRequestParams(state, page, Math.min(remaining, MAX_PAGE_SIZE)));
  }

  return requests;
}

/**
 * True when two states would ask the API for the same products in the same order — everything
 * except how many pages of them are wanted. It is what tells a "Load More" (append to what is on
 * screen) apart from a filter change (start again), so the shopper's scroll position survives.
 */
export function sameFilters(left: ListingState, right: ListingState): boolean {
  return (
    left.department === right.department &&
    left.category === right.category &&
    left.minPrice === right.minPrice &&
    left.maxPrice === right.maxPrice &&
    left.minRating === right.minRating &&
    left.inStock === right.inStock &&
    left.onSale === right.onSale &&
    left.minDiscountPercent === right.minDiscountPercent &&
    left.newArrivals === right.newArrivals &&
    left.sort === right.sort &&
    LIST_FILTER_KEYS.every((key) => sameValues(left[key], right[key]))
  );
}

function sameValues(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

/** True when any PLP-02 filter is on. The sort and the page count are not filters. */
export function hasActiveFilters(state: ListingState): boolean {
  return (
    LIST_FILTER_KEYS.some((key) => state[key].length > 0) ||
    state.minPrice !== null ||
    state.maxPrice !== null ||
    state.minRating !== null ||
    state.inStock ||
    state.onSale ||
    state.newArrivals
  );
}

/**
 * Clears every filter, and with them the extra pages: the shopper is looking at a different set
 * of products, so starting them at the bottom of a list they have never seen helps nobody.
 * The department and category stay, because they are the page, not a filter on it.
 */
export function clearFilters(state: ListingState): ListingState {
  return {
    ...EMPTY_LISTING_STATE,
    department: state.department,
    category: state.category,
    sort: state.sort,
  };
}

/** Adds or removes one value of a repeatable filter, and returns to the first page. */
export function toggleListValue(
  state: ListingState,
  key: ListFilterKey,
  value: string,
): ListingState {
  const current = state[key];
  const next = current.includes(value)
    ? current.filter((candidate) => candidate !== value)
    : [...current, value].slice(0, MAX_FILTER_VALUES);

  return { ...state, [key]: next, loadedPages: 1 };
}

/** Applies a partial change and returns to the first page, which every filter change must do. */
export function withFilter(state: ListingState, change: Partial<ListingState>): ListingState {
  return { ...state, ...change, loadedPages: 1 };
}

/** True when the discount filter is set to a real minimum rather than the bare "on sale" flag. */
export function hasDiscountFilter(state: ListingState): boolean {
  return state.minDiscountPercent !== null && state.minDiscountPercent > 0;
}

/** The band whose bounds the state matches exactly, for the filter control and the chip. */
export function matchedPriceBand(state: ListingState): string | null {
  return (
    PRICE_BANDS.find((band) => band.min === state.minPrice && band.max === state.maxPrice)?.id ??
    null
  );
}

function readSlug(raw: string | null | undefined): string | null {
  const value = raw?.trim().toLowerCase();
  if (!value || value.length > MAX_SLUG_LENGTH || !SLUG_PATTERN.test(value)) {
    return null;
  }

  return value;
}

function readSlugList(raw: string | readonly string[] | undefined): readonly string[] {
  return dedupe(all(raw).map(readSlug));
}

function readLabelList(raw: string | readonly string[] | undefined): readonly string[] {
  return dedupe(
    all(raw).map((candidate) => {
      const label = candidate.trim();
      return label.length > 0 && label.length <= MAX_SIZE_LABEL_LENGTH ? label : null;
    }),
    // The API compares size labels case-insensitively, so "m" and "M" are one filter.
    (value) => value.toLowerCase(),
  );
}

function dedupe(
  values: readonly (string | null)[],
  key: (value: string) => string = (value) => value,
): readonly string[] {
  const seen = new Set<string>();
  const kept: string[] = [];

  for (const value of values) {
    if (value === null || seen.has(key(value))) {
      continue;
    }

    seen.add(key(value));
    kept.push(value);

    if (kept.length === MAX_FILTER_VALUES) {
      break;
    }
  }

  return kept;
}

/**
 * Reads a decimal parameter inside its bounds. A blank, unreadable or out-of-range value is not
 * repaired into a different filter — it is dropped, and the listing renders without it.
 */
function readNumber(
  raw: string | readonly string[] | undefined,
  minimum: number,
  maximum: number,
): number | null {
  const value = first(raw)?.trim();
  if (!value || !/^-?\d+(\.\d+)?$/.test(value)) {
    return null;
  }

  const parsed = Number(value);
  return parsed >= minimum && parsed <= maximum ? parsed : null;
}

/** The four spellings the API accepts. Anything else is not a filter the shopper asked for. */
function readBoolean(raw: string | readonly string[] | undefined): boolean {
  const value = first(raw)?.trim().toLowerCase();
  return value === 'true' || value === '1';
}

function readLoadedPages(raw: string | readonly string[] | undefined): number {
  const value = readNumber(raw, 1, MAX_LOADED_PAGES);
  return value === null ? 1 : Math.floor(value);
}

function numberParam(value: number | null): string | null {
  return value === null ? null : String(value);
}

function first(raw: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(raw) ? raw[0] : (raw as string | undefined);
}

function all(raw: string | readonly string[] | undefined): readonly string[] {
  if (raw === undefined) {
    return [];
  }

  return Array.isArray(raw) ? raw : [raw as string];
}
