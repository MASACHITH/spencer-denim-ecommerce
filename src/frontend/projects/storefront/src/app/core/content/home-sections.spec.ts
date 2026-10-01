import {
  HOME_SECTION_TYPES,
  destinationOf,
  hasContent,
  labelOf,
  renderableSections,
  toLink,
} from './home-sections';
import { HomeSectionDto, HomeSectionItemDto, HomeSectionType } from './content.model';
import { ProductCardDto } from '../catalog/catalog.model';

function item(overrides: Partial<HomeSectionItemDto> = {}): HomeSectionItemDto {
  return {
    id: 1,
    title: null,
    subtitle: null,
    body: null,
    buttonLabel: null,
    linkUrl: null,
    imageUrl: null,
    imageSrcSet: null,
    imageAltText: null,
    mobileImageUrl: null,
    mobileImageSrcSet: null,
    mobileImageAltText: null,
    videoUrl: null,
    videoPosterUrl: null,
    iconKey: null,
    departmentSlug: null,
    departmentName: null,
    productSlug: null,
    sortOrder: 1,
    ...overrides,
  };
}

function section(
  type: HomeSectionType,
  overrides: Partial<HomeSectionDto> = {},
): HomeSectionDto {
  return {
    id: HOME_SECTION_TYPES.indexOf(type) + 1,
    type,
    title: type,
    subtitle: null,
    sortOrder: HOME_SECTION_TYPES.indexOf(type) + 1,
    linkUrl: null,
    linkLabel: null,
    overlay: null,
    items: [],
    products: [],
    ...overrides,
  };
}

