import {
  EMPTY_LISTING_STATE,
  clampLoadedPages,
  LIST_FILTER_KEYS,
  ListingState,
  MAX_FILTER_VALUES,
  MAX_LOADED_PAGES,
  MAX_PAGE_SIZE,
  PAGE_SIZE,
  RawParams,
  clearFilters,
  hasActiveFilters,
  matchedPriceBand,
  readListingState,
  sameFilters,
  toQueryParams,
  toRequestParams,
  toRestoreRequests,
  requestKey,
  toggleListValue,
  withFilter,
} from './listing-state';

function read(query: RawParams, department = 'women', category: string | null = null): ListingState {
  return readListingState(department, category, query);
}

function state(overrides: Partial<ListingState> = {}): ListingState {
  return { ...EMPTY_LISTING_STATE, department: 'women', ...overrides };
}

describe('readListingState', () => {
  it('reads a plain department listing', () => {
    expect(read({})).toEqual(state());
  });

  it('takes the department and category from the route, not the query string', () => {
    const result = readListingState('women', 'jeans', { department: 'men', category: 'shirts' });

    expect(result.department).toBe('women');
    expect(result.category).toBe('jeans');
  });

  it('reads every repeatable filter, single or repeated', () => {
    const result = read({
      ageGroup: 'kids',
      size: ['M', 'L'],
      colour: ['navy', 'black'],
      material: 'denim',
      fit: ['slim'],
      occasion: ['casual', 'party'],
    });

    expect(result.ageGroups).toEqual(['kids']);
    expect(result.sizes).toEqual(['M', 'L']);
    expect(result.colours).toEqual(['navy', 'black']);
    expect(result.materials).toEqual(['denim']);
    expect(result.fits).toEqual(['slim']);
    expect(result.occasions).toEqual(['casual', 'party']);
  });

  it('normalises a slug the way the API does, and drops one it would reject', () => {
    expect(read({ colour: ['  NAVY  ', 'not a slug', 'black--blue', ''] }).colours).toEqual([
      'navy',
    ]);
  });

  it('drops a slug longer than the column that stores it', () => {
    expect(read({ material: 'a'.repeat(101) }).materials).toEqual([]);
    expect(read({ material: 'a'.repeat(100) }).materials).toHaveLength(1);
  });

  it('keeps size labels as typed but treats them case-insensitively', () => {
    expect(read({ size: ['M', 'm', '  XL  '] }).sizes).toEqual(['M', 'XL']);
  });

  it('drops a size label longer than the column that stores it', () => {
    expect(read({ size: 'x'.repeat(21) }).sizes).toEqual([]);
  });

  it('de-duplicates and caps a repeatable filter at the API limit', () => {
    const many = Array.from({ length: MAX_FILTER_VALUES + 10 }, (_, index) => `colour-${index}`);

    expect(read({ colour: [...many, ...many] }).colours).toHaveLength(MAX_FILTER_VALUES);
  });

  it('reads prices and drops ones the API would refuse', () => {
    expect(read({ minPrice: '1500', maxPrice: '5000.50' })).toMatchObject({
      minPrice: 1500,
      maxPrice: 5000.5,
    });
    expect(read({ minPrice: 'abc' }).minPrice).toBeNull();
    expect(read({ minPrice: '-1' }).minPrice).toBeNull();
    expect(read({ maxPrice: '10000001' }).maxPrice).toBeNull();
    expect(read({ minPrice: '1,500' }).minPrice).toBeNull();
  });

  it('drops both bounds of the one range the API answers with a 400, not just the upper one', () => {
    // Keeping the lower bound would leave a chip reading "Over Rs. 5,000" on a listing whose URL
    // never asked for that, which is a filter the shopper cannot account for.
    const result = read({ minPrice: '5000', maxPrice: '1000' });

    expect(result.minPrice).toBeNull();
    expect(result.maxPrice).toBeNull();
  });

  it('reads a rating only inside 0 to 5', () => {
    expect(read({ minRating: '4' }).minRating).toBe(4);
    expect(read({ minRating: '6' }).minRating).toBeNull();
    expect(read({ minRating: 'four' }).minRating).toBeNull();
  });

  it('accepts the four flag spellings the API accepts, and nothing else', () => {
    expect(read({ inStock: 'true' }).inStock).toBe(true);
    expect(read({ inStock: '1' }).inStock).toBe(true);
    expect(read({ inStock: 'false' }).inStock).toBe(false);
    expect(read({ inStock: '0' }).inStock).toBe(false);
    expect(read({ inStock: 'yes' }).inStock).toBe(false);
  });

  it('reads a discount as also asking for the sale, as the API does', () => {
    const result = read({ minDiscountPercent: '25' });

    expect(result.minDiscountPercent).toBe(25);
    expect(result.onSale).toBe(true);
  });

  it('drops a discount outside 0 to 100', () => {
    expect(read({ minDiscountPercent: '250' }).minDiscountPercent).toBeNull();
  });

  it('falls back to the default sort when the URL names one the API does not know', () => {
    expect(read({ sort: 'price-asc' }).sort).toBe('price-asc');
    expect(read({ sort: 'cheapest' }).sort).toBe('recommended');
    expect(read({ sort: '' }).sort).toBe('recommended');
  });

  it('reads the loaded page count', () => {
    expect(read({ pages: '3' }).loadedPages).toBe(3);
    expect(read({ pages: 'lots' }).loadedPages).toBe(1);
    expect(read({ pages: '2.7' }).loadedPages).toBe(2);
  });

  it('clamps a page count past the ceiling instead of collapsing to the first page', () => {
    // Dropping an out-of-range value back to 1 meant one click past the ceiling took a listing of
    // 480 products down to 24 — the shopper lost everything for asking for more.
    expect(read({ pages: String(MAX_LOADED_PAGES + 1) }).loadedPages).toBe(MAX_LOADED_PAGES);
    expect(read({ pages: '9999' }).loadedPages).toBe(MAX_LOADED_PAGES);
    expect(read({ pages: '0' }).loadedPages).toBe(1);
    expect(read({ pages: '-4' }).loadedPages).toBe(1);
  });

  it('survives a query string that is nothing but junk', () => {
    const result = read({
      colour: ['!!', '  '],
      size: [''],
      minPrice: 'NaN',
      maxPrice: 'Infinity',
      pages: '0',
      minRating: '-3',
      minDiscountPercent: 'half',
      inStock: 'maybe',
      sort: '../../etc/passwd',
    });

    expect(result).toEqual(state());
  });

  it('refuses a department segment that is not a slug', () => {
    expect(readListingState('Women%20', null, {}).department).toBeNull();
  });
});

