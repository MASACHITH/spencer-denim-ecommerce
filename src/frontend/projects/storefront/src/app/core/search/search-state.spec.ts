import {
  EMPTY_LISTING_STATE,
  MAX_LOADED_PAGES,
  MAX_PAGE_SIZE,
  PAGE_SIZE,
} from '../catalog/listing-state';
import { activeChips, removeChip } from '../catalog/listing-chips';
import {
  SearchState,
  clearSearchFilters,
  hasActiveSearchFilters,
  readSearchState,
  sameSearch,
  searchRequestKey,
  toSearchQueryParams,
  toSearchRequests,
  withSearchFilters,
} from './search-state';

function state(overrides: Partial<SearchState['listing']> = {}, term = 'linen shirt'): SearchState {
  return { term, listing: { ...EMPTY_LISTING_STATE, ...overrides } };
}

describe('readSearchState (SRCH-02)', () => {
  it('reads the term and the filters out of the query string', () => {
    const read = readSearchState({
      q: ' Linen  Shirt ',
      department: 'women',
      category: 'jeans',
      colour: ['blue', 'black'],
      sort: 'price-asc',
      pages: '3',
    });

    expect(read.term).toBe('Linen Shirt');
    expect(read.listing.department).toBe('women');
    expect(read.listing.category).toBe('jeans');
    expect(read.listing.colours).toEqual(['blue', 'black']);
    expect(read.listing.sort).toBe('price-asc');
    expect(read.listing.loadedPages).toBe(3);
  });

  it('drops a department that is not a slug rather than sending it to the API', () => {
    expect(readSearchState({ q: 'linen', department: 'Not A Slug!' }).listing.department).toBeNull();
  });

  it('reads an absent term as an empty one', () => {
    expect(readSearchState({}).term).toBe('');
    expect(readSearchState({ q: '' }).term).toBe('');
  });

  it('normalises a term that could not be encoded', () => {
    expect(readSearchState({ q: 'linen\ud83d' }).term).toBe('linen');
  });

  it('takes the first value when a parameter is repeated', () => {
    expect(readSearchState({ q: ['linen', 'denim'] }).term).toBe('linen');
  });

  it('clamps a page count above the ceiling instead of collapsing to one', () => {
    expect(readSearchState({ q: 'linen', pages: '999' }).listing.loadedPages).toBe(MAX_LOADED_PAGES);
  });
});

describe('toSearchQueryParams', () => {
  it('writes only the term for a search with no filters', () => {
    const params = toSearchQueryParams(state());

    expect(params['q']).toBe('linen shirt');
    expect(params['department']).toBeNull();
    expect(params['category']).toBeNull();
    expect(params['sort']).toBeNull();
    expect(params['pages']).toBeNull();
  });

  it('writes the department and the category, which a listing never does', () => {
    const params = toSearchQueryParams(state({ department: 'women', category: 'jeans' }));

    expect(params['department']).toBe('women');
    expect(params['category']).toBe('jeans');
  });

  it('removes the term when there is none, so the parameter is merged away', () => {
    expect(toSearchQueryParams(state({}, ''))['q']).toBeNull();
  });
});

describe('toSearchRequests', () => {
  it('asks for one page of the usual size on a fresh search', () => {
    const requests = toSearchRequests(state());

    expect(requests).toHaveLength(1);
    expect(requests[0]['q']).toBe('linen shirt');
    expect(requests[0]['page']).toBe('1');
    expect(requests[0]['pageSize']).toBe(String(PAGE_SIZE));
  });

  it('carries the filters to the API alongside the term', () => {
    const requests = toSearchRequests(state({ department: 'women', colours: ['blue'] }));

    expect(requests[0]['department']).toBe('women');
    expect(requests[0]['colour']).toEqual(['blue']);
  });

  /**
   * The duplicate-rendering blocker, in its new home.
   *
   * A restored listing used to shrink its last request to the remainder, which **moved** that
   * request's window instead of trimming it: at `?pages=5` the second request asked for rows 25–48
   * rather than 97–120 and the page drew 120 cards of which 96 were distinct. A bookmarked page-2
   * search is the same arithmetic, so the windows are asserted here rather than taken on trust.
   */
  it('covers a bookmarked deep page with windows that do not overlap', () => {
    const requests = toSearchRequests(state({ loadedPages: 5 }));
    const wanted = 5 * PAGE_SIZE;

    expect(requests).toHaveLength(2);

    const windows = requests.map((request) => {
      const pageSize = Number(request['pageSize']);
      const skip = (Number(request['page']) - 1) * pageSize;
      return { skip, end: skip + pageSize };
    });

    expect(windows[0]).toEqual({ skip: 0, end: MAX_PAGE_SIZE });
    expect(windows[1].skip).toBe(MAX_PAGE_SIZE);
    // The last request over-fetches rather than moving; the store slices the tail off.
    expect(windows[1].end).toBeGreaterThanOrEqual(wanted);
  });

  it('asks every request for the same page size, which is what makes the offsets line up', () => {
    const sizes = toSearchRequests(state({ loadedPages: MAX_LOADED_PAGES })).map(
      (request) => request['pageSize'],
    );

    expect(new Set(sizes).size).toBe(1);
  });

  it('never asks for more than five requests, whatever the URL claims', () => {
    expect(toSearchRequests(state({ loadedPages: MAX_LOADED_PAGES }))).toHaveLength(5);
  });
});

