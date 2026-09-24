import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { ProductCardDto, ProductDetailDto } from './catalog.model';
import {
  RECENTLY_VIEWED_KEY,
  RECENTLY_VIEWED_LIMIT,
  RecentlyViewed,
  toRecentCard,
} from './recently-viewed.service';

/** An in-memory stand-in for `localStorage`, as the district selector's tests use. */
class StorageStub {
  readonly entries = new Map<string, string>();

  read(key: string): string | null {
    return this.entries.get(key) ?? null;
  }

  write(key: string, value: string): boolean {
    this.entries.set(key, value);
    return true;
  }

  remove(key: string): boolean {
    return this.entries.delete(key);
  }
}

/** Storage that refuses everything, the way a private window can (lesson L1). */
class BlockedStorage extends StorageStub {
  override read(): null {
    return null;
  }

  override write(): boolean {
    return false;
  }
}

/**
 * The list is read in `afterNextRender`, so a component has to render for it to fill. This is
 * that component, and it is also proof of the rule the service exists to keep: nothing is read
 * from storage until the page is live.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<p>{{ recent.products().length }}</p>`,
})
class Host {
  readonly recent = inject(RecentlyViewed);
}

function card(slug: string, overrides: Partial<ProductCardDto> = {}): ProductCardDto {
  return {
    id: slug.length,
    productCode: `SD-${slug}`,
    name: `Product ${slug}`,
    slug,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    imageUrl: null,
    hoverImageUrl: null,
    imageAltText: null,
    price: { current: 5990, original: 7990, discountPercent: 25, hasDiscount: true, currency: 'LKR' },
    rating: { average: 4.4, count: 12 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
    ...overrides,
  };
}

describe('RecentlyViewed (PDP-07)', () => {
  function setUp(storage: StorageStub = new StorageStub()) {
    // Several of the junk-in-storage cases build a fresh service inside one `it`, and a TestBed
    // cannot be reconfigured once it has made a component.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: SafeLocalStorage, useValue: storage }],
    });

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();

    return {
      storage,
      fixture,
      service: fixture.componentInstance.recent,
      settle: () => fixture.detectChanges(),
    };
  }

  /** What the service wrote, parsed back. */
  function stored(storage: StorageStub): readonly ProductCardDto[] {
    return JSON.parse(storage.entries.get(RECENTLY_VIEWED_KEY) ?? '[]') as ProductCardDto[];
  }

  it('starts empty when nothing has been stored', () => {
    const { service } = setUp();

    expect(service.products()).toEqual([]);
  });

  it('reads what a previous visit left', () => {
    const storage = new StorageStub();
    storage.entries.set(RECENTLY_VIEWED_KEY, JSON.stringify([card('a'), card('b')]));

    expect(setUp(storage).service.products().map((product) => product.slug)).toEqual(['a', 'b']);
  });

  it('puts the newest first and never twice', () => {
    const { service, storage } = setUp();

    service.remember(card('a'));
    service.remember(card('b'));
    service.remember(card('a'));

    expect(service.products().map((product) => product.slug)).toEqual(['a', 'b']);
    expect(stored(storage).map((product) => product.slug)).toEqual(['a', 'b']);
  });

  it('keeps only the most recent few', () => {
    const { service } = setUp();

    for (let index = 0; index < RECENTLY_VIEWED_LIMIT + 4; index++) {
      service.remember(card(`product-${index}`));
    }

    expect(service.products()).toHaveLength(RECENTLY_VIEWED_LIMIT);
    expect(service.products()[0].slug).toBe(`product-${RECENTLY_VIEWED_LIMIT + 3}`);
  });

  it('leaves out the product being looked at', () => {
    const { service } = setUp();

    service.remember(card('a'));
    service.remember(card('b'));

    expect(service.except('b').map((product) => product.slug)).toEqual(['a']);
  });

  describe('junk in storage (lesson L1)', () => {
    function from(raw: string): readonly ProductCardDto[] {
      const storage = new StorageStub();
      storage.entries.set(RECENTLY_VIEWED_KEY, raw);
      return setUp(storage).service.products();
    }

    it('reads unparseable text as nothing', () => {
      expect(from('{not json')).toEqual([]);
      expect(from('')).toEqual([]);
    });

    it('reads a value that is not an array as nothing', () => {
      expect(from('null')).toEqual([]);
      expect(from('42')).toEqual([]);
      expect(from('{"slug":"a"}')).toEqual([]);
      expect(from('"a-string"')).toEqual([]);
    });

    it('drops entries that are not products and keeps the ones that are', () => {
      const raw = JSON.stringify([
        card('good'),
        null,
        'a-string',
        42,
        { slug: 'no-price', name: 'No price', id: 1, departmentName: '', categoryName: '', colours: [] },
        { ...card('no-slug'), slug: '' },
        { ...card('no-name'), name: '   ' },
        { ...card('bad-price'), price: { current: 'free' } },
        { ...card('bad-rating'), rating: { count: 'lots', average: null } },
        { ...card('no-colours'), colours: 'blue' },
        card('also-good'),
      ]);

      expect(from(raw).map((product) => product.slug)).toEqual(['good', 'also-good']);
    });

    it('allows a rating that has no average, which is every unreviewed product', () => {
      const raw = JSON.stringify([{ ...card('unreviewed'), rating: { average: null, count: 0 } }]);

      expect(from(raw).map((product) => product.slug)).toEqual(['unreviewed']);
    });

    it('never keeps more than the cap, however long the stored list is', () => {
      const raw = JSON.stringify(
        Array.from({ length: 40 }, (_, index) => card(`product-${index}`)),
      );

      expect(from(raw)).toHaveLength(RECENTLY_VIEWED_LIMIT);
    });
  });

  it('degrades to nothing saved when storage refuses (lesson L1)', () => {
    const { service } = setUp(new BlockedStorage());

    service.remember(card('a'));

    // The signal still updates, so the row is right for this page view; nothing is persisted.
    expect(service.products().map((product) => product.slug)).toEqual(['a']);
  });
});