function card(id: number): ProductCardDto {
  return {
    id,
    productCode: `SD-${id}`,
    name: `Product ${id}`,
    slug: `product-${id}`,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    imageUrl: null,
    imageSrcSet: null,
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

describe('hasContent', () => {
  /**
   * The rule the whole "no empty boxes" promise rests on. Written over *every* section type
   * rather than the two that were in front of me: a type left out of this list is a type nobody
   * decided about, and the front page is where that shows.
   */
  it('says no to every section type when it arrives with nothing in it', () => {
    const empty = HOME_SECTION_TYPES.filter((type) => !hasContent(section(type)));

    // The newsletter is the one exception: it is a form, and it has content by being itself.
    expect(empty).toEqual(HOME_SECTION_TYPES.filter((type) => type !== 'Newsletter'));
  });

  for (const type of ['HeroVideo', 'CampaignBanner', 'ActivewearFeature'] as const) {
    describe(`${type} (artwork only)`, () => {
      it('needs a picture or a video', () => {
        expect(hasContent(section(type, { items: [item({ imageUrl: '/media/a.webp' })] }))).toBe(true);
        expect(hasContent(section(type, { items: [item({ mobileImageUrl: '/media/a.webp' })] }))).toBe(true);
        expect(hasContent(section(type, { items: [item({ videoUrl: '/media/a.mp4' })] }))).toBe(true);
      });

      it('is not rescued by a heading, which would draw an empty frame', () => {
        expect(
          hasContent(
            section(type, { title: 'Summer', items: [item({ title: 'Summer', body: 'Copy' })] }),
          ),
        ).toBe(false);
      });
    });
  }

  it('SocialGallery needs a photograph, not merely a link', () => {
    expect(hasContent(section('SocialGallery', { items: [item({ linkUrl: 'https://example.com' })] }))).toBe(
      false,
    );
    expect(hasContent(section('SocialGallery', { items: [item({ imageUrl: '/media/a.webp' })] }))).toBe(true);
  });

  it('ShopByDepartment needs a tile with somewhere to go and a name', () => {
    expect(hasContent(section('ShopByDepartment', { items: [item({ linkUrl: '/women' })] }))).toBe(false);
    expect(hasContent(section('ShopByDepartment', { items: [item({ title: 'Women' })] }))).toBe(false);
    expect(
      hasContent(section('ShopByDepartment', { items: [item({ linkUrl: '/women', title: 'Women' })] })),
    ).toBe(true);
  });

  /**
   * The department name arrives from the API alongside the slug, so a tile the client never gave a
   * title to still has one. Without this a seeded tile would be dropped from the page.
   */
  it('ShopByDepartment accepts a tile named only by its department', () => {
    expect(
      hasContent(
        section('ShopByDepartment', {
          items: [item({ departmentSlug: 'women', departmentName: 'Women' })],
        }),
      ),
    ).toBe(true);
  });

  for (const type of ['NewArrivals', 'TrendingItems', 'SaleSection'] as const) {
    it(`${type} is drawn for products, or for tiles the client arranged`, () => {
      expect(hasContent(section(type, { products: [card(1)] }))).toBe(true);
      expect(
        hasContent(section(type, { items: [item({ linkUrl: '/sale', title: 'Under Rs. 5,000' })] })),
      ).toBe(true);
      expect(hasContent(section(type))).toBe(false);
    });
  }

  it('Benefits needs words, and needs no picture', () => {
    expect(hasContent(section('Benefits', { items: [item({ iconKey: 'delivery' })] }))).toBe(false);
    expect(hasContent(section('Benefits', { items: [item({ title: 'Delivery' })] }))).toBe(true);
    expect(hasContent(section('Benefits', { items: [item({ body: 'We deliver.' })] }))).toBe(true);
  });

  it('whitespace is not content', () => {
    expect(hasContent(section('Benefits', { items: [item({ title: '   ', body: '  ' })] }))).toBe(false);
    expect(
      hasContent(section('ShopByDepartment', { items: [item({ linkUrl: '/women', title: '  ' })] })),
    ).toBe(false);
  });

  /**
   * A stored link the tile cannot be drawn with is not a destination, so the tile does not count
   * as content and the section is not drawn half-dead around it.
   *
   * The `https://` case is the one that bit: both tile components render with `routerLink` and
   * drop anything that is not a path on this site, so an absolute URL passed the gate and then
   * rendered nothing — a bare heading over an empty list. Pasting a full
   * `https://colombowardrobecompany.com/women` into a department tile is an ordinary ADM-17 mistake.
   */
  it.each([
    ['a javascript: URL', 'javascript:alert(1)'],
    ['an absolute URL to this very site', 'https://colombowardrobecompany.com/women'],
    ['an absolute URL somewhere else', 'https://example.com/women'],
    ['a protocol-relative URL', '//evil.example/women'],
    ['a relative path with no leading slash', 'women'],
  ])('a tile whose link is %s does not keep its section alive', (_name, linkUrl) => {
    expect(
      hasContent(section('ShopByDepartment', { items: [item({ linkUrl, title: 'Women' })] })),
    ).toBe(false);

    expect(
      hasContent(section('TrendingItems', { items: [item({ linkUrl, title: 'Wide leg' })] })),
    ).toBe(false);
  });
});

describe('renderableSections', () => {
  it('keeps the API order and drops nothing that has content', () => {
    const sections = [
      section('Benefits', { items: [item({ title: 'Delivery' })] }),
      section('ShopByDepartment', { items: [item({ linkUrl: '/women', title: 'Women' })] }),
      section('Newsletter'),
    ];

    expect(renderableSections(sections).map((entry) => entry.type)).toEqual([
      'Benefits',
      'ShopByDepartment',
      'Newsletter',
    ]);
  });

  it('drops the sections with nothing in them and keeps the rest in place', () => {
    const sections = [
      section('HeroVideo'),
      section('ShopByDepartment', { items: [item({ linkUrl: '/women', title: 'Women' })] }),
      section('CampaignBanner'),
      section('Newsletter'),
    ];

    expect(renderableSections(sections).map((entry) => entry.type)).toEqual([
      'ShopByDepartment',
      'Newsletter',
    ]);
  });

  /**
   * A successful response with a useless body is still a successful response, and no `catchError`
   * will ever see it. The department menu learned this in W2-03 the expensive way.
   */
  it.each([
    ['null', null],
    ['undefined', undefined],
    ['an object instead of an array', { oops: true } as never],
  ])('answers nothing for %s', (_name, payload) => {
    expect(renderableSections(payload as never)).toEqual([]);
  });

  it('drops a section whose fields are the wrong shape rather than throwing on it', () => {
    const broken = [{ Type: 'Benefits' } as never, section('Newsletter')];

    expect(renderableSections(broken).map((entry) => entry.type)).toEqual(['Newsletter']);
  });

  it('drops a section whose items are not an array', () => {
    const broken = [{ ...section('Benefits'), items: 'nope' as never }];

    expect(renderableSections(broken)).toEqual([]);
  });
});

describe('destinationOf and labelOf', () => {
  it('prefers the link the client typed, then the department, then the product', () => {
    expect(destinationOf(item({ linkUrl: '/sale', departmentSlug: 'women' }))).toBe('/sale');
    expect(destinationOf(item({ departmentSlug: 'women', productSlug: 'jeans' }))).toBe('/women');
    expect(destinationOf(item({ productSlug: 'wide-leg-jeans' }))).toBe('/product/wide-leg-jeans');
    expect(destinationOf(item())).toBeNull();
  });

  it('names a tile by its title, then by its department', () => {
    expect(labelOf(item({ title: 'Denim', departmentName: 'Women' }))).toBe('Denim');
    expect(labelOf(item({ departmentName: 'Women' }))).toBe('Women');
    expect(labelOf(item({ title: '  ' }))).toBeNull();
  });
});

describe('toLink', () => {
  it('routes a path on this site', () => {
    expect(toLink('/women')).toEqual({ kind: 'internal', path: '/women', queryParams: null });
  });

  /**
   * `routerLink` takes a path. Handed the whole URL as one command it percent-encodes the `?`,
   * and a trending tile pointing at `/women/jeans?fit=wide-leg` lands on a path literally called
   * "jeans?fit=wide-leg" — a 404 dressed up as a filter, on the one feature (HOME-04) whose whole
   * point is that the card opens the matching filtered listing.
   */
  it('splits the query off the path, because routerLink would otherwise encode it', () => {
    expect(toLink('/women/jeans?fit=wide-leg')).toEqual({
      kind: 'internal',
      path: '/women/jeans',
      queryParams: { fit: 'wide-leg' },
    });
  });

  it('keeps every value of a repeatable filter', () => {
    expect(toLink('/women?colour=navy&colour=black&size=M')).toEqual({
      kind: 'internal',
      path: '/women',
      queryParams: { colour: ['navy', 'black'], size: 'M' },
    });
  });

  it('reads a query on the site root as the whole catalogue', () => {
    expect(toLink('/?sort=best-selling')).toEqual({
      kind: 'internal',
      path: '/',
      queryParams: { sort: 'best-selling' },
    });
  });

  it('decodes an escaped value and reads a plus as a space', () => {
    expect(toLink('/women?size=one%20size&colour=off+white')).toEqual({
      kind: 'internal',
      path: '/women',
      queryParams: { size: 'one size', colour: 'off white' },
    });
  });

  it('does not throw on a malformed escape', () => {
    expect(toLink('/women?colour=%zz')).toEqual({
      kind: 'internal',
      path: '/women',
      queryParams: { colour: '%zz' },
    });
  });

  it('reads an empty query as no query at all', () => {
    expect(toLink('/sale?')).toEqual({ kind: 'internal', path: '/sale', queryParams: null });
  });

  it('allows an absolute http(s) address, which is what a social profile is', () => {
    expect(toLink('https://instagram.com/colombowardrobe')).toEqual({
      kind: 'external',
      href: 'https://instagram.com/colombowardrobe',
    });
    expect(toLink('http://example.com')).toEqual({ kind: 'external', href: 'http://example.com' });
  });

  /**
   * The protocol-relative one is the trap: it starts with a slash and is not on this site. The
   * rest would put a script or a data payload straight into an `href`.
   */
  it.each([
    ['//evil.example/sale'],
    ['javascript:alert(1)'],
    ['JavaScript:alert(1)'],
    ['data:text/html,<script>alert(1)</script>'],
    ['vbscript:msgbox(1)'],
    ['women/jeans'],
    [''],
    ['   '],
  ])('refuses %s', (url) => {
    expect(toLink(url)).toBeNull();
  });

  it('refuses nothing at all', () => {
    expect(toLink(null)).toBeNull();
    expect(toLink(undefined)).toBeNull();
  });
});