describe('toQueryParams', () => {
  it('writes nothing for a plain listing', () => {
    const params = toQueryParams(state());

    expect(Object.values(params).every((value) => value === null)).toBe(true);
  });

  it('round-trips every filter it writes', () => {
    const original = state({
      ageGroups: ['kids'],
      sizes: ['M'],
      colours: ['navy', 'black'],
      materials: ['denim'],
      fits: ['slim'],
      occasions: ['casual'],
      minPrice: 2500,
      maxPrice: 5000,
      minRating: 4,
      inStock: true,
      onSale: true,
      minDiscountPercent: 25,
      newArrivals: true,
      sort: 'price-desc',
      loadedPages: 3,
    });

    const restored = readListingState('women', null, toQueryParams(original) as RawParams);

    expect(restored).toEqual(original);
  });

  it('leaves the department and the category out: they are the route', () => {
    const params = toQueryParams(state({ category: 'jeans' }));

    expect(params['department']).toBeUndefined();
    expect(params['category']).toBeUndefined();
  });

  it('writes only the discount when one is set, because it already implies the sale', () => {
    const params = toQueryParams(state({ onSale: true, minDiscountPercent: 25 }));

    expect(params['minDiscountPercent']).toBe('25');
    expect(params['onSale']).toBeNull();
  });

  it('writes the bare sale flag when no minimum discount is asked for', () => {
    expect(toQueryParams(state({ onSale: true }))['onSale']).toBe('true');
  });

  it('leaves the default sort and the first page out of the URL', () => {
    const params = toQueryParams(state({ sort: 'recommended', loadedPages: 1 }));

    expect(params['sort']).toBeNull();
    expect(params['pages']).toBeNull();
  });
});

