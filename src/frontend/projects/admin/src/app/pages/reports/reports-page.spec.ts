import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { InventoryReportPage, SalesReport } from '../../core/reporting/reporting.model';
import { ReportsPage } from './reports-page';

const WHOAMI = '/api/admin/auth/whoami';
const NOW = Date.parse('2026-10-15T04:30:00Z');

const SALES: SalesReport = {
  period: { from: '2026-09-16', to: '2026-10-15', days: 30 },
  groupBy: 'Day',
  currency: 'LKR',
  rows: [
    { start: '2026-10-01', end: '2026-10-01', orders: 2, paidOrders: 1, grossRevenue: 2400, itemsRevenue: 2000, deliveryRevenue: 400, refundsCompleted: 0, netRevenue: 2400 },
  ],
  totals: { start: '2026-09-16', end: '2026-10-15', orders: 2, paidOrders: 1, grossRevenue: 2400, itemsRevenue: 2000, deliveryRevenue: 400, refundsCompleted: 0, netRevenue: 2400 },
};

function inventory(page: number, totalCount: number): InventoryReportPage {
  return {
    rows: [
      { variantId: page * 100, productId: 1, productName: 'Linen shirt', productCode: 'SD-1', department: 'Men', sku: `SKU-${page}`, colour: 'White', size: 'M', onHand: 4, reserved: 1, available: 3, threshold: 5, thresholdIsDefault: true, stockStatus: 'LowStock' },
    ],
    page,
    pageSize: 25,
    totalCount,
    defaultThreshold: 5,
    counts: { inStock: 10, lowStock: 1, outOfStock: 2 },
  };
}

describe('ReportsPage (ADM-19)', () => {
  let fixture: ComponentFixture<ReportsPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  function configure(query: Record<string, string>): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { queryParamMap: of(convertToParamMap(query)), snapshot: { queryParamMap: convertToParamMap(query) } },
        },
      ],
    });

    http = TestBed.inject(HttpTestingController);
  }

  async function open(permissions: string[]): Promise<void> {
    fixture = TestBed.createComponent(ReportsPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);
    await settle();

    http.expectOne(WHOAMI).flush({ grantsAllPermissions: false, permissions });
    await settle();
  }

  beforeEach(() => vi.spyOn(Date, 'now').mockReturnValue(NOW));

  afterEach(() => {
    http.verify();
    element?.remove();
    vi.restoreAllMocks();
  });

  it('fetches nothing for a role without Reports.View, and says so', async () => {
    configure({});
    await open(['Dashboard.View', 'Orders.View']);

    expect(byTestId('reports-forbidden')).not.toBeNull();
    http.expectNone(() => true);
  });

  it('offers no Export button to a role that can view but not export', async () => {
    configure({});
    await open(['Reports.View']);

    const request = http.expectOne((candidate) => candidate.url === '/api/admin/reports/sales');
    expect(request.request.params.get('from')).toBe('2026-09-16');
    expect(request.request.params.get('to')).toBe('2026-10-15');
    expect(request.request.params.get('groupBy')).toBe('Day');
    request.flush(SALES);
    await settle();

    expect(byTestId('sales-export')).toBeNull();
    expect(byTestId('reports-no-export')).not.toBeNull();
    expect(byTestId('sales-table')!.querySelector('tfoot')!.textContent).toContain('Rs. 2,400.00');
  });

  it('exports with the token, the period and the grouping applied, and refuses a second press meanwhile', async () => {
    configure({});
    await open(['Reports.View', 'Reports.Export']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/sales').flush(SALES);
    await settle();

    const createObjectURL = vi.fn(() => 'blob:x');
    URL.createObjectURL = createObjectURL;
    URL.revokeObjectURL = vi.fn();
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    const button = byTestId<HTMLButtonElement>('sales-export')!;
    button.click();
    await settle();
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.disabled).toBe(false);

    button.click();
    await settle();

    http.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'tok' });
    const exported = http.expectOne((candidate) => candidate.url === '/api/admin/reports/sales/export');
    expect(exported.request.method).toBe('POST');
    expect(exported.request.headers.get('X-XSRF-TOKEN')).toBe('tok');
    expect(exported.request.params.get('groupBy')).toBe('Day');
    exported.flush(new Blob(['x']), { headers: { 'Content-Disposition': 'attachment; filename=sales-report-2026-09-16-to-2026-10-15.csv' } });
    await settle();

    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(byTestId('sales-status')!.textContent).toContain('sales-report-2026-09-16-to-2026-10-15.csv');
    expect(button.getAttribute('aria-disabled')).toBeNull();
  });

  it('refuses a period longer than 366 days without asking the server', async () => {
    configure({ report: 'customers' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers').flush({
      period: { from: '2026-09-16', to: '2026-10-15', days: 30 }, currency: 'LKR', rows: [], page: 1, pageSize: 25, totalCount: 0, totalRevenue: 0,
    });
    await settle();

    const from = byTestId<HTMLInputElement>('customers-from')!;
    from.value = '2025-10-14';
    from.dispatchEvent(new Event('change'));
    byTestId<HTMLButtonElement>('customers-apply')!.click();
    await settle();

    http.expectNone((candidate) => candidate.url === '/api/admin/reports/customers');
    expect(byTestId('customers-error')!.textContent).toContain('at most 366 days');
  });

  it('opens the inventory report on the status the dashboard link asked for', async () => {
    configure({ report: 'inventory', status: 'LowStock' });
    await open(['Reports.View']);

    const request = http.expectOne((candidate) => candidate.url === '/api/admin/reports/inventory');
    expect(request.request.params.get('status')).toBe('LowStock');
    request.flush(inventory(1, 1));
    await settle();

    expect(byTestId('inventory-counts')!.textContent).toContain('10 in stock · 1 low · 2 out of stock');
    expect(byTestId<HTMLSelectElement>('inventory-status')!.value).toBe('LowStock');
  });

  it('refuses Next on the last page, and keeps the buttons while the next page loads', async () => {
    configure({ report: 'inventory' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/inventory').flush(inventory(1, 30));
    await settle();

    const next = byTestId<HTMLButtonElement>('inventory-next')!;
    next.focus();
    next.click();
    await settle();

    // The page stays on screen while page 2 is asked for, so focus is not thrown to <body>.
    expect(document.activeElement).toBe(next);
    const second = http.expectOne((candidate) => candidate.url === '/api/admin/reports/inventory');
    expect(second.request.params.get('page')).toBe('2');
    second.flush(inventory(2, 30));
    await settle();

    expect(byTestId('inventory-next')!.getAttribute('aria-disabled')).toBe('true');
    byTestId<HTMLButtonElement>('inventory-next')!.click();
    await settle();
    http.expectNone((candidate) => candidate.url === '/api/admin/reports/inventory');
  });

  it('re-homes focus on the status line when a page fails and its buttons go', async () => {
    configure({ report: 'inventory' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/inventory').flush(inventory(1, 30));
    await settle();

    const next = byTestId<HTMLButtonElement>('inventory-next')!;
    next.focus();
    next.click();
    await settle();

    http.expectOne((candidate) => candidate.url === '/api/admin/reports/inventory').flush(null, { status: 500, statusText: 'Server Error' });
    await settle();
    await settle();

    expect(byTestId('inventory-next')).toBeNull();
    expect(document.activeElement).toBe(byTestId('inventory-status-message'));
    expect(byTestId('inventory-status-message')!.textContent).toContain('Something went wrong');
  });
});
