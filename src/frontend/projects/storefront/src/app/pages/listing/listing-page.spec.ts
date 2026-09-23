import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from '../../app.routes';
import { PagedResult, ProductCardDto } from '../../core/catalog/catalog.model';
import { PAGE_SIZE } from '../../core/catalog/listing-state';
import { CategoryNodeDto, DepartmentDto } from '../../core/navigation/catalog-departments.model';

/**
 * A cut of `GET /api/catalog/departments` in the shape the API really sends, descriptions and
 * all — the fallback tree in `navigation.data.ts` carries none, and PLP-01 renders them.
 */
function node(
  id: number,
  name: string,
  slug: string,
  description: string | null,
  children: CategoryNodeDto[] = [],
  depth = 1,
): CategoryNodeDto {
  return {
    id,
    name,
    slug,
    depth,
    description,
    imageUrl: null,
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
    description: 'Everyday essentials and occasion pieces.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      node(11, 'Dresses', 'dresses', 'From weekday shifts to evening midis.'),
      node(12, 'Jeans', 'jeans', 'Denim in high-rise, straight and wide-leg cuts.'),
    ],
  },
  {
    id: 3,
    name: 'Kids',
    slug: 'kids',
    description: 'Girls, boys and baby.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      node(31, 'Girls', 'girls', 'Dresses, tops and bottoms for girls.', [
        node(311, 'Girls Dresses', 'girls-dresses', 'Cotton dresses.', [], 2),
      ]),
    ],
  },
];

function product(id: number): ProductCardDto {
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
    hoverImageUrl: null,
    imageAltText: null,
    price: { current: 5990, original: 7990, discountPercent: 25, hasDiscount: true, currency: 'LKR' },
    rating: { average: 4.4, count: 12 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
  };
}

function page(count: number, total = count, from = 1): PagedResult<ProductCardDto> {
  return {
    items: Array.from({ length: count }, (_, index) => product(from + index)),
    page: 1,
    pageSize: PAGE_SIZE,
    totalCount: total,
  };
}

