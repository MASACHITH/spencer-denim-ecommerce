import { ProductCardDto } from '../catalog/catalog.model';
import { CategoryNodeDto, DepartmentDto } from '../navigation/catalog-departments.model';
import {
  CATEGORY_SUGGESTION_LIMIT,
  PRODUCT_SUGGESTION_LIMIT,
  browseSuggestions,
  categorySuggestions,
  flattenSuggestions,
  group,
  nextSuggestionId,
  productSuggestions,
  recentSuggestions,
  suggestionAt,
} from './search-suggestions';

function node(
  id: number,
  name: string,
  slug: string,
  children: CategoryNodeDto[] = [],
  depth = 1,
): CategoryNodeDto {
  return {
    id,
    name,
    slug,
    depth,
    description: null,
    imageUrl: null,
    imageSrcSet: null,
    showInMegaMenu: true,
    megaMenuColumn: null,
    ageGroupSlug: null,
    children,
  };
}

const DEPARTMENTS: readonly DepartmentDto[] = [
  {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      node(11, 'Dresses', 'dresses'),
      node(12, 'Jeans', 'jeans'),
      node(13, 'Linen Shirts', 'linen-shirts'),
    ],
  },
  {
    id: 3,
    name: 'Kids',
    slug: 'kids',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [node(31, 'Girls', 'girls', [node(311, 'Girls Dresses', 'girls-dresses', [], 2)])],
  },
];

