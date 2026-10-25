import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';

import { InventoryApi } from '../../core/inventory/inventory-api';
import {
  InventoryFilterOptions,
  InventoryListPage,
  InventoryRow,
  StockChangeResult,
} from '../../core/inventory/inventory.model';
import { InventoryPage } from './inventory-page';

const WHOAMI = '/api/admin/auth/whoami';
const TOKEN = '/api/admin/auth/antiforgery-token';

/** Every field carries a real value, so a field that stops rendering is a failing assertion (W1-12). */
function row(overrides: Partial<InventoryRow> = {}): InventoryRow {
  return {
    variantId: 11,
    productId: 3,
    productName: 'Linen shirt',
    productCode: 'SD-0003',
    departmentName: 'Men',
    categoryName: 'Shirts',
    sku: 'SD-0003-WHT-M',
    colour: 'White',
    size: 'M',
    hasStockRecord: true,
    onHand: 10,
    reserved: 4,
    available: 6,
    threshold: 5,
    ownThreshold: null,
    stockStatus: 'InStock',
    updatedAtUtc: '2026-10-19T04:30:00Z',
    ...overrides,
  };
}

function page(rows: InventoryRow[] = [row(), row({ variantId: 12, sku: 'SD-0003-WHT-L', onHand: 0, reserved: 0, available: 0, stockStatus: 'OutOfStock' })]): InventoryListPage {
  return { rows, page: 1, pageSize: 25, totalCount: rows.length, defaultThreshold: 5, counts: { inStock: 7, lowStock: 2, outOfStock: 3 } };
}

const FILTERS: InventoryFilterOptions = {
  defaultThreshold: 5,
  departments: [
    { id: 1, name: 'Women' },
    { id: 2, name: 'Men' },
  ],
  categories: [
    { id: 10, departmentId: 1, name: 'Dresses', depth: 1 },
    { id: 20, departmentId: 2, name: 'Shirts', depth: 1 },
    { id: 21, departmentId: 2, name: 'Linen', depth: 2 },
  ],
};