describe('toRequestParams', () => {
  it('sends the department and category the API binds by name', () => {
    const params = toRequestParams(state({ category: 'jeans' }), 1, PAGE_SIZE);

    expect(params['department']).toBe('women');
    expect(params['category']).toBe('jeans');
  });

  it('always names the sort, the page and the page size', () => {
    const params = toRequestParams(state({ sort: 'newest' }), 2, 48);

    expect(params).toMatchObject({ sort: 'newest', page: '2', pageSize: '48' });
  });

  it('repeats a multi-value filter rather than joining it', () => {
    expect(toRequestParams(state({ colours: ['navy', 'black'] }), 1, PAGE_SIZE)['colour']).toEqual([
      'navy',
      'black',
    ]);
  });

  it('omits a filter that is not set, so the API adds no predicate for it', () => {
    const params = toRequestParams(state(), 1, PAGE_SIZE);

    expect(params['minPrice']).toBeUndefined();
    expect(params['inStock']).toBeUndefined();
    expect(params['colour']).toBeUndefined();
  });
});

describe('toRestoreRequests', () => {
  it('asks for one page on a fresh listing', () => {
    const requests = toRestoreRequests(state());

    expect(requests).toHaveLength(1);
    expect(requests[0]).toMatchObject({ page: '1', pageSize: String(PAGE_SIZE) });
  });

  it('restores several "Load More" pages in one request while the cap allows', () => {
    const requests = toRestoreRequests(state({ loadedPages: 4 }));

    expect(requests).toHaveLength(1);
    expect(requests[0]['pageSize']).toBe(String(MAX_PAGE_SIZE));
  });

  it('never issues more requests than the page-size cap forces, whatever the URL claims', () => {
    const requests = toRestoreRequests(state({ loadedPages: MAX_LOADED_PAGES }));

    expect(requests).toHaveLength(Math.ceil((MAX_LOADED_PAGES * PAGE_SIZE) / MAX_PAGE_SIZE));
    expect(requests.at(-1)?.['page']).toBe(String(requests.length));
  });

  /**
   * This replaces a test that asserted the page sizes summed to exactly the rows wanted. That is
   * not a property of a correct restore — it is what *forced* the defect, because the only way to
   * hit the sum is to shrink the last request, and the API's offset is
   * `(Page - 1) * PageSize` against that request's own page size (`ProductListQuery.Skip`). A
   * smaller last page therefore moved its window backwards instead of trimming it: at
   * `?pages=5` the second request asked for rows 25–48 rather than 97–120, and the listing
   * rendered 120 cards of which 96 were distinct.
   *
   * What matters is that the windows tile the listing: start at the beginning, no gap, no repeat.
   */
  it('asks for windows that tile the listing, with no gap and no repeat', () => {
    for (const loadedPages of [1, 2, 3, 4, 5, 6, 7, 17, MAX_LOADED_PAGES]) {
      const requests = toRestoreRequests(state({ loadedPages }));
      let nextRow = 0;

      for (const request of requests) {
        const pageSize = Number(request['pageSize']);
        const skip = (Number(request['page']) - 1) * pageSize;

        expect(skip, `gap or overlap at pages=${loadedPages}`).toBe(nextRow);
        nextRow += pageSize;
      }

      expect(nextRow, `pages=${loadedPages} stops short`).toBeGreaterThanOrEqual(
        loadedPages * PAGE_SIZE,
      );
    }
  });

  it('never over-fetches by more than one page', () => {
    for (const loadedPages of [1, 5, 6, 17, MAX_LOADED_PAGES]) {
      const wanted = loadedPages * PAGE_SIZE;
      const fetched = toRestoreRequests(state({ loadedPages })).reduce(
        (sum, request) => sum + Number(request['pageSize']),
        0,
      );

      expect(fetched - wanted, `pages=${loadedPages}`).toBeLessThan(MAX_PAGE_SIZE);
    }
  });
});

