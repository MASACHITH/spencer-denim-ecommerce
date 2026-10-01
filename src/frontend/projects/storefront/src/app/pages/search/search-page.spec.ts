import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { routes } from '../../app.routes';
import { ProductCardDto, ProductSearchResponse } from '../../core/catalog/catalog.model';
import { MAX_LOADED_PAGES, MAX_PAGE_SIZE, PAGE_SIZE } from '../../core/catalog/listing-state';
import { CategoryNodeDto, DepartmentDto } from '../../core/navigation/catalog-departments.model';
import { NavigationService } from '../../core/navigation/navigation.service';
import { RECENT_SEARCHES_KEY } from '../../core/search/recent-searches.service';

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

function node(id: number, name: string, slug: string, children: CategoryNodeDto[] = []): CategoryNodeDto {
  return {
    id,
    name,
    slug,
    depth: 1,
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
    categories: [node(11, 'Jeans', 'jeans'), node(12, 'Dresses', 'dresses')],
  },
  {
    id: 2,
    name: 'Men',
    slug: 'men',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [],
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
    imageSrcSet: null,
    hoverImageUrl: null,
    hoverImageSrcSet: null,
    imageAltText: null,
    price: { current: 5990, original: 7990, discountPercent: 25, hasDiscount: true, currency: 'LKR' },
    rating: { average: 4.4, count: 12 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
  };
}

describe('SearchPage (SRCH-02)', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let storage: StorageStub;

  function text(): string {
    return (harness.routeNativeElement as HTMLElement).textContent ?? '';
  }

  function query<T extends Element>(selector: string): T | null {
    return (harness.routeNativeElement as HTMLElement).querySelector<T>(selector);
  }

  function queryAll<T extends Element>(selector: string): T[] {
    return [...(harness.routeNativeElement as HTMLElement).querySelectorAll<T>(selector)];
  }

  /** The product names on screen, de-duplicated: a restore must never show one twice. */
  function distinctNames(): string[] {
    return [...new Set(queryAll('.card__name').map((name) => name.textContent?.trim() ?? ''))];
  }

  function cardCount(): number {
    return queryAll('.product-grid > li').length;
  }

  function searchRequests(): TestRequest[] {
    return http.match((request) => request.url === '/api/catalog/search');
  }

  function searchRequest(): TestRequest {
    return http.expectOne((request) => request.url === '/api/catalog/search');
  }

  /**
   * Answers a request the way the API would: from the `q`, `page` and `pageSize` it was **actually
   * sent**, never from what the component was hoping for.
   *
   * This is the listing's hard-won rule. Its test helper used to fabricate the rows the component
   * expected, which meant it could only ever confirm the component's arithmetic against itself — and
   * it agreed happily for three rounds while a restored listing was asking for the wrong window and
   * rendering 96 distinct products as 120 cards.
   */
  function answer(request: TestRequest, total: number): void {
    const pageSize = Number(request.request.params.get('pageSize'));
    const page = Number(request.request.params.get('page'));
    const skip = (page - 1) * pageSize;
    const count = Math.max(0, Math.min(pageSize, total - skip));
    const term = request.request.params.get('q') ?? '';

    const body: ProductSearchResponse = {
      term,
      tokens: term.toLowerCase().split(' ').filter((token) => token.length >= 2),
      results: {
        items: Array.from({ length: count }, (_, index) => product(skip + index + 1)),
        page,
        pageSize,
        totalCount: total,
      },
    };

    request.flush(body);
  }

  function flushAll(requests: readonly TestRequest[], total: number): void {
    requests.forEach((request) => answer(request, total));
    harness.detectChanges();
  }

  function primeDepartments(departments: readonly DepartmentDto[] = DEPARTMENTS): void {
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(NavigationService);
    http.expectOne('/api/catalog/departments').flush(departments);
  }

  async function activated(): Promise<void> {
    await TestBed.inject(ApplicationRef).whenStable();
    harness.detectChanges();

    if (!harness.routeDebugElement) {
      throw new Error('The router never activated a component.');
    }
  }

  async function goTo(url: string, departments: readonly DepartmentDto[] = DEPARTMENTS): Promise<void> {
    storage = new StorageStub();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(routes),
        { provide: SafeLocalStorage, useValue: storage },
      ],
    });

    primeDepartments(departments);
    harness = await RouterTestingHarness.create(url);
    await activated();
  }

  async function open(url: string, total = 2): Promise<void> {
    await goTo(url);
    flushAll(searchRequests(), total);
  }

  /** A click that navigates is asynchronous; the reload it causes happens on the way back. */
  async function settle(): Promise<void> {
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  afterEach(() => {
    http.verify();
  });

  // ------------------------------------------------------------------ the route

  it('is reached at /search, and is not read as a department listing', async () => {
    await open('/search?q=linen');

    expect(TestBed.inject(Router).url).toContain('/search');
    // The activated component *is* the host element, so it is the host's own tag that answers this,
    // not a query inside it.
    expect((harness.routeNativeElement as HTMLElement).tagName.toLowerCase()).toBe('sd-search-page');
  });

  /**
   * `/search` is a single segment, and `:department` matches any single segment — which is how
   * `/:department` once swallowed eleven site-wide links. The route table is ordered so that the
   * catalogue cannot decide this address, and the only way to say that in a test is to let the
   * catalogue claim the name and watch it lose.
   */
  it('is the search page even when the catalogue claims a department called search', async () => {
    const impostor: DepartmentDto = {
      ...DEPARTMENTS[0],
      id: 99,
      name: 'Search',
      slug: 'search',
      categories: [],
    };

    await goTo('/search?q=linen', [...DEPARTMENTS, impostor]);

    expect((harness.routeNativeElement as HTMLElement).tagName.toLowerCase()).toBe('sd-search-page');
    const request = searchRequest();
    expect(request.request.params.get('q')).toBe('linen');
    answer(request, 1);
  });

  it('asks the API for the term in the URL', async () => {
    await goTo('/search?q=linen%20shirt');

    const request = searchRequest();
    expect(request.request.params.get('q')).toBe('linen shirt');
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe(String(PAGE_SIZE));
    answer(request, 2);
    harness.detectChanges();

    expect(cardCount()).toBe(2);
    expect(query('h1')?.textContent).toContain('linen shirt');
  });

  it('renders the term in the box on the page, for a shopper who opened a shared link', async () => {
    await open('/search?q=linen');

    expect(query<HTMLInputElement>('input[role="combobox"]')?.getAttribute('value')).toBe('linen');
  });

  // ------------------------------------------------------------------ terms the API would refuse

  it.each(['/search', '/search?q=', '/search?q=a', '/search?q=a%20b'])(
    '%s asks the API for nothing and prompts instead',
    async (url) => {
      await goTo(url);

      expect(searchRequests()).toEqual([]);
      expect(text()).toContain('What are you looking for?');
      expect(text()).toContain('at least 2 characters');
    },
  );

  it('still answers with a page, never a 404 or a 503', async () => {
    await goTo('/search?q=a');

    // The two behaviours the listing routes grew in #30 must not be here: no not-found page and no
    // service-unavailable page, whatever the term.
    expect(query('sd-not-found-page')).toBeNull();
    expect(query('sd-catalog-unavailable-page')).toBeNull();
    expect(query('h1')?.textContent?.trim()).toBe('Search');
  });

  it('normalises a term that could not be encoded rather than failing', async () => {
    await goTo('/search?q=linen%20shirt');
    const request = searchRequest();

    expect(request.request.urlWithParams).toContain('q=linen%20shirt');
    answer(request, 1);
  });

  // ------------------------------------------------------------------ the no-results state

  it('says what it searched for and how to widen it', async () => {
    await open('/search?q=zzzz', 0);

    expect(text()).toContain('We found nothing for “zzzz”');
    expect(text()).toContain('Check the spelling');
    expect(text()).toContain('Try fewer words');
    expect(queryAll('.search__state-actions a').map((link) => link.textContent?.trim())).toEqual([
      'Women',
      'Men',
      'New Arrivals',
      'Sale',
    ]);
  });

  it('offers the filters back when a filter is what emptied it', async () => {
    await open('/search?q=linen&colour=blue', 0);

    expect(text()).toContain('Nothing matches that search and all 1 of your filters');
    expect(queryAll('.search__state-chips .chip')).toHaveLength(1);
    expect(text()).toContain('Search all products');
  });

  it('counts a department as one of those filters, unlike a listing', async () => {
    await open('/search?q=linen&department=women', 0);

    expect(queryAll('.search__state-chips .chip')[0].textContent).toContain('Department: Women');
  });

  // ------------------------------------------------------------------ paging

  /**
   * The duplicate-rendering blocker, asked about the search page.
   *
   * A bookmarked deep page is restored in as few requests as the API's page-size cap allows, and the
   * tail of the last one is sliced off rather than the request's window being moved. The listing
   * rendered 120 cards of which 96 were distinct for three rounds before this was caught, and the
   * arithmetic here is the same arithmetic.
   */
  it('restores a bookmarked deep page without showing a product twice', async () => {
    const total = 399;
    await goTo('/search?q=linen&pages=5');

    const requests = searchRequests();
    expect(requests).toHaveLength(2);
    expect(requests.map((request) => request.request.params.get('pageSize'))).toEqual([
      String(MAX_PAGE_SIZE),
      String(MAX_PAGE_SIZE),
    ]);
    expect(requests.every((request) => request.request.params.get('q') === 'linen')).toBe(true);

    flushAll(requests, total);

    expect(cardCount()).toBe(5 * PAGE_SIZE);
    expect(distinctNames()).toHaveLength(5 * PAGE_SIZE);
  });

  it('appends one page on Load More rather than reloading the grid', async () => {
    await open('/search?q=linen&pages=1', 100);
    expect(cardCount()).toBe(PAGE_SIZE);

    query<HTMLButtonElement>('.search__more button')!.click();
    await settle();

    const requests = searchRequests();
    expect(requests).toHaveLength(1);
    expect(requests[0].request.params.get('page')).toBe('2');
    expect(requests[0].request.params.get('pageSize')).toBe(String(PAGE_SIZE));
    flushAll(requests, 100);

    expect(cardCount()).toBe(2 * PAGE_SIZE);
    expect(distinctNames()).toHaveLength(2 * PAGE_SIZE);
    expect(TestBed.inject(Router).url).toContain('pages=2');
  });

  it('keeps the term in the URL when Load More adds a page', async () => {
    await open('/search?q=linen&pages=1', 100);

    query<HTMLButtonElement>('.search__more button')!.click();
    await settle();
    flushAll(searchRequests(), 100);

    expect(TestBed.inject(Router).url).toContain('q=linen');
  });

  it('stops offering Load More at the ceiling and says why', async () => {
    await goTo(`/search?q=linen&pages=${MAX_LOADED_PAGES}`);
    flushAll(searchRequests(), 10_000);

    expect(query('.search__more button')).toBeNull();
    expect(text()).toContain('That is as many as one page shows');
  });

  // ------------------------------------------------------------------ filters and sort

  it('writes a filter into the query string and starts again at page one', async () => {
    await open('/search?q=linen&pages=3', 100);

    query<HTMLSelectElement>('#search-sort')!.value = 'price-asc';
    query<HTMLSelectElement>('#search-sort')!.dispatchEvent(new Event('change'));
    await settle();
    flushAll(searchRequests(), 100);

    const url = TestBed.inject(Router).url;
    expect(url).toContain('sort=price-asc');
    expect(url).not.toContain('pages=');
  });

  it('sends the filters to the API alongside the term', async () => {
    await goTo('/search?q=linen&department=women&colour=blue&sort=price-asc');

    const request = searchRequest();
    expect(request.request.params.get('department')).toBe('women');
    expect(request.request.params.getAll('colour')).toEqual(['blue']);
    expect(request.request.params.get('sort')).toBe('price-asc');
    answer(request, 1);
  });

  it('shows the department and category as chips, which a listing never does', async () => {
    await open('/search?q=linen&department=women&category=jeans', 3);

    expect(queryAll('.search__chips .chip').map((chip) => chip.textContent?.trim())).toEqual([
      'Department: Women',
      'Category: Jeans',
    ]);
  });

  it('takes the category off with the department, so the API is never asked an ambiguous one', async () => {
    await open('/search?q=linen&department=women&category=jeans', 3);

    queryAll<HTMLButtonElement>('.search__chips .chip')[0].click();
    await settle();
    flushAll(searchRequests(), 3);

    const url = TestBed.inject(Router).url;
    expect(url).not.toContain('department=');
    expect(url).not.toContain('category=');
  });

  it('clears every filter including the department, and keeps the term', async () => {
    await open('/search?q=linen&department=women&colour=blue', 3);

    query<HTMLButtonElement>('.search__clear')!.click();
    await settle();
    flushAll(searchRequests(), 3);

    const url = TestBed.inject(Router).url;
    expect(url).toContain('q=linen');
    expect(url).not.toContain('department=');
    expect(url).not.toContain('colour=');
  });

  it('offers All departments in the filter panel, which a listing must not', async () => {
    await open('/search?q=linen&department=women', 3);

    const labels = queryAll('.search__sidebar .opt span').map((span) => span.textContent?.trim());
    expect(labels).toContain('All departments');
  });

  // ------------------------------------------------------------------ failure paths

  it('says something a shopper can act on when the store answers badly', async () => {
    await goTo('/search?q=linen');
    searchRequest().flush('nope', { status: 500, statusText: 'Server Error' });
    harness.detectChanges();

    expect(text()).toContain('We could not run that search');
    // Scoped to the state block: the filter panel's price bands legitimately say "Rs. 2,500", and
    // an assertion over the whole page would be about those rather than about the error.
    expect(query('.search__results .search__state')?.textContent).not.toContain('500');
    expect(query('.search__state-actions button')?.textContent).toContain('Try again');
  });

  it('retries on demand', async () => {
    await goTo('/search?q=linen');
    searchRequest().flush('nope', { status: 500, statusText: 'Server Error' });
    harness.detectChanges();

    query<HTMLButtonElement>('.search__state-actions button')!.click();
    harness.detectChanges();

    flushAll(searchRequests(), 2);
    expect(cardCount()).toBe(2);
  });

  it('says it could not reach the store when the browser is offline', async () => {
    await goTo('/search?q=linen');
    searchRequest().error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    harness.detectChanges();

    expect(text()).toContain('could not reach the store');
  });

  /**
   * A 400 naming `q` is the API saying the term is unusable. `normaliseTerm` applies the same rules
   * before the request so it should be unreachable, but a hand-built link or a proxy that mangles the
   * query string would land here — and "something went wrong" would be the wrong thing to say.
   */
  it('treats a refused term as a prompt, not as a fault', async () => {
    await goTo('/search?q=linen');
    searchRequest().flush(
      { title: 'One or more query parameters are invalid.', status: 400, errors: { q: ['q must be at least 2 characters.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    harness.detectChanges();

    expect(text()).not.toContain('We could not run that search');
    expect(text()).toContain('We found nothing for');
  });

  it('shows the API’s own message when it refuses a filter rather than the term', async () => {
    await goTo('/search?q=linen&category=jeans');
    searchRequest().flush(
      {
        title: 'One or more query parameters are invalid.',
        status: 400,
        errors: { category: ['category "jeans" names more than one category. Add the department.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    harness.detectChanges();

    expect(text()).toContain('names more than one category');
  });

  // ------------------------------------------------------------------ the page's head, and history

  it('names the search in the title and asks not to be indexed', async () => {
    await open('/search?q=linen');

    expect(TestBed.inject(Title).getTitle()).toBe('Search: linen | Colombo Wardrobe');
    expect(document.head.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe(
      'noindex',
    );
    // A results page is not canonically anything.
    expect(document.head.querySelector('link[rel="canonical"]')).toBeNull();
  });

  it('announces the count to a screen reader', async () => {
    await open('/search?q=linen', 2);

    expect(query('.search__results [role="status"]')?.textContent?.trim()).toBe(
      'Showing 2 of 2 products for linen.',
    );
  });

  it('announces that nothing matched', async () => {
    await open('/search?q=zzzz', 0);

    expect(query('.search__results [role="status"]')?.textContent?.trim()).toBe(
      'No products match zzzz.',
    );
  });

  it('remembers the search, so the overlay can offer it next time', async () => {
    await open('/search?q=linen', 2);

    expect(JSON.parse(storage.entries.get(RECENT_SEARCHES_KEY) ?? '[]')).toEqual(['linen']);
  });

  it('does not remember a term that looks like an order number', async () => {
    await open('/search?q=20260115001', 0);

    expect(storage.entries.has(RECENT_SEARCHES_KEY)).toBe(false);
  });

  it('has exactly one h1', async () => {
    await open('/search?q=linen');

    expect(queryAll('h1')).toHaveLength(1);
  });
});
