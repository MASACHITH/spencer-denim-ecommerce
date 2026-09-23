import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from '../../app.routes';
import { NavigationService } from '../../core/navigation/navigation.service';
import { PagedResult, ProductCardDto } from '../../core/catalog/catalog.model';
import { MAX_LOADED_PAGES, MAX_PAGE_SIZE, PAGE_SIZE } from '../../core/catalog/listing-state';
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

  /**
   * Answers the department feed before anything navigates.
   *
   * The route guards read it to tell a department from any other first path segment, so a
   * navigation cannot complete until it has an answer. Priming it here rather than after
   * `RouterTestingHarness.create` is what stops the two waiting on each other.
   */
  function primeDepartments(departments: readonly DepartmentDto[] = DEPARTMENTS): void {
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(NavigationService);
    http.expectOne('/api/catalog/departments').flush(departments);
  }

  /**
   * Waits until the router has really put a component on screen.
   *
   * `canMatch` puts an asynchronous hop between "navigate" and "the component exists", and
   * `RouterTestingHarness.create` resolving does not guarantee the other side of it has run. This
   * suite read the listing's requests straight afterwards and went red about one run in fourteen
   * on a loaded machine — over a blocker fix, which is the kind of test that gets deleted rather
   * than trusted. Once the component is up, its constructor has already issued every request the
   * URL implies, synchronously.
   *
   * Note it waits on the component, not on the requests: `HttpTestingController.match` takes the
   * requests it matches off the queue, so polling with it would eat what the test came to flush.
   */
  async function activated(): Promise<void> {
    for (let attempt = 0; attempt < 50; attempt++) {
      harness.detectChanges();
      if (harness.routeDebugElement) {
        return;
      }

      await new Promise((resolve) => setTimeout(resolve, 0));
    }

    throw new Error('The router never activated a component.');
  }

  async function goTo(url: string): Promise<void> {
    primeDepartments();
    harness = await RouterTestingHarness.create(url);
    await activated();
  }

  /** Every outstanding products request, for a restore that needs more than one. */
  function productRequests(): TestRequest[] {
    return http.match((request) => request.url === '/api/catalog/products');
  }

  async function open(url: string, result = page(2)) {
    await goTo(url);
    productRequest().flush(result);
    harness.detectChanges();
  }

  /** Answers a restore that needs more than one request, 96 rows at a time. */
  function flushAll(requests: readonly TestRequest[], total: number): void {
    requests.forEach((request, index) => request.flush(page(96, total, index * 96 + 1)));
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
      await goTo('/women');

      const request = productRequest();
      expect(request.request.params.get('department')).toBe('women');
      expect(request.request.params.get('category')).toBeNull();
      expect(request.request.params.get('page')).toBe('1');
      expect(request.request.params.get('pageSize')).toBe(String(PAGE_SIZE));

      request.flush(page(2));
      harness.detectChanges();
    });

    it('filters on the deepest category segment, which is what the API needs', async () => {
      await goTo('/kids/girls/girls-dresses');

      const request = productRequest();
      expect(request.request.params.get('department')).toBe('kids');
      expect(request.request.params.get('category')).toBe('girls-dresses');

      request.flush(page(1));
      harness.detectChanges();
    });

    it('passes the filters and the sort from the query string', async () => {
      await goTo('/women/jeans?colour=navy&colour=black&size=M&minPrice=2500&inStock=true&sort=newest');

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
      await goTo('/women/jeans?sort=price-desc');
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
      await goTo('/women/jeans');

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
      await goTo('/women/jeans');

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
      await goTo('/women/jeans?pages=3');

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

  describe('the Load More ceiling (PLP-07)', () => {
    /** A ceiling listing is 480 rows, which the API's page-size cap serves in five requests. */
    const CEILING_REQUESTS = Math.ceil((MAX_LOADED_PAGES * PAGE_SIZE) / MAX_PAGE_SIZE);

    it('stops offering Load More at the ceiling instead of collapsing the listing', async () => {
      // One click past the ceiling used to be read back as page 1: 480 products became 24, and
      // the button was still on screen inviting the next one.
      await goTo(`/women/jeans?pages=${MAX_LOADED_PAGES}`);

      const requests = productRequests();
      expect(requests).toHaveLength(CEILING_REQUESTS);
      flushAll(requests, 2000);

      expect(queryAll('.product-grid > li')).toHaveLength(MAX_LOADED_PAGES * PAGE_SIZE);
      expect(text()).not.toContain('Load More');
      expect(text()).toContain('as many as one page shows');
    });

    it('clamps a URL that asks for more pages than the listing will load', async () => {
      await goTo(`/women/jeans?pages=${MAX_LOADED_PAGES + 1}`);

      const requests = productRequests();
      const wanted = requests.reduce(
        (sum, request) => sum + Number(request.request.params.get('pageSize')),
        0,
      );

      expect(wanted).toBe(MAX_LOADED_PAGES * PAGE_SIZE);
      flushAll(requests, 2000);

      expect(queryAll('.product-grid > li')).toHaveLength(MAX_LOADED_PAGES * PAGE_SIZE);
    });
  });

  describe('New Arrivals and Sale (DEPT-02, DEPT-03)', () => {
    it('asks the API for new arrivals across every department', async () => {
      await goTo('/new-arrivals');

      const request = productRequest();
      expect(request.request.params.get('newArrivals')).toBe('true');
      expect(request.request.params.get('department')).toBeNull();

      request.flush(page(3));
      harness.detectChanges();

      expect(query('h1')?.textContent?.trim()).toBe('New Arrivals');
      expect(harness.fixture.nativeElement.ownerDocument.title).toBe(
        'New Arrivals | Spencer Denim',
      );
    });

    it('asks the API for the sale', async () => {
      await goTo('/sale');

      const request = productRequest();
      expect(request.request.params.get('onSale')).toBe('true');

      request.flush(page(3));
      harness.detectChanges();

      expect(query('h1')?.textContent?.trim()).toBe('Sale');
    });

    it("does not offer the listing's own filter as a chip to take off", async () => {
      await open('/sale', page(3));

      expect(queryAll('.chips .chip')).toHaveLength(0);
    });

    it('still lets a shopper narrow the sale, and take that back off', async () => {
      await goTo('/sale?minDiscountPercent=50');
      productRequest().flush(page(3));
      harness.detectChanges();

      expect(queryAll('.chips .chip').map((chip) => chip.textContent?.trim())).toEqual([
        '50% off or more',
      ]);

      queryAll<HTMLButtonElement>('.chips .chip')[0].click();
      await settle();

      expect(TestBed.inject(Router).url).toBe('/sale');
      expect(productRequest().request.params.get('onSale')).toBe('true');
    });

    it('offers no department or category group: the listing has neither', async () => {
      await open('/new-arrivals', page(3));

      expect(text()).not.toContain('Department');
      expect(text()).not.toContain('Category');
    });
  });

  describe('the canonical address (GLB-11)', () => {
    function canonical(): string | null {
      return (harness.routeNativeElement as HTMLElement).ownerDocument
        .querySelector('link[rel="canonical"]')
        ?.getAttribute('href') ?? null;
    }

    it('points an upper-case address at the one the catalogue spells', async () => {
      await open('/WOMEN/JEANS');

      expect(canonical()).toBe('/women/jeans');
    });

    it('keeps the filters and the sort, and drops how much has been loaded', async () => {
      await goTo('/women/jeans?colour=navy&sort=newest&pages=3');
      productRequest().flush(page(72, 200));
      harness.detectChanges();

      expect(canonical()).toBe('/women/jeans?colour=navy&sort=newest');
    });

    it('leaves the fixed filter off a New Arrivals address', async () => {
      await open('/new-arrivals', page(3));

      expect(canonical()).toBe('/new-arrivals');
    });

    it('takes the tag with it when the shopper leaves the listing', async () => {
      // Nothing removed it, so following the wordmark home left the home page claiming to be
      // canonically the jeans listing.
      await open('/women/jeans');
      expect(canonical()).toBe('/women/jeans');

      await harness.navigateByUrl('/');
      http.match((request) => request.url.startsWith('/api/ping')).forEach((ping) => ping.flush({}));
      harness.detectChanges();

      expect(canonical()).toBeNull();
    });
  });

  /**
   * The guards read the department tree to tell a department from any other first path segment.
   * When that tree is unusable they must fall back with the menu, not disagree with it: a
   * `200 []` — a freshly migrated database before the seeder runs — sent every listing URL to the
   * home page while the mega menu still offered all three departments.
   */
  describe('a catalogue that answers with nothing usable', () => {
    async function openWith(body: unknown): Promise<void> {
      http = TestBed.inject(HttpTestingController);
      TestBed.inject(NavigationService);
      http
        .expectOne('/api/catalog/departments')
        .flush(body as readonly DepartmentDto[]);

      harness = await RouterTestingHarness.create('/women');
      await activated();
      productRequest().flush(page(2));
      harness.detectChanges();
    }

    for (const [name, body] of [
      ['an empty catalogue', []],
      ['a body that is not an array', { oops: true }],
    ] as const) {
      it(`still routes /women from the seeded tree with ${name}`, async () => {
        await openWith(body);

        expect(TestBed.inject(Router).url).toBe('/women');
        expect(query('h1')?.textContent?.trim()).toBe('Women');
        expect(text()).not.toContain('could not find that page');
      });
    }

    it('keeps the menu and the routes agreeing about which departments exist', async () => {
      await openWith([]);

      const menu = TestBed.inject(NavigationService)
        .itemsWithMenu()
        .map((item) => item.id);

      expect(menu).toEqual(['women', 'men', 'kids']);
    });
  });

  describe('an address the catalogue does not know', () => {
    /**
     * `/:department` matches any single segment. Before the route guard, every header and footer
     * link that is not built yet rendered this listing's not-found state at HTTP 200 and told the
     * shopper a *department* was missing when they had clicked "Bag".
     */
    for (const path of ['/bag', '/wishlist', '/account', '/privacy', '/contact', '/not-a-department']) {
      it(`${path} goes back to the home page, not to the listing`, async () => {
        await goTo(path);

        // The home page's own wiring check; reaching it is the point of the test.
        http.match((request) => request.url.startsWith('/api/ping')).forEach((ping) => ping.flush({}));

        expect(TestBed.inject(Router).url).toBe('/');
        expect(text()).not.toContain('No products found');
        expect(text()).not.toContain('could not find that page');
      });
    }

    it('shows the missing page for a category that belongs to another department', async () => {
      // /women/shirts names a Men's category. It used to render as the Women department page:
      // an <h1>Women</h1> over "0 products" while /women itself had nineteen.
      await goTo('/women/shirts');

      expect(text()).toContain('could not find that page');
      expect(text()).not.toContain('No products found');
      expect(TestBed.inject(Router).url).toBe('/women/shirts');
    });

    it('asks the API for nothing at all when the page does not exist', async () => {
      await goTo('/women/shirts');

      // `verify()` in afterEach is the assertion: a products request here would be one nobody
      // flushed, and the listing used to fire one for every unknown address.
      expect(text()).toContain('could not find that page');
    });
  });
});