describe('filter helpers', () => {
  it('knows when something is filtering the listing', () => {
    expect(hasActiveFilters(state())).toBe(false);
    expect(hasActiveFilters(state({ sort: 'newest', loadedPages: 4 }))).toBe(false);
    expect(hasActiveFilters(state({ colours: ['navy'] }))).toBe(true);
    expect(hasActiveFilters(state({ inStock: true }))).toBe(true);
  });

  it('clears every filter but keeps the page and the sort', () => {
    const cleared = clearFilters(
      state({ category: 'jeans', colours: ['navy'], inStock: true, sort: 'newest', loadedPages: 3 }),
    );

    expect(cleared).toEqual(state({ category: 'jeans', sort: 'newest' }));
  });

  it('toggles one value of a repeatable filter and returns to the first page', () => {
    const added = toggleListValue(state({ loadedPages: 3 }), 'colours', 'navy');
    expect(added.colours).toEqual(['navy']);
    expect(added.loadedPages).toBe(1);

    expect(toggleListValue(added, 'colours', 'navy').colours).toEqual([]);
  });

  it('never lets a toggle push a filter past the API limit', () => {
    const full = state({
      colours: Array.from({ length: MAX_FILTER_VALUES }, (_, index) => `c-${index}`),
    });

    expect(toggleListValue(full, 'colours', 'navy').colours).toHaveLength(MAX_FILTER_VALUES);
  });

  it('returns to the first page whenever a filter changes', () => {
    expect(withFilter(state({ loadedPages: 5 }), { inStock: true }).loadedPages).toBe(1);
  });

  it('matches the price bands the filter panel offers', () => {
    expect(matchedPriceBand(state({ minPrice: null, maxPrice: 2500 }))).toBe('under-2500');
    expect(matchedPriceBand(state({ minPrice: 2500, maxPrice: 5000 }))).toBe('2500-5000');
    expect(matchedPriceBand(state({ minPrice: 1234, maxPrice: 4321 }))).toBeNull();
  });
});

describe('sameFilters', () => {
  it('ignores how many pages are loaded, which is what makes "Load More" append', () => {
    expect(sameFilters(state(), state({ loadedPages: 4 }))).toBe(true);
  });

  it('sees a change in any filter, the sort, the department or the category', () => {
    expect(sameFilters(state(), state({ sort: 'newest' }))).toBe(false);
    expect(sameFilters(state(), state({ category: 'jeans' }))).toBe(false);
    expect(sameFilters(state(), state({ department: 'men' }))).toBe(false);
    expect(sameFilters(state(), state({ inStock: true }))).toBe(false);

    for (const key of LIST_FILTER_KEYS) {
      expect(sameFilters(state(), state({ [key]: ['x'] }))).toBe(false);
    }
  });

  it('treats a different order of the same values as a different query', () => {
    expect(
      sameFilters(state({ colours: ['navy', 'black'] }), state({ colours: ['black', 'navy'] })),
    ).toBe(false);
  });
});

describe('requestKey', () => {
  it('is the same for two states that ask the API for the same rows', () => {
    expect(requestKey(state({ colours: ['navy'] }))).toBe(requestKey(state({ colours: ['navy'] })));
  });

  it('changes with the filters, the sort and how many pages are wanted', () => {
    const base = state();

    expect(requestKey(base)).not.toBe(requestKey(state({ colours: ['navy'] })));
    expect(requestKey(base)).not.toBe(requestKey(state({ sort: 'newest' })));
    expect(requestKey(base)).not.toBe(requestKey(state({ loadedPages: 2 })));
    expect(requestKey(base)).not.toBe(requestKey(state({ category: 'jeans' })));
  });
});

describe('clampLoadedPages', () => {
  it('keeps a page count inside its bounds, whoever asked', () => {
    expect(clampLoadedPages(1)).toBe(1);
    expect(clampLoadedPages(0)).toBe(1);
    expect(clampLoadedPages(-3)).toBe(1);
    expect(clampLoadedPages(MAX_LOADED_PAGES)).toBe(MAX_LOADED_PAGES);
    expect(clampLoadedPages(MAX_LOADED_PAGES + 1)).toBe(MAX_LOADED_PAGES);
    expect(clampLoadedPages(Number.NaN)).toBe(1);
  });
});
