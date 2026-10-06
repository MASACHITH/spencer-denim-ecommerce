import { ColourValues, ProductSizeOption } from './admin-product.model';
import {
  MatrixRow,
  buildMatrix,
  isUndeletable,
  moveInArray,
  scaleIn,
  sizeIdsIn,
  suggestSku,
  suggestSlug,
  withSortOrder,
} from './variant-matrix';

const SIZES: readonly ProductSizeOption[] = [
  { id: 1, scaleName: 'Alpha', label: 'S', sortOrder: 1 },
  { id: 2, scaleName: 'Alpha', label: 'M', sortOrder: 2 },
  { id: 3, scaleName: 'Alpha', label: 'L', sortOrder: 3 },
  { id: 9, scaleName: 'Kids age', label: '4–5Y', sortOrder: 1 },
];

const sizesById = new Map(SIZES.map((size) => [size.id, size]));

function colour(name: string, familyId = 51): ColourValues {
  return { id: null, name, colourFamilyId: familyId, swatchHex: null, swatchAssetPublicId: null, sortOrder: 0 };
}

function row(overrides: Partial<MatrixRow> & Pick<MatrixRow, 'colourName' | 'sizeId'>): MatrixRow {
  return {
    id: null,
    sku: 'SKU',
    barcode: null,
    priceOverride: null,
    compareAtPriceOverride: null,
    isActive: true,
    sortOrder: 0,
    sizeLabel: sizesById.get(overrides.sizeId)?.label ?? '?',
    sizeScaleName: sizesById.get(overrides.sizeId)?.scaleName ?? '',
    hasOrders: false,
    onHand: 0,
    ...overrides,
  };
}

describe('buildMatrix', () => {
  it('makes one row per colour and size, in colour order then size order', () => {
    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse'), colour('Jet Black')], [1, 2], sizesById, []);

    expect(matrix.map((candidate) => `${candidate.colourName}/${candidate.sizeLabel}`)).toEqual([
      'Indigo Rinse/S',
      'Indigo Rinse/M',
      'Jet Black/S',
      'Jet Black/M',
    ]);

    expect(matrix.map((candidate) => candidate.sortOrder)).toEqual([0, 1, 2, 3]);
  });

  /**
   * The rule most likely to lose an admin's work: ticking one more size must not rebuild the rows that are
   * already there, because each one carries a SKU somebody typed and, for a saved product, the id an order
   * line points at.
   */
  it('keeps every existing row untouched when a size is added', () => {
    const existing = [
      row({ colourName: 'Indigo Rinse', sizeId: 1, id: 77, sku: 'TYPED-BY-HAND', priceOverride: 6490 }),
    ];

    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse')], [1, 2], sizesById, existing);

    expect(matrix).toHaveLength(2);
    expect(matrix[0].id).toBe(77);
    expect(matrix[0].sku).toBe('TYPED-BY-HAND');
    expect(matrix[0].priceOverride).toBe(6490);
    expect(matrix[1].id).toBeNull();
  });

  it('drops a row whose colour or size is no longer chosen', () => {
    const existing = [row({ colourName: 'Jet Black', sizeId: 1, id: 78, sku: 'JB-S' })];

    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse')], [1], sizesById, existing);

    expect(matrix.map((candidate) => candidate.colourName)).toEqual(['Indigo Rinse']);
  });

  /**
   * Doc 08 §3.2 keeps an ordered variant for ever, and the API refuses the delete with 409
   * `variant_not_deletable`. Dropping it from the screen would make that refusal impossible to act on,
   * because the row would not be there to explain it.
   */
  it('keeps an ordered row that is no longer chosen, switched off', () => {
    const existing = [
      row({ colourName: 'Jet Black', sizeId: 1, id: 78, sku: 'JB-S', hasOrders: true, isActive: true }),
    ];

    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse')], [1], sizesById, existing);

    const kept = matrix.find((candidate) => candidate.id === 78);
    expect(kept).toBeDefined();
    expect(kept!.isActive).toBe(false);
  });

  /**
   * The second reason a row cannot be deleted, and the one a real database insists on:
   * `FK_StockLevels_ProductVariants_VariantId` is `NO ACTION`, so deleting a variant that holds stock is
   * refused outright — and it would take a count of real garments with it.
   */
  it('keeps a row that holds stock but has never been ordered, switched off', () => {
    const existing = [
      row({ colourName: 'Jet Black', sizeId: 1, id: 79, sku: 'JB-S', hasOrders: false, onHand: 12 }),
    ];

    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse')], [1], sizesById, existing);

    const kept = matrix.find((candidate) => candidate.id === 79);
    expect(kept).toBeDefined();
    expect(kept!.isActive).toBe(false);
  });

  it('drops a row with no orders and no stock, which is the ordinary correction', () => {
    const existing = [
      row({ colourName: 'Jet Black', sizeId: 1, id: 80, sku: 'JB-S', hasOrders: false, onHand: 0 }),
    ];

    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse')], [1], sizesById, existing);

    expect(matrix.find((candidate) => candidate.id === 80)).toBeUndefined();
  });

  it('suggests a SKU for every new row and never repeats one', () => {
    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse'), colour('indigo rinse ')], [1], sizesById, []);

    const skus = matrix.map((candidate) => candidate.sku);
    expect(new Set(skus).size).toBe(skus.length);
  });

  it('ignores a colour with no name yet, so an empty new row does not make phantom variants', () => {
    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse'), colour('  ')], [1], sizesById, []);

    expect(matrix).toHaveLength(1);
  });

  it('ignores a size the options do not know', () => {
    const matrix = buildMatrix('SD-W-0001', [colour('Indigo Rinse')], [1, 404], sizesById, []);

    expect(matrix).toHaveLength(1);
  });

  /** A colour is matched case-insensitively, the way `UX_ProductColours_Product_Name` compares it. */
  it('matches an existing row whatever case the colour is in', () => {
    const existing = [row({ colourName: 'Indigo Rinse', sizeId: 1, id: 77, sku: 'KEEP-ME' })];

    const matrix = buildMatrix('SD-W-0001', [colour('INDIGO RINSE')], [1], sizesById, existing);

    expect(matrix[0].sku).toBe('KEEP-ME');
  });
});

