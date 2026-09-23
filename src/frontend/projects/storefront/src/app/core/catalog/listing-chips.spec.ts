import { activeChips, clearAllChips, removeChip } from './listing-chips';
import { EMPTY_LISTING_STATE, ListingState } from './listing-state';

function state(overrides: Partial<ListingState> = {}): ListingState {
  return { ...EMPTY_LISTING_STATE, department: 'women', ...overrides };
}

function labels(current: ListingState): string[] {
  return activeChips(current).map((chip) => chip.label);
}

describe('activeChips', () => {
  it('shows nothing on an unfiltered listing', () => {
    expect(activeChips(state())).toEqual([]);
  });

  it('never chips the department or the category: they are the page, not a filter', () => {
    expect(activeChips(state({ department: 'women', category: 'jeans' }))).toEqual([]);
  });

  it('names the group a repeatable filter came from', () => {
    expect(
      labels(state({ colours: ['navy'], sizes: ['M'], fits: ['slim'], ageGroups: ['kids'] })),
    ).toEqual(['Age group: Kids (5–10)', 'Size: M', 'Colour: Navy', 'Fit: Slim']);
  });

  it('gives one chip per value, so each can be taken off on its own', () => {
    expect(labels(state({ colours: ['navy', 'black'] }))).toEqual([
      'Colour: Navy',
      'Colour: Black',
    ]);
  });

  it('falls back to the raw value when the catalogue no longer offers that option', () => {
    expect(labels(state({ materials: ['hemp'] }))).toEqual(['Material: hemp']);
  });

  it('reads a price filter as its band when the bounds match one', () => {
    expect(labels(state({ minPrice: 2500, maxPrice: 5000 }))).toEqual(['Rs. 2,500 – Rs. 5,000']);
    expect(labels(state({ maxPrice: 2500 }))).toEqual(['Under Rs. 2,500']);
  });

  it('reads bounds a link invented as bounds, not as a band', () => {
    expect(labels(state({ minPrice: 1200, maxPrice: 3400 }))).toEqual(['Rs. 1,200 – Rs. 3,400']);
    expect(labels(state({ minPrice: 9000 }))).toEqual(['Over Rs. 9,000']);
    expect(labels(state({ maxPrice: 900 }))).toEqual(['Under Rs. 900']);
  });

  it('chips a rating, availability, a discount and new arrivals', () => {
    expect(
      labels(state({ minRating: 4, inStock: true, onSale: true, newArrivals: true })),
    ).toEqual(['4 stars & up', 'In stock', 'On sale', 'New arrivals']);
  });

  it('shows one chip for a discount, not one for the discount and one for the sale', () => {
    expect(labels(state({ onSale: true, minDiscountPercent: 25 }))).toEqual(['25% off or more']);
  });

  it('gives every chip a remove label a screen reader can act on', () => {
    const [chip] = activeChips(state({ colours: ['navy'] }));

    expect(chip.removeLabel).toBe('Remove filter: Colour: Navy');
  });

  it('gives every chip an id that is stable and unique', () => {
    const chips = activeChips(
      state({ colours: ['navy', 'black'], sizes: ['M'], inStock: true, minRating: 4 }),
    );
    const ids = chips.map((chip) => chip.id);

    expect(new Set(ids).size).toBe(ids.length);
    expect(activeChips(state({ colours: ['navy'] }))[0].id).toBe('colours:navy');
  });
});

describe('removeChip', () => {
  it('takes off one value and leaves the rest of the group alone', () => {
    const current = state({ colours: ['navy', 'black'] });
    const [navy] = activeChips(current);

    expect(removeChip(current, navy).colours).toEqual(['black']);
  });

  it('takes off a whole-filter chip', () => {
    const current = state({ minPrice: 2500, maxPrice: 5000, minRating: 4, inStock: true });

    for (const chip of activeChips(current)) {
      const next = removeChip(current, chip);
      expect(activeChips(next).length).toBe(activeChips(current).length - 1);
    }
  });

  it('takes the sale off with the discount, because one implies the other', () => {
    const current = state({ onSale: true, minDiscountPercent: 25 });
    const [discount] = activeChips(current);
    const next = removeChip(current, discount);

    expect(next.onSale).toBe(false);
    expect(next.minDiscountPercent).toBeNull();
  });

  it('returns to the first page, because the shopper is looking at a different set', () => {
    const current = state({ colours: ['navy'], loadedPages: 4 });

    expect(removeChip(current, activeChips(current)[0]).loadedPages).toBe(1);
  });

  it('keeps the page, the sort and the category', () => {
    const current = state({ category: 'jeans', sort: 'newest', colours: ['navy'] });
    const next = removeChip(current, activeChips(current)[0]);

    expect(next.department).toBe('women');
    expect(next.category).toBe('jeans');
    expect(next.sort).toBe('newest');
  });
});

describe('clearAllChips', () => {
  it('leaves no chip standing', () => {
    const current = state({
      category: 'jeans',
      colours: ['navy'],
      sizes: ['M'],
      minPrice: 2500,
      minRating: 4,
      inStock: true,
      onSale: true,
      newArrivals: true,
    });

    expect(activeChips(clearAllChips(current))).toEqual([]);
    expect(clearAllChips(current).category).toBe('jeans');
  });
});
