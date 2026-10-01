import {
  ColourSwatchDto,
  PriceDto,
  ProductDetailDto,
  ProductImageDto,
  ProductSizeDto,
  ProductVariantDto,
} from './catalog.model';
import {
  colourOptions,
  galleryFor,
  hasPriceRange,
  priceFor,
  readRequestedSelection,
  resolveSelection,
  sizeOptions,
  stockFor,
  toVariantParams,
  variantFor,
  videosFor,
} from './product-variants';

/*
 * The fixture is a product with a genuinely uneven variant grid, because that is where the
 * interesting behaviour is: Indigo is made in all three sizes but sold out in M, Ecru is not made
 * in L at all, and Rust is made in everything and has none of it. A product where every colour
 * comes in every size and all of it is in stock would agree with almost any implementation.
 */

const INDIGO = 1;
const ECRU = 2;
const RUST = 3;

const SMALL = 10;
const MEDIUM = 11;
const LARGE = 12;

function colour(id: number, name: string, hex: string, family = name): ColourSwatchDto {
  return { id, name, familyName: family, familySlug: family.toLowerCase(), hexCode: hex, swatchImageUrl: null };
}

function size(id: number, label: string, sortOrder: number, isInStock: boolean): ProductSizeDto {
  return { id, label, scaleName: 'Apparel alpha', sortOrder, isInStock };
}

function price(current: number, original = current): PriceDto {
  const discount = original > current ? Math.floor(((original - current) / original) * 100) : 0;
  return {
    current,
    original,
    discountPercent: discount,
    hasDiscount: discount > 0,
    currency: 'LKR',
  };
}

function variant(
  id: number,
  colourId: number,
  sizeId: number,
  available: number,
  amount = 5990,
): ProductVariantDto {
  return {
    id,
    sku: `SD-${colourId}-${sizeId}`,
    colourId,
    colourName: String(colourId),
    sizeId,
    sizeLabel: String(sizeId),
    scaleName: 'Apparel alpha',
    price: price(amount, 7990),
    stockStatus: available === 0 ? 'OutOfStock' : available <= 3 ? 'LowStock' : 'InStock',
    isInStock: available > 0,
    availableQuantity: available,
  };
}

function image(
  id: number,
  colourId: number | null,
  sortOrder: number,
  role: ProductImageDto['role'] = 'Gallery',
): ProductImageDto {
  return {
    id,
    url: `/media/${id}.jpg`,
    srcSet: null,
    thumbnailUrl: null,
    zoomUrl: null,
    altText: null,
    role,
    colourId,
    width: 1200,
    height: 1600,
    posterUrl: role === 'Video' ? '/media/poster.jpg' : null,
    sortOrder,
  };
}

function product(overrides: Partial<ProductDetailDto> = {}): ProductDetailDto {
  return {
    id: 1,
    productCode: 'SD-0001',
    name: 'High-Rise Straight Jean',
    slug: 'high-rise-straight-jean',
    department: { id: 1, name: 'Women', slug: 'women' },
    breadcrumb: [{ id: 12, name: 'Jeans', slug: 'jeans' }],
    shortDescription: 'A clean high-rise straight leg.',
    description: 'Cut from rigid cotton denim with a touch of stretch.',
    careInstructions: 'Machine wash cold, inside out.',
    attributes: {
      material: 'Denim',
      materialSlug: 'denim',
      fit: 'Straight',
      fitSlug: 'straight',
      occasion: null,
      occasionSlug: null,
      ageGroup: null,
      ageGroupSlug: null,
    },
    price: price(5990, 7990),
    minPrice: 5990,
    maxPrice: 6490,
    rating: { average: 4.4, count: 12 },
    badge: 'Sale',
    stockStatus: 'InStock',
    isInStock: true,
    seo: { title: 'High-Rise Straight Jean | Colombo Wardrobe', description: 'Shop it.' },
    sizeGuide: { id: 1, name: "Women's apparel" },
    newUntilUtc: null,
    images: [
      image(101, INDIGO, 1),
      image(102, ECRU, 2),
      image(103, null, 3),
      image(104, INDIGO, 4, 'Video'),
    ],
    colours: [colour(INDIGO, 'Indigo', '#2E6DB4', 'Blue'), colour(ECRU, 'Ecru', '#F3EADA', 'Cream')],
    sizes: [size(SMALL, 'S', 1, true), size(MEDIUM, 'M', 2, true), size(LARGE, 'L', 3, true)],
    variants: [
      variant(1, INDIGO, SMALL, 5),
      variant(2, INDIGO, MEDIUM, 0),
      variant(3, INDIGO, LARGE, 2, 6490),
      variant(4, ECRU, SMALL, 4),
      variant(5, ECRU, MEDIUM, 9),
      // No Ecru in L: this colourway is not made in that size at all.
    ],
    relatedProducts: [],
    ...overrides,
  };
}