describe('isUndeletable', () => {
  it('is true for a row an order names and for one that holds stock, false otherwise', () => {
    expect(isUndeletable({ hasOrders: true, onHand: 0 })).toBe(true);
    expect(isUndeletable({ hasOrders: false, onHand: 1 })).toBe(true);
    expect(isUndeletable({ hasOrders: false, onHand: 0 })).toBe(false);
  });
});

describe('suggestSku', () => {
  it('upper-cases and hyphenates the code, the colour and the size', () => {
    expect(suggestSku('SD-W-0001', 'Indigo Rinse', 'M')).toBe('SD-W-0001-INDIGO-RINSE-M');
  });

  it('collapses punctuation rather than leaving it in a varchar column', () => {
    expect(suggestSku('SD/W/1', 'Blue & White', '4–5Y')).toBe('SD-W-1-BLUE-WHITE-4-5Y');
  });

  it('never exceeds the 64 characters varchar(64) allows', () => {
    const long = suggestSku('SD-W-0001', 'A'.repeat(60), 'EXTRA-LARGE');

    expect(long.length).toBeLessThanOrEqual(64);
  });

  it('falls back to SKU rather than an empty string', () => {
    expect(suggestSku('', '', '')).toBe('SKU');
  });
});

describe('suggestSlug', () => {
  it('produces a slug the domain and the check constraint both accept', () => {
    expect(suggestSlug('Wide-Leg Rinse Jeans')).toBe('wide-leg-rinse-jeans');
  });

  it('never leaves a leading, trailing or doubled hyphen', () => {
    expect(suggestSlug('  --Denim  Jacket!!  ')).toBe('denim-jacket');
  });

  it('keeps an accented letter and drops only its accent', () => {
    expect(suggestSlug('Café au Lait')).toBe('cafe-au-lait');
    expect(suggestSlug('Café Déjà-Vu Dress')).toBe('cafe-deja-vu-dress');
    expect(suggestSlug('Über Schön')).toBe('uber-schon');

    // A script with no Latin form still has nothing a varchar slug can hold.
    expect(suggestSlug('ආඩුම')).toBe('');
  });

  it('stays inside varchar(220) and still ends on a letter', () => {
    const slug = suggestSlug('a'.repeat(300));

    expect(slug.length).toBeLessThanOrEqual(220);
    expect(slug.endsWith('-')).toBe(false);
  });
});

describe('moveInArray', () => {
  it('swaps with the neighbour in that direction', () => {
    expect(moveInArray([1, 2, 3], 1, -1)).toEqual([2, 1, 3]);
    expect(moveInArray([1, 2, 3], 1, 1)).toEqual([1, 3, 2]);
  });

  it('refuses a move off either end rather than wrapping round', () => {
    expect(moveInArray([1, 2, 3], 0, -1)).toBeNull();
    expect(moveInArray([1, 2, 3], 2, 1)).toBeNull();
  });
});

describe('withSortOrder', () => {
  it('renumbers to the list order, which is what the admin has just arranged', () => {
    const renumbered = withSortOrder([{ sortOrder: 9 }, { sortOrder: 4 }]);

    expect(renumbered.map((item) => item.sortOrder)).toEqual([0, 1]);
  });
});

describe('sizeIdsIn and scaleIn', () => {
  it('report the sizes and the scale a saved product is already on', () => {
    const rows = [
      row({ colourName: 'Indigo Rinse', sizeId: 1 }),
      row({ colourName: 'Indigo Rinse', sizeId: 2 }),
      row({ colourName: 'Jet Black', sizeId: 1 }),
    ];

    expect(sizeIdsIn(rows)).toEqual([1, 2]);
    expect(scaleIn(rows)).toBe('Alpha');
  });

  it('reports no scale for a product with no variants', () => {
    expect(scaleIn([])).toBeNull();
    expect(sizeIdsIn([])).toEqual([]);
  });
});
