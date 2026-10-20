import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  CampaignProductPicker,
  PRODUCT_PICKER_DEBOUNCE_MS,
  PRODUCT_PICKER_PAGE_SIZE,
} from './campaign-product-picker';
import { AdminProductApi } from '../../core/products/admin-product-api';
import { AdminProductListItem } from '../../core/products/admin-product.model';

function product(id: number, name = `Linen Shirt ${id}`, isPublished = true): AdminProductListItem {
  return {
    id,
    productCode: `SD-T-${String(id).padStart(4, '0')}`,
    name,
    slug: `linen-shirt-${id}`,
    departmentId: 3,
    departmentName: 'Women',
    primaryCategoryId: 31,
    primaryCategoryName: 'Shirts & Blouses',
    basePrice: 5490,
    compareAtPrice: null,
    badgeOverride: null,
    isPublished,
    firstPublishedAtUtc: null,
    updatedAtUtc: null,
    variantCount: 1,
    colourCount: 1,
    hasVideo: false,
    cardImageUrl: null,
    rowVersion: 'AAAAAAAAB9M=',
  };
}

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * The campaign product picker (#191): a search-as-you-type combobox over the paged product list.
 *
 * What jsdom can prove: the request it makes (term, page, page size, sort), the ARIA 1.2 combobox
 * wiring, the keyboard, paging, and that an overtaken answer is never shown. What it cannot — that the
 * list is reachable and readable at 320 px — is `admin-sale-campaigns.e2e.ts`'s.
 */
describe('CampaignProductPicker (#191)', () => {
  let fixture: ComponentFixture<CampaignProductPicker>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let picked: AdminProductListItem[];
  let refused: string[];

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function box(): HTMLInputElement {
    return element.querySelector<HTMLInputElement>('[data-testid="product-search"]')!;
  }

  function typeTerm(value: string): void {
    box().value = value;
    box().dispatchEvent(new Event('input'));
  }

  function press(key: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, cancelable: true, bubbles: true });
    box().dispatchEvent(event);
    return event;
  }

  function status(): string {
    return element.querySelector('[data-testid="product-search-status"]')?.textContent?.trim() ?? '';
  }

  function options(): HTMLElement[] {
    return Array.from(element.querySelectorAll<HTMLElement>('[role="option"]'));
  }

  function pending(): TestRequest {
    return http.expectOne((request) => request.url === AdminProductApi.productsUrl);
  }

  function answer(request: TestRequest, items: readonly AdminProductListItem[], totalCount = items.length, page = 1): void {
    request.flush({
      items,
      page,
      pageSize: PRODUCT_PICKER_PAGE_SIZE,
      totalCount,
      totalPages: Math.ceil(totalCount / PRODUCT_PICKER_PAGE_SIZE),
      hasPreviousPage: page > 1,
      hasNextPage: page * PRODUCT_PICKER_PAGE_SIZE < totalCount,
    });
  }

  async function searchFor(term: string, items: readonly AdminProductListItem[], totalCount = items.length): Promise<void> {
    typeTerm(term);
    await pause(PRODUCT_PICKER_DEBOUNCE_MS + 50);
    answer(pending(), items, totalCount);
    await settle();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CampaignProductPicker);
    element = fixture.nativeElement as HTMLElement;
    picked = [];
    refused = [];
    fixture.componentInstance.picked.subscribe((item) => picked.push(item));
    fixture.componentInstance.refused.subscribe((message) => refused.push(message));
    await settle();
  });

  afterEach(() => {
    http.verify();
  });

  it('is an ARIA 1.2 combobox that owns a listbox and starts closed', () => {
    const input = box();

    expect(input.getAttribute('role')).toBe('combobox');
    expect(input.getAttribute('aria-autocomplete')).toBe('list');
    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(input.getAttribute('aria-controls')).toBe('campaign-product-results');
    expect(input.hasAttribute('aria-activedescendant')).toBe(false);
    expect(element.querySelector('#campaign-product-results')?.getAttribute('role')).toBe('listbox');

    // Labelled by a real <label>, described by the keyboard hint and the live status.
    expect(element.querySelector('label[for="campaign-product-search"]')).not.toBeNull();
    expect(input.getAttribute('aria-describedby')).toBe('campaign-product-hint campaign-product-status');
  });

  it('asks the server for one page of the typed term, sorted by name, after a pause', async () => {
    typeTerm('lin');

    // Nothing yet: the box waits for the staff member to stop typing.
    http.expectNone((request) => request.url === AdminProductApi.productsUrl);

    await pause(PRODUCT_PICKER_DEBOUNCE_MS + 50);

    const request = pending();

    expect(request.request.params.get('term')).toBe('lin');
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe(String(PRODUCT_PICKER_PAGE_SIZE));
    expect(request.request.params.get('sort')).toBe('name');

    answer(request, [product(1), product(2)]);
    await settle();

    expect(box().getAttribute('aria-expanded')).toBe('true');
    expect(options().length).toBe(2);
    expect(status()).toBe('2 products match.');
  });

  it('sends one request for a burst of typing, for the last term', async () => {
    typeTerm('l');
    typeTerm('li');
    typeTerm('lin');
    await pause(PRODUCT_PICKER_DEBOUNCE_MS + 50);

    const request = pending();
    expect(request.request.params.get('term')).toBe('lin');
    answer(request, []);
    await settle();
  });

  it('moves the highlight with the arrow keys, wrapping, and adds the highlighted product on Enter', async () => {
    await searchFor('lin', [product(1), product(2), product(3)]);

    // Opening highlights the first option.
    expect(box().getAttribute('aria-activedescendant')).toBe('campaign-product-option-1');
    expect(options()[0].getAttribute('aria-selected')).toBe('true');

    press('ArrowDown');
    await settle();
    expect(box().getAttribute('aria-activedescendant')).toBe('campaign-product-option-2');

    press('ArrowUp');
    press('ArrowUp');
    await settle();
    expect(box().getAttribute('aria-activedescendant')).toBe('campaign-product-option-3');

    const enter = press('Enter');
    await settle();

    expect(enter.defaultPrevented).toBe(true);
    expect(picked.map((item) => item.id)).toEqual([3]);

    // The list stays open, so the next product is one keystroke away.
    expect(box().getAttribute('aria-expanded')).toBe('true');
  });

  it('never lets Enter submit the form, even with nothing highlighted', async () => {
    typeTerm('x');
    const enter = press('Enter');

    expect(enter.defaultPrevented).toBe(true);
    expect(picked).toEqual([]);

    await pause(PRODUCT_PICKER_DEBOUNCE_MS + 50);
    answer(pending(), []);
    await settle();
  });

  it('closes on Escape, and a second Escape clears the box', async () => {
    await searchFor('lin', [product(1)]);

    press('Escape');
    await settle();

    expect(box().getAttribute('aria-expanded')).toBe('false');
    expect(box().hasAttribute('aria-activedescendant')).toBe(false);
    expect(box().value).toBe('lin');

    press('Escape');
    await settle();

    expect(box().value).toBe('');

    // And Down opens it again on the same results when there still are some.
    await searchFor('lin', [product(1)]);
    press('Escape');
    await settle();
    press('ArrowDown');
    await settle();
    expect(box().getAttribute('aria-expanded')).toBe('true');
  });

  it('adds a clicked option without taking focus out of the box', async () => {
    await searchFor('lin', [product(1), product(2)]);

    const option = options()[1];
    const mousedown = new MouseEvent('mousedown', { cancelable: true, bubbles: true });
    option.dispatchEvent(mousedown);
    option.click();
    await settle();

    expect(mousedown.defaultPrevented).toBe(true);
    expect(picked.map((item) => item.id)).toEqual([2]);
  });

  it('marks and refuses a product already on the campaign', async () => {
    fixture.componentRef.setInput('chosen', [2]);
    await searchFor('lin', [product(1), product(2)]);

    expect(options()[1].textContent).toContain('already on this campaign');
    expect(options()[0].textContent).not.toContain('already on this campaign');

    options()[1].click();
    await settle();

    expect(picked).toEqual([]);
    expect(refused).toEqual(['Linen Shirt 2 is already on this campaign.']);
  });

  it('labels an unpublished product rather than hiding it', async () => {
    await searchFor('dress', [product(7, 'Linen Shirt Dress', false)]);

    expect(options()[0].textContent).toContain('not published');
  });

  it('pages through a long result with the buttons and with Page Down and Page Up', async () => {
    const firstPage = Array.from({ length: PRODUCT_PICKER_PAGE_SIZE }, (_, index) => product(index + 1));
    await searchFor('linen', firstPage, 25);

    expect(status()).toBe('25 products match. Showing 1 to 10.');
    expect(element.querySelector('[data-testid="product-results-previous"]')?.getAttribute('aria-disabled')).toBe('true');

    element.querySelector<HTMLElement>('[data-testid="product-results-next"]')!.click();

    const second = pending();
    expect(second.request.params.get('page')).toBe('2');
    expect(second.request.params.get('term')).toBe('linen');
    answer(second, Array.from({ length: 10 }, (_, index) => product(index + 11)), 25, 2);
    await settle();

    expect(status()).toBe('25 products match. Showing 11 to 20.');
    expect(box().getAttribute('aria-activedescendant')).toBe('campaign-product-option-11');

    const pageDown = press('PageDown');
    expect(pageDown.defaultPrevented).toBe(true);

    const third = pending();
    expect(third.request.params.get('page')).toBe('3');
    answer(third, Array.from({ length: 5 }, (_, index) => product(index + 21)), 25, 3);
    await settle();

    expect(status()).toBe('25 products match. Showing 21 to 25.');
    expect(element.querySelector('[data-testid="product-results-next"]')?.getAttribute('aria-disabled')).toBe('true');

    // No page four to ask for.
    press('PageDown');
    http.expectNone((request) => request.url === AdminProductApi.productsUrl);

    press('PageUp');
    const back = pending();
    expect(back.request.params.get('page')).toBe('2');
    answer(back, Array.from({ length: 10 }, (_, index) => product(index + 11)), 25, 2);
    await settle();
  });

  it('says when nothing matches, and closes', async () => {
    await searchFor('zzz', []);

    expect(status()).toBe('No product matches “zzz”.');
    expect(box().getAttribute('aria-expanded')).toBe('false');
  });

  it('never shows the answer to a search the staff member has moved on from', async () => {
    typeTerm('linen');
    await pause(PRODUCT_PICKER_DEBOUNCE_MS + 50);
    const stale = pending();

    // The box is cleared before the answer arrives.
    typeTerm('');
    answer(stale, [product(1)]);
    await settle();

    expect(options().length).toBe(0);
    expect(box().getAttribute('aria-expanded')).toBe('false');
    expect(status()).toBe('');
  });

  it('names the permission when the product list is forbidden', async () => {
    typeTerm('lin');
    await pause(PRODUCT_PICKER_DEBOUNCE_MS + 50);
    pending().flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(status()).toContain('"Products" permission');
    expect(box().getAttribute('aria-expanded')).toBe('false');
  });

  it('says a search failed without throwing the typed term away', async () => {
    typeTerm('lin');
    await pause(PRODUCT_PICKER_DEBOUNCE_MS + 50);
    pending().flush(null, { status: 500, statusText: 'Server Error' });
    await settle();

    expect(status()).toContain('could not be searched');
    expect(box().value).toBe('lin');
  });
});
