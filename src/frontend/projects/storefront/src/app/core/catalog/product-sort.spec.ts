import { DEFAULT_SORT, SORT_OPTIONS, readSort, sortLabel } from './product-sort';

describe('SORT_OPTIONS', () => {
  it('offers the seven sorts of PLP-04, in order', () => {
    expect(SORT_OPTIONS.map((option) => option.label)).toEqual([
      'Recommended',
      'Newest',
      'Best Selling',
      'Price: low to high',
      'Price: high to low',
      'Highest Rated',
      'Biggest Discount',
    ]);
  });

  it('uses the values the API accepts on ?sort=', () => {
    // ProductSortOptions in SpencerDenim.Application/Catalog/Products: anything else is a 400.
    expect(SORT_OPTIONS.map((option) => option.value)).toEqual([
      'recommended',
      'newest',
      'best-selling',
      'price-asc',
      'price-desc',
      'rating',
      'discount',
    ]);
  });

  it('starts at the sort the API uses when the URL names none', () => {
    expect(DEFAULT_SORT).toBe('recommended');
    expect(SORT_OPTIONS[0].value).toBe(DEFAULT_SORT);
  });
});

describe('readSort', () => {
  it('accepts every offered value', () => {
    for (const option of SORT_OPTIONS) {
      expect(readSort(option.value)).toBe(option.value);
    }
  });

  it('is forgiving about spacing and case, as the API is', () => {
    expect(readSort('  PRICE-ASC ')).toBe('price-asc');
  });

  it('falls back rather than send the API something it would reject', () => {
    expect(readSort('cheapest')).toBe(DEFAULT_SORT);
    expect(readSort('')).toBe(DEFAULT_SORT);
    expect(readSort(null)).toBe(DEFAULT_SORT);
    expect(readSort(undefined)).toBe(DEFAULT_SORT);
  });
});

describe('sortLabel', () => {
  it('names a sort for the control that shows it', () => {
    expect(sortLabel('discount')).toBe('Biggest Discount');
  });
});