describe('readRequestedSelection (PDP-03)', () => {
  it('reads the two ids out of the query string', () => {
    expect(readRequestedSelection({ colour: '2', size: '11' })).toEqual({
      colourId: 2,
      sizeId: 11,
    });
  });

  it('drops anything that is not a positive whole number', () => {
    for (const value of ['0', '-3', 'black', '2.5', '', '  ', '1e999']) {
      expect(readRequestedSelection({ colour: value, size: value })).toEqual({
        colourId: null,
        sizeId: null,
      });
    }
  });

  it('takes the first value when a parameter is repeated', () => {
    expect(readRequestedSelection({ colour: ['2', '1'] }).colourId).toBe(2);
  });
});

describe('resolveSelection (PDP-03)', () => {
  it('opens on the first colourway that has something in stock', () => {
    expect(resolveSelection(product(), { colourId: null, sizeId: null })).toEqual({
      colourId: INDIGO,
      sizeId: null,
    });
  });

  it('skips a sold-out colourway when a live one exists', () => {
    const soldOutIndigo = product({
      colours: [
        colour(INDIGO, 'Indigo', '#2E6DB4'),
        colour(ECRU, 'Ecru', '#F3EADA'),
      ],
      variants: [variant(1, INDIGO, SMALL, 0), variant(2, ECRU, SMALL, 4)],
    });

    expect(resolveSelection(soldOutIndigo, { colourId: null, sizeId: null }).colourId).toBe(ECRU);
  });

  it('falls back to the first colourway when nothing at all is in stock', () => {
    const nothing = product({
      variants: [variant(1, INDIGO, SMALL, 0), variant(2, ECRU, SMALL, 0)],
    });

    expect(resolveSelection(nothing, { colourId: null, sizeId: null }).colourId).toBe(INDIGO);
  });

  it('ignores a colour id the product does not have', () => {
    expect(resolveSelection(product(), { colourId: RUST, sizeId: null }).colourId).toBe(INDIGO);
  });

  it('never guesses a size', () => {
    expect(resolveSelection(product(), { colourId: ECRU, sizeId: null }).sizeId).toBeNull();
  });

  it('keeps a size that is made in the chosen colour but sold out', () => {
    // Indigo M exists and has none left. A shopper may still want to look at it.
    expect(resolveSelection(product(), { colourId: INDIGO, sizeId: MEDIUM })).toEqual({
      colourId: INDIGO,
      sizeId: MEDIUM,
    });
  });

  it('clears a size the chosen colour is not made in', () => {
    // Ecru has no L variant at all, so ?colour=2&size=12 names nothing.
    expect(resolveSelection(product(), { colourId: ECRU, sizeId: LARGE })).toEqual({
      colourId: ECRU,
      sizeId: null,
    });
  });
});

describe('sizeOptions (PDP-03)', () => {
  it('tells a size that is sold out apart from one that is not made', () => {
    const options = sizeOptions(product(), INDIGO);

    expect(options.map((option) => [option.size.label, option.offered, option.isInStock])).toEqual([
      ['S', true, true],
      ['M', true, false],
      ['L', true, true],
    ]);

    const ecru = sizeOptions(product(), ECRU);
    expect(ecru.map((option) => [option.size.label, option.offered, option.isInStock])).toEqual([
      ['S', true, true],
      ['M', true, true],
      ['L', false, false],
    ]);
  });

  it('shows every size the product is made in, whichever colour is chosen', () => {
    expect(sizeOptions(product(), ECRU)).toHaveLength(3);
  });

  it('carries the quantity of the colour and size, not of the size across all colours', () => {
    expect(sizeOptions(product(), INDIGO)[0].availableQuantity).toBe(5);
    expect(sizeOptions(product(), ECRU)[0].availableQuantity).toBe(4);
    // With no colour chosen the whole size is counted: 5 Indigo + 4 Ecru.
    expect(sizeOptions(product(), null)[0].availableQuantity).toBe(9);
  });
});

describe('colourOptions (PDP-03)', () => {
  it('marks a colourway with nothing left as out of stock', () => {
    const options = colourOptions(
      product({ variants: [variant(1, INDIGO, SMALL, 0), variant(2, ECRU, SMALL, 4)] }),
    );

    expect(options.map((option) => [option.colour.name, option.isInStock])).toEqual([
      ['Indigo', false],
      ['Ecru', true],
    ]);
  });
});