describe('InventoryPage (ADM-12)', () => {
  let fixture: ComponentFixture<InventoryPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  function configure(query: Record<string, string> = {}): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
      ],
    });

    http = TestBed.inject(HttpTestingController);
  }

  function listRequest(): TestRequest {
    return http.expectOne((candidate) => candidate.url === InventoryApi.url && candidate.method === 'GET');
  }

  async function open(permissions: string[], payload: InventoryListPage = page()): Promise<void> {
    fixture = TestBed.createComponent(InventoryPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);
    await settle();

    http.expectOne(WHOAMI).flush({ grantsAllPermissions: false, permissions });
    await settle();

    if (permissions.includes('Inventory.View')) {
      http.expectOne(`${InventoryApi.url}/filters`).flush(FILTERS);
      listRequest().flush(payload);
      await settle();
    }
  }

  function type(testId: string, value: string): void {
    const input = byTestId<HTMLInputElement>(testId)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function choose(testId: string, value: string): void {
    const select = byTestId<HTMLSelectElement>(testId)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
  }

  async function submit(testId: string): Promise<void> {
    byTestId<HTMLFormElement>(testId)!.dispatchEvent(new Event('submit'));
    await settle();
  }

  /** The antiforgery token, then the write itself. */
  async function write(): Promise<TestRequest> {
    http.expectOne(TOKEN).flush({ token: 't' });
    await settle();
    const request = http.expectOne(`${InventoryApi.url}/adjustments`);
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('t');
    return request;
  }

  afterEach(() => {
    http.verify();
    element?.remove();
  });

  it('fetches nothing for a role without Inventory.View, and says so', async () => {
    configure();
    await open(['Dashboard.View', 'Reports.View']);

    expect(byTestId('inventory-forbidden')).not.toBeNull();
    http.expectNone(() => true);
  });

  it('shows every figure of a row, the threshold source and the status', async () => {
    configure();
    await open(['Inventory.View']);

    const first = byTestId('variant-11')!;
    expect(first.textContent).toContain('Linen shirt');
    expect(first.textContent).toContain('SD-0003 · Men · Shirts');
    expect(first.textContent).toContain('SD-0003-WHT-M');
    expect(first.textContent).toContain('White / M');
    expect(byTestId('on-hand-11')!.textContent).toBe('10');
    expect(byTestId('available-11')!.textContent).toBe('6');
    expect(byTestId('threshold-11')!.textContent).toContain('5 (default)');
    expect(byTestId('status-11')!.textContent).toContain('In stock');
    expect(byTestId('status-12')!.classList).toContain('adm-pill--hidden');
    expect(byTestId('inventory-counts')!.textContent).toContain('7 in stock');
  });

  /** UAT feedback (2026-10-24): the row buttons and the status were all the same white box. */
  it('colours the status and the row buttons by what they mean', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);

    expect(byTestId('status-11')!.classList).toContain('inv__in');
    expect(byTestId('status-12')!.classList).toContain('inv__out');
    expect(byTestId('change-11')!.classList).toContain('inv__act--stock');
    expect(byTestId('set-threshold-11')!.classList).toContain('inv__act--threshold');
    expect(byTestId('history-11')!.classList).toContain('inv__act--history');
  });

  it('offers a viewer the history but no change, threshold or selection', async () => {
    configure();
    await open(['Inventory.View']);

    expect(byTestId('inventory-read-only')).not.toBeNull();
    expect(byTestId('change-11')).toBeNull();
    expect(byTestId('set-threshold-11')).toBeNull();
    expect(byTestId('select-11')).toBeNull();
    expect(byTestId('history-11')).not.toBeNull();
  });

  it('opens on the status the dashboard link asked for', async () => {
    configure({ status: 'LowStock' });
    fixture = TestBed.createComponent(InventoryPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne(WHOAMI).flush({ grantsAllPermissions: true, permissions: [] });
    await settle();
    http.expectOne(`${InventoryApi.url}/filters`).flush(FILTERS);
    const request = listRequest();

    expect(request.request.params.get('status')).toBe('LowStock');
    request.flush(page());
  });

  it('narrows the categories to the chosen department, sub-categories indented', async () => {
    configure();
    await open(['Inventory.View']);

    choose('inventory-department', '2');
    await settle();
    const categories = Array.from(byTestId<HTMLSelectElement>('inventory-category')!.options).map((option) => option.textContent!);
    expect(categories.map((text) => text.trim())).toEqual(['Every category', 'Shirts', 'Linen']);
    expect(categories[2].startsWith('  ')).toBe(true);
  });

  it('applies the query on Show', async () => {
    configure();
    await open(['Inventory.View']);

    choose('inventory-department', '2');
    choose('inventory-category', '20');
    type('inventory-search', ' linen ');
    choose('inventory-status', 'LowStock');
    choose('inventory-sort', 'Available:Asc');
    element.querySelector<HTMLFormElement>('form[role="search"]')!.dispatchEvent(new Event('submit'));
    await settle();

    const request = listRequest();
    expect(request.request.params.get('search')).toBe('linen');
    expect(request.request.params.get('status')).toBe('LowStock');
    expect(request.request.params.get('departmentId')).toBe('2');
    expect(request.request.params.get('categoryId')).toBe('20');
    expect(request.request.params.get('sort')).toBe('Available');
    expect(request.request.params.get('direction')).toBe('Asc');
    expect(request.request.params.get('page')).toBe('1');
    request.flush(page());
  });

  it('removes units with a reason as a negative Adjust, and patches the row from the answer', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);

    byTestId<HTMLButtonElement>('change-11')!.click();
    await settle();
    expect(byTestId('inventory-editor')!.textContent).toContain('Change stock: SD-0003-WHT-M');

    choose('stock-action', 'remove');
    type('stock-quantity', '2');
    type('stock-reason', '  Damaged in store ');
    await settle();
    expect(byTestId('stock-preview')!.textContent).toContain('After saving: 8 on hand');

    await submit('stock-form');
    const request = await write();
    expect(request.request.body).toEqual({
      reason: 'Damaged in store',
      rows: [{ variantId: 11, kind: 'Adjust', quantity: -2, expectedOnHand: null }],
    });

    request.flush({
      batchId: null,
      appliedCount: 1,
      notAppliedCount: 0,
      rows: [{ variantId: 11, outcome: 'Applied', message: null, onHandDelta: -2, onHand: 8, reserved: 4, available: 4 }],
    } satisfies StockChangeResult);
    await settle();

    expect(byTestId('inventory-editor')).toBeNull();
    expect(byTestId('on-hand-11')!.textContent).toBe('8');
    expect(byTestId('available-11')!.textContent).toBe('4');
    expect(byTestId('status-11')!.textContent).toContain('Low stock');
    expect(byTestId('inventory-said')!.textContent).toContain('8 on hand');
  });

  it('refuses before sending only what it can know: no reason, a fraction', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);
    byTestId<HTMLButtonElement>('change-11')!.click();
    await settle();

    choose('stock-action', 'remove');
    type('stock-quantity', '1.5');
    await submit('stock-form');
    expect(byTestId('stock-quantity-error')!.textContent).toContain('whole number');
    expect(byTestId('stock-reason-error')!.textContent).toContain('Give a reason');

    http.expectNone(`${InventoryApi.url}/adjustments`);
  });

  it('warns about the reserved figure from page load but lets the server decide (D4b)', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);
    byTestId<HTMLButtonElement>('change-11')!.click();
    await settle();

    choose('stock-action', 'remove');
    type('stock-quantity', '7');
    type('stock-reason', 'Lost');
    await settle();
    expect(byTestId('stock-preview')!.textContent).toContain('When this page loaded, 4 units are reserved');

    // The orders holding those units may have been paid or expired since: the change is sent, not blocked.
    await submit('stock-form');
    expect(byTestId('stock-quantity-error')).toBeNull();
    const request = await write();
    expect(request.request.body.rows).toEqual([{ variantId: 11, kind: 'Adjust', quantity: -7, expectedOnHand: null }]);
    request.flush({
      batchId: null,
      appliedCount: 1,
      notAppliedCount: 0,
      rows: [{ variantId: 11, outcome: 'Applied', message: null, onHandDelta: -7, onHand: 3, reserved: 0, available: 3 }],
    } satisfies StockChangeResult);
    await settle();

    expect(byTestId('on-hand-11')!.textContent).toBe('3');
  });

  it('shows the server’s refusal when the reserved floor still holds', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);
    byTestId<HTMLButtonElement>('change-11')!.click();
    await settle();

    choose('stock-action', 'remove');
    type('stock-quantity', '7');
    type('stock-reason', 'Lost');
    await submit('stock-form');
    (await write()).flush({
      batchId: null,
      appliedCount: 0,
      notAppliedCount: 1,
      rows: [{ variantId: 11, outcome: 'Refused', message: '5 units are reserved for unpaid orders, so on hand cannot go below 5. Nothing was changed.', onHandDelta: 0, onHand: 10, reserved: 5, available: 5 }],
    } satisfies StockChangeResult);
    await settle();

    expect(byTestId('stock-refusal')!.textContent).toContain('5 units are reserved');
    expect(byTestId('available-11')!.textContent).toBe('5');
  });

  it('sends a count with the figure on screen, and shows a Changed answer with the new figure', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);
    byTestId<HTMLButtonElement>('change-11')!.click();
    await settle();

    choose('stock-action', 'count');
    type('stock-quantity', '9');
    type('stock-reason', 'Stock-take');
    await submit('stock-form');

    const request = await write();
    expect(request.request.body.rows).toEqual([{ variantId: 11, kind: 'SetCount', quantity: 9, expectedOnHand: 10 }]);
    request.flush({
      batchId: null,
      appliedCount: 0,
      notAppliedCount: 1,
      rows: [{ variantId: 11, outcome: 'Changed', message: 'On hand is now 12, not the 10 you were looking at.', onHandDelta: 0, onHand: 12, reserved: 4, available: 8 }],
    } satisfies StockChangeResult);
    await settle();

    expect(byTestId('stock-refusal')!.textContent).toContain('On hand is now 12');
    expect(byTestId('on-hand-12')).not.toBeNull();
    expect(byTestId('on-hand-11')!.textContent).toBe('12');
    expect(byTestId('stock-preview')!.textContent).toContain('On hand now 12');
  });

  it('puts a threshold back to the shop default with null', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit'], page([row({ ownThreshold: 8, threshold: 8 })]));

    byTestId<HTMLButtonElement>('set-threshold-11')!.click();
    await settle();
    expect(byTestId<HTMLInputElement>('threshold-own')!.checked).toBe(true);
    expect(byTestId<HTMLInputElement>('threshold-value')!.value).toBe('8');

    byTestId<HTMLInputElement>('threshold-default')!.click();
    await settle();
    await submit('threshold-form');

    http.expectOne(TOKEN).flush({ token: 't' });
    await settle();
    const request = http.expectOne(`${InventoryApi.url}/11/threshold`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ lowStockThreshold: null });
    request.flush(row({ ownThreshold: null, threshold: 5 }));
    await settle();

    expect(byTestId('threshold-11')!.textContent).toContain('5 (default)');
  });

  it('refuses a threshold that is not 0 to 10,000', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);
    byTestId<HTMLButtonElement>('set-threshold-11')!.click();
    await settle();

    byTestId<HTMLInputElement>('threshold-own')!.click();
    await settle();
    type('threshold-value', '10001');
    await submit('threshold-form');

    expect(byTestId('threshold-error')!.textContent).toContain('0 to 10,000');
    http.expectNone(`${InventoryApi.url}/11/threshold`);
  });

  it('applies a bulk receive to the selected rows and lists the ones not changed', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);

    byTestId<HTMLInputElement>('select-page')!.click();
    await settle();
    expect(byTestId('bulk-bar')!.textContent).toContain('2 selected');

    byTestId<HTMLButtonElement>('bulk-open')!.click();
    await settle();
    type('bulk-quantity', '5');
    type('bulk-reason', 'Weekly delivery');
    await submit('bulk-form');

    const request = await write();
    expect(request.request.body).toEqual({
      reason: 'Weekly delivery',
      rows: [
        { variantId: 11, kind: 'Receive', quantity: 5, expectedOnHand: null },
        { variantId: 12, kind: 'Receive', quantity: 5, expectedOnHand: null },
      ],
    });
    request.flush({
      batchId: '0b0c7a9e-3b1d-4f0a-9d8e-0a1b2c3d4e5f',
      appliedCount: 1,
      notAppliedCount: 1,
      rows: [
        { variantId: 11, outcome: 'Applied', message: null, onHandDelta: 5, onHand: 15, reserved: 4, available: 11 },
        { variantId: 12, outcome: 'NotFound', message: 'This variant does not exist or has no stock record.', onHandDelta: 0, onHand: null, reserved: null, available: null },
      ],
    } satisfies StockChangeResult);
    await settle();

    expect(byTestId('bulk-result')!.textContent).toContain('1 saved, 1 not changed');
    expect(byTestId('bulk-row-12')!.textContent).toContain('SD-0003-WHT-L');
    expect(byTestId('on-hand-11')!.textContent).toBe('15');
    expect(byTestId('bulk-bar')!.textContent).toContain('1 selected');

    // Tester #190: it is the row that was NOT changed that stays selected, not the one that was.
    expect(byTestId<HTMLInputElement>('select-12')!.checked).toBe(true);
    expect(byTestId<HTMLInputElement>('select-11')!.checked).toBe(false);
  });

  it('puts a 400 on rows[0].quantity next to the quantity box (tester #190)', async () => {
    configure();
    await open(['Inventory.View', 'Inventory.Edit']);
    byTestId<HTMLButtonElement>('change-11')!.click();
    await settle();

    type('stock-quantity', '3');
    type('stock-reason', 'Delivery');
    await submit('stock-form');

    (await write()).flush(
      { title: 'Invalid', status: 400, errors: { 'rows[0].quantity': ['Receive between 1 and 100,000 units.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    expect(byTestId('stock-quantity-error')!.textContent).toContain('Receive between 1 and 100,000 units.');
    expect(byTestId('stock-refusal')).toBeNull();
  });

  it('loads a variant’s history and labels an order’s movement with its number', async () => {
    configure();
    await open(['Inventory.View']);

    byTestId<HTMLButtonElement>('history-11')!.click();
    await settle();

    const request = http.expectOne((candidate) => candidate.url === `${InventoryApi.url}/11/movements`);
    request.flush({
      variant: row(),
      rows: [
        { movementId: 2, movementType: 'Reserve', onHandDelta: 0, reservedDelta: 2, onHandAfter: 10, reservedAfter: 4, orderNumber: 'SD-2026-000123', batchId: null, reason: null, createdBy: null, createdAtUtc: '2026-10-19T04:30:00Z' },
        { movementId: 1, movementType: 'AdjustOut', onHandDelta: -1, reservedDelta: 0, onHandAfter: 10, reservedAfter: 2, orderNumber: null, batchId: null, reason: 'Damaged', createdBy: 'manager@example.com', createdAtUtc: '2026-10-18T04:30:00Z' },
      ],
      page: 1,
      pageSize: 20,
      totalCount: 2,
    });
    await settle();

    expect(byTestId('movement-2')!.textContent).toContain('Held by an order, order SD-2026-000123');
    expect(byTestId('movement-2')!.textContent).toContain('The shop');
    expect(byTestId('movement-1')!.textContent).toContain('-1 → 10');
    expect(byTestId('movement-1')!.textContent).toContain('manager@example.com');
  });
});
