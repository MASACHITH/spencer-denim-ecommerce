import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminProductListItem, PagedProducts } from '../../core/products/admin-product.model';
import { ProductsListPage } from './products-list-page';

const ANTIFORGERY = '/api/admin/auth/antiforgery-token';

const OPTIONS = {
  departments: [
    { id: 1, name: 'Women' },
    { id: 2, name: 'Men' },
  ],
  categories: [],
  ageGroups: [],
  materials: [],
  fits: [],
  occasions: [],
  colourFamilies: [],
  sizes: [],
  sizeGuides: [],
};

function item(overrides: Partial<AdminProductListItem> = {}): AdminProductListItem {
  return {
    id: 12,
    productCode: 'SD-W-0001',
    name: 'Wide-Leg Rinse Jeans',
    slug: 'wide-leg-rinse-jeans',
    departmentId: 1,
    departmentName: 'Women',
    primaryCategoryId: 11,
    primaryCategoryName: 'Jeans',
    basePrice: 5490,
    compareAtPrice: 6990,
    badgeOverride: null,
    isPublished: false,
    firstPublishedAtUtc: null,
    updatedAtUtc: '2026-10-05T09:00:00Z',
    variantCount: 6,
    colourCount: 2,
    hasVideo: true,
    cardImageUrl: '/media/one.webp',
    rowVersion: 'rv-1',
    ...overrides,
  };
}

function page(items: readonly AdminProductListItem[] = [item()]): PagedProducts {
  return {
    items,
    page: 1,
    pageSize: 24,
    totalCount: items.length,
    totalPages: items.length === 0 ? 0 : 1,
    hasPreviousPage: false,
    hasNextPage: false,
  };
}

describe('ProductsListPage (ADM-10)', () => {
  let controller: HttpTestingController;
  let fixture: ComponentFixture<ProductsListPage>;
  let element: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ProductsListPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  async function open(payload: PagedProducts = page()): Promise<void> {
    fixture = TestBed.createComponent(ProductsListPage);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();

    controller.expectOne('/api/admin/products/form-options').flush(OPTIONS);
    listRequest().flush(payload);

    await settle();
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function listRequest() {
    return controller.expectOne((request) => request.url === '/api/admin/products');
  }

  function testId<T extends HTMLElement>(id: string): T {
    return element.querySelector(`[data-testid="${id}"]`) as T;
  }

  it('lists an unpublished product and says so', async () => {
    await open();

    expect(testId('product-12')).not.toBeNull();
    expect(testId('state-12').textContent).toContain('Not published');
  });

  it('says there is nothing rather than showing an empty list', async () => {
    await open(page([]));

    expect(testId('empty')).not.toBeNull();
    expect(testId('product-list')).toBeNull();
  });

  it('sends the typed filters only when the admin presses Search', async () => {
    await open();

    const term = testId<HTMLInputElement>('filter-term');
    term.value = 'denim';
    term.dispatchEvent(new Event('input'));
    await settle();

    // Nothing has been requested yet: a request per keystroke would be a request per keystroke.
    testId<HTMLButtonElement>('apply-filters').click();
    await settle();

    const request = listRequest();
    expect(request.request.params.get('term')).toBe('denim');
    request.flush(page());
    await settle();
  });

  it('asks for published only, unpublished only, or both', async () => {
    await open();

    const published = testId<HTMLSelectElement>('filter-published');
    published.value = 'unpublished';
    published.dispatchEvent(new Event('change'));
    await settle();

    testId<HTMLButtonElement>('apply-filters').click();
    await settle();

    const request = listRequest();
    expect(request.request.params.get('published')).toBe('false');
    request.flush(page());
    await settle();
  });

  it('publishes a row through the publish route and carries its row version', async () => {
    await open();

    testId<HTMLButtonElement>('toggle-12').click();
    await settle();

    controller.expectOne(ANTIFORGERY).flush({ token: 'xsrf-token' });

    const request = controller.expectOne('/api/admin/products/12/published');
    expect(request.request.body).toEqual({ isPublished: true, rowVersion: 'rv-1' });
    request.flush(null);

    listRequest().flush(page([item({ isPublished: true })]));
    await settle();

    expect(testId('state-12').textContent).toContain('On the shop');
    expect(testId('announcement').textContent).toContain('is on the shop');
  });

  /**
   * The ADM-22 defect: the re-read after a write must not replace the list with "Loading products…",
   * because that destroys the button the admin pressed and drops their focus to `<body>`.
   */
  it('keeps the list on screen across a publish, so focus survives', async () => {
    await open();

    const button = testId<HTMLButtonElement>('toggle-12');
    button.focus();
    button.click();
    await settle();

    expect(testId('loading')).toBeNull();
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.disabled).toBe(false);
    expect(document.activeElement).toBe(button);

    controller.expectOne(ANTIFORGERY).flush({ token: 'xsrf-token' });
    controller.expectOne('/api/admin/products/12/published').flush(null);
    await settle();

    // **The window that matters.** The write has returned and the re-read is still in flight: this is
    // exactly when a `load()` instead of a `read()` would replace the list with "Loading products…",
    // destroy the row and drop focus to <body>.
    expect(testId('loading')).toBeNull();
    expect(testId('product-list')).not.toBeNull();
    expect(document.activeElement).toBe(button);

    listRequest().flush(page([item({ isPublished: true })]));
    await settle();

    expect(testId('loading')).toBeNull();
    expect(document.activeElement?.getAttribute('data-testid')).toBe('toggle-12');
  });

  it('shows the publish checklist refusal as the page error rather than silently doing nothing', async () => {
    await open();

    testId<HTMLButtonElement>('toggle-12').click();
    await settle();

    controller.expectOne(ANTIFORGERY).flush({ token: 'xsrf-token' });
    controller.expectOne('/api/admin/products/12/published').flush(
      {
        title: 'This change cannot be applied',
        detail: 'This product is not ready to go on the shop yet: add at least one gallery photograph.',
        code: 'publish_checklist_failed',
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();

    expect(testId('page-error').textContent).toContain('gallery photograph');
    expect(testId('state-12').textContent).toContain('Not published');
  });

  it('refuses a second publish while one is in flight', async () => {
    await open();

    testId<HTMLButtonElement>('toggle-12').click();
    testId<HTMLButtonElement>('toggle-12').click();
    await settle();

    controller.expectOne(ANTIFORGERY).flush({ token: 'xsrf-token' });
    controller.expectOne('/api/admin/products/12/published').flush(null);
    listRequest().flush(page([item({ isPublished: true })]));
    await settle();

    // controller.verify() in afterEach is the assertion: a second write would have queued another
    // antiforgery fetch and another PUT.
    expect(testId('state-12').textContent).toContain('On the shop');
  });

  it('shows a forbidden read without offering a pointless retry', async () => {
    fixture = TestBed.createComponent(ProductsListPage);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();

    controller.expectOne('/api/admin/products/form-options').flush(OPTIONS);
    listRequest().flush({ detail: 'no' }, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(testId('page-error').textContent).toContain('do not have permission');
    expect(element.querySelector('[data-testid="page-error"] button')).toBeNull();
  });
});