describe('ListingPage', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  /** The one outstanding `/api/catalog/products` request, whatever its query string. */
  function productRequest(): TestRequest {
    return http.expectOne((request) => request.url === '/api/catalog/products');
  }

  function text(): string {
    return (harness.routeNativeElement as HTMLElement).textContent ?? '';
  }

  function query<T extends Element>(selector: string): T | null {
    return (harness.routeNativeElement as HTMLElement).querySelector<T>(selector);
  }

  function queryAll<T extends Element>(selector: string): T[] {
    return [...(harness.routeNativeElement as HTMLElement).querySelectorAll<T>(selector)];
  }

  /** A click that navigates is asynchronous; the reload it causes happens on the way back. */
  async function settle(): Promise<void> {
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  async function open(url: string, result = page(2)) {
    harness = await RouterTestingHarness.create(url);
    http = TestBed.inject(HttpTestingController);

    // The header's department feed; the listing reads the same tree for its breadcrumb.
    http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);
    productRequest().flush(result);
    harness.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  describe('the request it makes (PLP-01, PLP-02)', () => {
    it('asks for the department the route names', async () => {
      harness = await RouterTestingHarness.create('/women');
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);

      const request = productRequest();
      expect(request.request.params.get('department')).toBe('women');
      expect(request.request.params.get('category')).toBeNull();
      expect(request.request.params.get('page')).toBe('1');
      expect(request.request.params.get('pageSize')).toBe(String(PAGE_SIZE));

      request.flush(page(2));
      harness.detectChanges();
    });

    it('filters on the deepest category segment, which is what the API needs', async () => {
      harness = await RouterTestingHarness.create('/kids/girls/girls-dresses');
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);

      const request = productRequest();
      expect(request.request.params.get('department')).toBe('kids');
      expect(request.request.params.get('category')).toBe('girls-dresses');

      request.flush(page(1));
      harness.detectChanges();
    });

    it('passes the filters and the sort from the query string', async () => {
      harness = await RouterTestingHarness.create(
        '/women/jeans?colour=navy&colour=black&size=M&minPrice=2500&inStock=true&sort=newest',
      );
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);

      const request = productRequest();
      expect(request.request.params.getAll('colour')).toEqual(['navy', 'black']);
      expect(request.request.params.get('size')).toBe('M');
      expect(request.request.params.get('minPrice')).toBe('2500');
      expect(request.request.params.get('inStock')).toBe('true');
      expect(request.request.params.get('sort')).toBe('newest');

      request.flush(page(1));
      harness.detectChanges();
    });
  });

  describe('the page it renders (PLP-01, PLP-05, PLP-06)', () => {
    it('heads a category listing with its breadcrumb, name and description', async () => {
      await open('/women/jeans');

      expect(queryAll('.breadcrumb a, .breadcrumb [aria-current]').map((n) => n.textContent?.trim()))
        .toEqual(['Home', 'Women', 'Jeans']);
      expect(query('h1')?.textContent?.trim()).toBe('Jeans');
      expect(query('.plp__intro')?.textContent).toContain('Denim in high-rise');
    });

    it('heads a department listing with the department', async () => {
      await open('/women');

      expect(query('h1')?.textContent?.trim()).toBe('Women');
    });

    it('renders nothing at all where a promo banner is not set yet', async () => {
      await open('/women/jeans');

      expect(query('.plp__promo')).toBeNull();
    });

    it('shows the live total, not the number of rows it happens to be holding', async () => {
      await open('/women/jeans', page(24, 120));

      expect(query('.plp__count')?.textContent?.replace(/\s+/g, ' ')).toContain('120 products');
      expect(queryAll('.product-grid > li')).toHaveLength(24);
    });

    it('puts the products in the rendered HTML', async () => {
      await open('/women/jeans', page(2));

      expect(text()).toContain('Product 1');
      expect(text()).toContain('Product 2');
    });

    it('offers the seven sorts, with the URL\'s one selected', async () => {
      harness = await RouterTestingHarness.create('/women/jeans?sort=price-desc');
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);
      productRequest().flush(page(1));
      harness.detectChanges();

      const select = query<HTMLSelectElement>('#listing-sort');
      expect(select?.options).toHaveLength(7);
      expect(select?.value).toBe('price-desc');
    });
  });

  describe('active filter chips (PLP-03)', () => {
    it('shows a chip per filter in the URL', async () => {
      await open('/women/jeans?colour=navy&inStock=true');

      expect(queryAll('.chips .chip').map((chip) => chip.textContent?.trim())).toEqual([
        'Colour: Navy',
        'In stock',
      ]);
    });

    it('removing a chip rewrites the URL and reloads without it', async () => {
      await open('/women/jeans?colour=navy&colour=black');

      queryAll<HTMLButtonElement>('.chips .chip')[0].click();
      await settle();

      expect(productRequest().request.params.getAll('colour')).toEqual(['black']);
      expect(TestBed.inject(Router).url).toBe('/women/jeans?colour=black');
    });

    it('Clear filters empties the query string but keeps the page', async () => {
      await open('/women/jeans?colour=navy&inStock=true&sort=newest');

      query<HTMLButtonElement>('.plp__clear')?.click();
      await settle();

      productRequest().flush(page(2));
      expect(TestBed.inject(Router).url).toBe('/women/jeans?sort=newest');
    });
  });

  describe('the empty state (PLP-06)', () => {
    it('tells a shopper with filters on which one to take off', async () => {
      await open('/women/jeans?colour=navy&inStock=true', page(0));

      expect(text()).toContain('No products found');
      expect(queryAll('.plp__state-chips .chip')).toHaveLength(2);
      expect(text()).toContain('Clear all filters');
    });

    it('sends a shopper with no filters somewhere else to look', async () => {
      await open('/women/jeans', page(0));

      expect(text()).toContain('No products found');
      expect(query('.plp__state-actions a')?.getAttribute('href')).toBe('/women');
    });
  });

  describe('a rejected query string', () => {
    it('shows the API\'s own message for an ambiguous category, not a status code', async () => {
      harness = await RouterTestingHarness.create('/women/jeans');
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);

      productRequest().flush(
        {
          title: 'One or more query parameters are invalid.',
          status: 400,
          errors: {
            category: [
              "The category 'jeans' exists in more than one department. Add department=women or department=men.",
            ],
          },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      harness.detectChanges();

      expect(text()).toContain('exists in more than one department');
      expect(text()).not.toContain('400');
      expect(query('h1')).not.toBeNull();
    });

    it('says something a shopper can act on when the API cannot be reached', async () => {
      harness = await RouterTestingHarness.create('/women/jeans');
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);

      productRequest().error(new ProgressEvent('offline'));
      harness.detectChanges();

      expect(text()).toContain('could not reach the store');
      expect(text()).toContain('Try again');
    });
  });

  describe('Load More (PLP-07)', () => {
    it('is offered while the API says there is more', async () => {
      await open('/women/jeans', page(24, 120));

      expect(text()).toContain('Load More');
    });

    it('is not offered once every product is on screen', async () => {
      await open('/women/jeans', page(2, 2));

      expect(text()).not.toContain('Load More');
      expect(text()).toContain('Showing 2 of 2 products');
    });

    it('appends the next page instead of replacing what is on screen', async () => {
      await open('/women/jeans', page(24, 120));

      query<HTMLButtonElement>('.plp__more button')?.click();
      await settle();

      const request = productRequest();
      expect(request.request.params.get('page')).toBe('2');
      expect(request.request.params.get('pageSize')).toBe(String(PAGE_SIZE));

      request.flush({ ...page(24, 120, 25), page: 2 });
      harness.detectChanges();

      expect(queryAll('.product-grid > li')).toHaveLength(48);
      expect(text()).toContain('Product 1');
      expect(text()).toContain('Product 48');
    });

    it('records the depth in the URL, so the listing can be shared and reloaded', async () => {
      await open('/women/jeans', page(24, 120));

      query<HTMLButtonElement>('.plp__more button')?.click();
      await settle();
      productRequest().flush({ ...page(24, 120, 25), page: 2 });

      expect(TestBed.inject(Router).url).toBe('/women/jeans?pages=2');
    });

    it('restores every loaded page from a shared link, in as few requests as the API allows', async () => {
      harness = await RouterTestingHarness.create('/women/jeans?pages=3');
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);

      const request = productRequest();
      expect(request.request.params.get('pageSize')).toBe(String(3 * PAGE_SIZE));

      request.flush(page(72, 120));
      harness.detectChanges();

      expect(queryAll('.product-grid > li')).toHaveLength(72);
    });

    it('announces the new count to a screen reader', async () => {
      await open('/women/jeans', page(24, 120));

      const live = query('[role="status"]');
      expect(live?.textContent?.trim()).toBe('Showing 24 of 120 products.');

      query<HTMLButtonElement>('.plp__more button')?.click();
      await settle();
      productRequest().flush({ ...page(24, 120, 25), page: 2 });
      harness.detectChanges();

      expect(live?.textContent?.trim()).toBe('Showing 48 of 120 products.');
    });
  });

  describe('an address the catalogue does not know', () => {
    it('says so instead of showing an empty listing', async () => {
      harness = await RouterTestingHarness.create('/not-a-department');
      http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);
      productRequest().flush(page(0));
      harness.detectChanges();

      expect(text()).toContain('could not find that page');
      expect(text()).not.toContain('No products found');
    });
  });
});