function card(id: number, name = `Product ${id}`): ProductCardDto {
  return {
    id,
    productCode: `SD-${id}`,
    name,
    slug: `product-${id}`,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    imageUrl: '/media/public/card.webp',
    imageSrcSet: '/media/public/card.webp 600w',
    hoverImageUrl: null,
    hoverImageSrcSet: null,
    imageAltText: null,
    price: { current: 5990, original: 5990, discountPercent: 0, hasDiscount: false, currency: 'LKR' },
    rating: { average: null, count: 0 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
  };
}

describe('productSuggestions (SRCH-01)', () => {
  it('shows the product, where it sits and its picture', () => {
    const [option] = productSuggestions([card(1, 'Linen Shirt')], 'box');

    expect(option).toMatchObject({
      id: 'box-product-0',
      kind: 'product',
      label: 'Linen Shirt',
      detail: 'Women › Jeans',
      path: '/product/product-1',
      term: null,
    });
    expect(option.imageSrcSet).toBe('/media/public/card.webp 600w');
  });

  it('keeps no more than the limit, even when the API returns its full page', () => {
    const cards = Array.from({ length: 6 }, (_, index) => card(index + 1));

    expect(productSuggestions(cards, 'box')).toHaveLength(PRODUCT_SUGGESTION_LIMIT);
  });

  it('is empty for an empty result, which is the whole no-suggestions case', () => {
    expect(productSuggestions([], 'box')).toEqual([]);
  });
});

describe('categorySuggestions (SRCH-01)', () => {
  it('matches a category name and names its department', () => {
    const options = categorySuggestions(DEPARTMENTS, ['jeans'], 'box');

    expect(options).toHaveLength(1);
    expect(options[0]).toMatchObject({ label: 'Jeans', detail: 'Women', path: '/women/jeans' });
  });

  it('builds the full path for a nested category, as the mega menu links it', () => {
    const options = categorySuggestions(DEPARTMENTS, ['girls', 'dresses'], 'box');

    expect(options[0].path).toBe('/kids/girls/girls-dresses');
  });

  it('requires every token to match, as the API does', () => {
    expect(categorySuggestions(DEPARTMENTS, ['linen', 'trousers'], 'box')).toEqual([]);
  });

  it('puts a name that starts with the first token above one that merely contains it', () => {
    const options = categorySuggestions(DEPARTMENTS, ['dresses'], 'box');

    expect(options.map((option) => option.label)).toEqual(['Dresses', 'Girls Dresses']);
  });

  it('keeps no more than the limit', () => {
    // Every category contains "s".  Two characters is the shortest token the API accepts, so "es"
    // is the realistic version of the same question.
    const options = categorySuggestions(DEPARTMENTS, ['es'], 'box');

    expect(options.length).toBeLessThanOrEqual(CATEGORY_SUGGESTION_LIMIT);
  });

  it('suggests nothing without tokens', () => {
    expect(categorySuggestions(DEPARTMENTS, [], 'box')).toEqual([]);
  });

  it('survives a department whose categories are missing, which the fallback tree can be', () => {
    const broken = [{ ...DEPARTMENTS[0], categories: undefined as unknown as CategoryNodeDto[] }];

    expect(() => categorySuggestions(broken, ['jeans'], 'box')).not.toThrow();
  });
});

describe('recentSuggestions', () => {
  it('searches again rather than navigating', () => {
    const [option] = recentSuggestions(['linen shirt'], 'box');

    expect(option).toMatchObject({ kind: 'recent', label: 'linen shirt', term: 'linen shirt', path: null });
  });
});

/**
 * The honest answer to SRCH-01's "popular searches": nothing records what anyone has searched for,
 * so these are the catalogue's own entrances and they are never labelled as popularity.
 */
describe('browseSuggestions', () => {
  it('offers every department plus the two permanent listings', () => {
    const options = browseSuggestions(DEPARTMENTS, 'box');

    expect(options.map((option) => option.path)).toEqual([
      '/women',
      '/kids',
      '/new-arrivals',
      '/sale',
    ]);
  });

  it('is never described as popular', () => {
    expect(
      browseSuggestions(DEPARTMENTS, 'box').some((option) => /popular/i.test(option.label)),
    ).toBe(false);
  });
});

describe('group', () => {
  it('returns nothing for an empty block, so no heading is drawn over nothing', () => {
    expect(group('products', 'box', 'Products', [])).toEqual([]);
  });

  it('gives the block an id and a heading id, for the listbox group and its label', () => {
    const [block] = group('products', 'box', 'Products', productSuggestions([card(1)], 'box'));

    expect(block.id).toBe('box-products');
    expect(block.headingId).toBe('box-products-heading');
    expect(block.label).toBe('Products');
  });
});

/** The keyboard half of the overlay's state machine. */
describe('nextSuggestionId', () => {
  const groups = [
    ...group('products', 'box', 'Products', productSuggestions([card(1), card(2)], 'box')),
    ...group('categories', 'box', 'Categories', categorySuggestions(DEPARTMENTS, ['jeans'], 'box')),
  ];
  const ids = flattenSuggestions(groups).map((option) => option.id);

  it('walks across groups, because the arrow keys do not know about them', () => {
    expect(ids).toEqual(['box-product-0', 'box-product-1', 'box-category-0']);
  });

  it('moves to the first option from nothing when going down', () => {
    expect(nextSuggestionId(groups, null, 1)).toBe(ids[0]);
  });

  it('moves to the last option from nothing when going up', () => {
    expect(nextSuggestionId(groups, null, -1)).toBe(ids[2]);
  });

  it('steps forwards', () => {
    expect(nextSuggestionId(groups, ids[0], 1)).toBe(ids[1]);
  });

  it('wraps forwards past the end', () => {
    expect(nextSuggestionId(groups, ids[2], 1)).toBe(ids[0]);
  });

  it('wraps backwards past the start', () => {
    expect(nextSuggestionId(groups, ids[0], -1)).toBe(ids[2]);
  });

  it('answers null when there is nothing to highlight', () => {
    expect(nextSuggestionId([], null, 1)).toBeNull();
  });

  /**
   * The list changes under the shopper on every keystroke. An id that no longer names an option has
   * to be treated as "nothing highlighted", because `aria-activedescendant` pointing at an element
   * that has left the document is a screen reader saying nothing at all.
   */
  it('starts again when the highlighted id is no longer in the list', () => {
    expect(nextSuggestionId(groups, 'box-product-9', 1)).toBe(ids[0]);
    expect(suggestionAt(groups, 'box-product-9')).toBeNull();
  });
});

describe('suggestionAt', () => {
  const groups = group('products', 'box', 'Products', productSuggestions([card(1)], 'box'));

  it('finds the option an id names', () => {
    expect(suggestionAt(groups, 'box-product-0')?.label).toBe('Product 1');
  });

  it('answers null for no id at all', () => {
    expect(suggestionAt(groups, null)).toBeNull();
  });
});