describe('toRecentCard (PDP-07)', () => {
  function detail(overrides: Partial<ProductDetailDto> = {}): ProductDetailDto {
    return {
      id: 7,
      productCode: 'SD-0007',
      name: 'High-Rise Straight Jean',
      slug: 'high-rise-straight-jean',
      department: { id: 1, name: 'Women', slug: 'women' },
      breadcrumb: [
        { id: 11, name: 'Denim', slug: 'denim' },
        { id: 12, name: 'Jeans', slug: 'jeans' },
      ],
      shortDescription: null,
      description: null,
      careInstructions: null,
      attributes: {
        material: null,
        materialSlug: null,
        fit: null,
        fitSlug: null,
        occasion: null,
        occasionSlug: null,
        ageGroup: null,
        ageGroupSlug: null,
      },
      price: { current: 5990, original: 7990, discountPercent: 25, hasDiscount: true, currency: 'LKR' },
      minPrice: 5990,
      maxPrice: 5990,
      rating: { average: 4.4, count: 12 },
      badge: 'Sale',
      stockStatus: 'InStock',
      isInStock: true,
      seo: { title: 'Jean', description: null },
      sizeGuide: null,
      newUntilUtc: null,
      images: [
        {
          id: 1,
          url: '/media/1.jpg',
          altText: 'Front view',
          role: 'Gallery',
          colourId: null,
          width: null,
          height: null,
          posterUrl: null,
          sortOrder: 1,
        },
      ],
      colours: [],
      sizes: [],
      variants: [],
      relatedProducts: [],
      ...overrides,
    };
  }

  it('takes the deepest breadcrumb step as the category', () => {
    expect(toRecentCard(detail()).categoryName).toBe('Jeans');
    expect(toRecentCard(detail()).categorySlug).toBe('jeans');
  });

  it('falls back to the department when there is no breadcrumb', () => {
    expect(toRecentCard(detail({ breadcrumb: [] })).categoryName).toBe('Women');
  });

  it('takes the first still as the card image, never the video', () => {
    const withVideo = detail({
      images: [
        {
          id: 9,
          url: '/media/clip.mp4',
          altText: null,
          role: 'Video',
          colourId: null,
          width: null,
          height: null,
          posterUrl: '/media/poster.jpg',
          sortOrder: 0,
        },
        ...detail().images,
      ],
    });

    expect(toRecentCard(withVideo).imageUrl).toBe('/media/1.jpg');
  });

  it('has no image at all for a product with no media', () => {
    expect(toRecentCard(detail({ images: [] })).imageUrl).toBeNull();
  });

  it('produces something the store will accept back', () => {
    const storage = new StorageStub();
    storage.entries.set(RECENTLY_VIEWED_KEY, JSON.stringify([toRecentCard(detail())]));

    TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: SafeLocalStorage, useValue: storage }],
    });

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();

    expect(fixture.componentInstance.recent.products().map((product) => product.slug)).toEqual([
      'high-rise-straight-jean',
    ]);
  });
});