describe('priceFor (PDP-02)', () => {
  it('shows the product band until a size is chosen', () => {
    expect(priceFor(product(), { colourId: INDIGO, sizeId: null })).toEqual(product().price);
  });

  it('re-prices on the chosen size, because a variant may override the price', () => {
    const large = priceFor(product(), { colourId: INDIGO, sizeId: LARGE });

    expect(large.current).toBe(6490);
    expect(large.original).toBe(7990);
    // 7990 → 6490 is 18.77%, floored to 18 the way the API floors it.
    expect(large.discountPercent).toBe(18);
    expect(large.hasDiscount).toBe(true);
  });

  it('keeps a product with no discount free of one', () => {
    const flat = product({ price: price(5990), variants: [variant(1, INDIGO, SMALL, 5)] });
    const selected = priceFor(flat, { colourId: INDIGO, sizeId: SMALL });

    expect(priceFor(flat, { colourId: INDIGO, sizeId: null }).hasDiscount).toBe(false);
    // The variant fixture carries its own compare-at price, so this proves the page reads the
    // variant rather than the product once a size is chosen.
    expect(selected.hasDiscount).toBe(true);
  });

  it('says whether the sizes are priced apart, which is what "from" depends on', () => {
    expect(hasPriceRange(product())).toBe(true);
    expect(hasPriceRange(product({ minPrice: 5990, maxPrice: 5990 }))).toBe(false);
  });
});

describe('stockFor (PDP-02)', () => {
  it('reports the variant once one is chosen', () => {
    expect(stockFor(product(), { colourId: INDIGO, sizeId: MEDIUM })).toEqual({
      status: 'OutOfStock',
      available: 0,
    });

    expect(stockFor(product(), { colourId: INDIGO, sizeId: LARGE })).toEqual({
      status: 'LowStock',
      available: 2,
    });
  });

  it('rolls the colourway up while the size is still open', () => {
    // Indigo: 5 + 0 + 2, and at least one size is properly in stock.
    expect(stockFor(product(), { colourId: INDIGO, sizeId: null })).toEqual({
      status: 'InStock',
      available: 7,
    });
  });

  it('calls a colourway with nothing left out of stock', () => {
    const empty = product({
      variants: [variant(1, INDIGO, SMALL, 0), variant(2, INDIGO, MEDIUM, 0), variant(3, ECRU, SMALL, 4)],
    });

    expect(stockFor(empty, { colourId: INDIGO, sizeId: null }).status).toBe('OutOfStock');
  });

  it("falls back to the product's own status with no colour at all", () => {
    expect(stockFor(product({ colours: [], variants: [] }), { colourId: null, sizeId: null })).toEqual({
      status: 'InStock',
      available: 0,
    });
  });
});

describe('galleryFor and videosFor (PDP-01)', () => {
  it('shows the chosen colourway plus the photographs that belong to every colour', () => {
    expect(galleryFor(product(), INDIGO).map((media) => media.id)).toEqual([101, 103]);
    expect(galleryFor(product(), ECRU).map((media) => media.id)).toEqual([102, 103]);
  });

  it('keeps the video out of the stills', () => {
    expect(galleryFor(product(), INDIGO).some((media) => media.role === 'Video')).toBe(false);
    expect(videosFor(product(), INDIGO).map((media) => media.id)).toEqual([104]);
    expect(videosFor(product(), ECRU)).toEqual([]);
  });

  it('falls back to every photograph when a colourway has none of its own', () => {
    const unphotographed = product({ images: [image(201, ECRU, 1), image(202, ECRU, 2)] });

    expect(galleryFor(unphotographed, INDIGO).map((media) => media.id)).toEqual([201, 202]);
  });

  it('has nothing to show for a product with no media, rather than throwing', () => {
    expect(galleryFor(product({ images: [] }), INDIGO)).toEqual([]);
    expect(videosFor(product({ images: [] }), INDIGO)).toEqual([]);
  });

  it('orders by the sort order an admin set, then by id', () => {
    const shuffled = product({
      images: [image(301, null, 9), image(302, null, 1), image(303, null, 1)],
    });

    expect(galleryFor(shuffled, null).map((media) => media.id)).toEqual([302, 303, 301]);
  });
});

describe('toVariantParams (PDP-03)', () => {
  it('writes both ids into the query string', () => {
    expect(toVariantParams({ colourId: 2, sizeId: 11 })).toEqual({ colour: 2, size: 11 });
  });

  it('clears a parameter with null rather than writing an empty one', () => {
    expect(toVariantParams({ colourId: 2, sizeId: null })).toEqual({ colour: 2, size: null });
  });
});

describe('variantFor', () => {
  it('is null until both halves are chosen', () => {
    expect(variantFor(product(), { colourId: INDIGO, sizeId: null })).toBeNull();
    expect(variantFor(product(), { colourId: null, sizeId: SMALL })).toBeNull();
  });

  it('is null for a combination the product does not make', () => {
    expect(variantFor(product(), { colourId: ECRU, sizeId: LARGE })).toBeNull();
  });

  it('finds the exact variant', () => {
    expect(variantFor(product(), { colourId: ECRU, sizeId: MEDIUM })?.sku).toBe('SD-2-11');
  });
});