describe('sameSearch', () => {
  it('is false when only the term differs', () => {
    expect(sameSearch(state({}, 'linen'), state({}, 'denim'))).toBe(false);
  });

  it('is true when only the page count differs, which is what makes Load More append', () => {
    expect(sameSearch(state({ loadedPages: 1 }), state({ loadedPages: 2 }))).toBe(true);
  });

  it('is false when a filter differs', () => {
    expect(sameSearch(state({ colours: ['blue'] }), state())).toBe(false);
  });
});

describe('clearSearchFilters', () => {
  it('takes the department and the category off, unlike a listing', () => {
    const cleared = clearSearchFilters(state({ department: 'women', category: 'jeans', colours: ['blue'] }));

    expect(cleared.listing.department).toBeNull();
    expect(cleared.listing.category).toBeNull();
    expect(cleared.listing.colours).toEqual([]);
  });

  it('keeps the term and the sort', () => {
    const cleared = clearSearchFilters(state({ sort: 'price-asc' }));

    expect(cleared.term).toBe('linen shirt');
    expect(cleared.listing.sort).toBe('price-asc');
  });
});

describe('hasActiveSearchFilters', () => {
  it('counts the department, which is not a filter on a listing', () => {
    expect(hasActiveSearchFilters(state({ department: 'women' }))).toBe(true);
  });

  it('is false for a bare search', () => {
    expect(hasActiveSearchFilters(state())).toBe(false);
  });
});

describe('withSearchFilters', () => {
  it('returns to the first page, as every filter change must', () => {
    const next = withSearchFilters(state({ loadedPages: 4 }), {
      ...EMPTY_LISTING_STATE,
      colours: ['blue'],
      loadedPages: 4,
    });

    expect(next.listing.loadedPages).toBe(1);
    expect(next.term).toBe('linen shirt');
  });
});

describe('searchRequestKey', () => {
  it('differs when the term differs, so one search never takes the page of another', () => {
    expect(searchRequestKey(state({}, 'linen'))).not.toBe(searchRequestKey(state({}, 'denim')));
  });

  it('is the same for two states that would ask for exactly the same rows', () => {
    expect(searchRequestKey(state({ colours: ['blue'] }))).toBe(
      searchRequestKey(state({ colours: ['blue'] })),
    );
  });
});

/** The placement chips only the search page asks for. */
describe('department and category chips', () => {
  it('are absent by default, which is the listing', () => {
    const chips = activeChips({ ...EMPTY_LISTING_STATE, department: 'women', category: 'jeans' });

    expect(chips).toEqual([]);
  });

  it('are present when the caller asks, labelled with the names it resolved', () => {
    const chips = activeChips(
      { ...EMPTY_LISTING_STATE, department: 'women', category: 'jeans' },
      { placement: true, departmentName: 'Women', categoryName: 'Jeans' },
    );

    expect(chips.map((chip) => chip.label)).toEqual(['Department: Women', 'Category: Jeans']);
  });

  it('fall back to the slug when the tree could not name them', () => {
    const chips = activeChips(
      { ...EMPTY_LISTING_STATE, department: 'women' },
      { placement: true },
    );

    expect(chips[0].label).toBe('Department: women');
  });

  /**
   * A category slug is unique per department and parent, so "jeans" on its own names two categories
   * and the API answers 400 naming `category`. Leaving one behind would turn "show me everything"
   * into an error page.
   */
  it('removing the department takes the category with it', () => {
    const listing = { ...EMPTY_LISTING_STATE, department: 'women', category: 'jeans' };
    const chips = activeChips(listing, { placement: true });

    const next = removeChip(listing, chips[0]);

    expect(next.department).toBeNull();
    expect(next.category).toBeNull();
  });

  it('removing the category keeps the department', () => {
    const listing = { ...EMPTY_LISTING_STATE, department: 'women', category: 'jeans' };
    const chips = activeChips(listing, { placement: true });

    const next = removeChip(listing, chips[1]);

    expect(next.department).toBe('women');
    expect(next.category).toBeNull();
  });
});
