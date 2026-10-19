import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { CustomerBuyers, CustomerReportPage, InventoryReportPage, SalesReport } from '../../core/reporting/reporting.model';
import { ChartView } from '../../core/charts/chart';
import { DoughnutSpec, StackedBarSpec } from '../../core/charts/chart-config';
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

function buyers(points: { start: string; buyers: number; firstTimeBuyers: number }[], totals?: { buyers: number; firstTimeBuyers: number }): CustomerBuyers {
  const whole = totals ?? { buyers: points.reduce((sum, point) => sum + point.buyers, 0), firstTimeBuyers: points.reduce((sum, point) => sum + point.firstTimeBuyers, 0) };

  return {
    period: { from: '2026-09-16', to: '2026-10-15', days: 30 },
    interval: 'Day',
    buyers: whole.buyers,
    firstTimeBuyers: whole.firstTimeBuyers,
    returningBuyers: whole.buyers - whole.firstTimeBuyers,
    points: points.map((point) => ({ start: point.start, end: point.start, buyers: point.buyers, firstTimeBuyers: point.firstTimeBuyers, returningBuyers: point.buyers - point.firstTimeBuyers })),
  };
}

function customerPage(page: number, totalCount: number): CustomerReportPage {
  return {
    period: { from: '2026-09-16', to: '2026-10-15', days: 30 },
    currency: 'LKR',
    rows: [{ email: `buyer${page}@example.com`, name: 'Nimali Perera', district: 'Colombo', hasAccount: false, paidOrders: 1, grossRevenue: 2400, firstPaidOn: '2026-10-01', lastPaidOn: '2026-10-01' }],
    page,
    pageSize: 25,
    totalCount,
    totalRevenue: 2400 * totalCount,
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
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers/buyers').flush(buyers([]));
    await settle();

    const from = byTestId<HTMLInputElement>('customers-from')!;
    from.value = '2025-10-14';
    from.dispatchEvent(new Event('change'));
    byTestId<HTMLButtonElement>('customers-apply')!.click();
    await settle();

    http.expectNone((candidate) => candidate.url === '/api/admin/reports/customers');
    http.expectNone((candidate) => candidate.url === '/api/admin/reports/customers/buyers');
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

  // ---- #186 tester: the report charts' figures, which no spec read before -------------------------------------

  function chartNamed(testId: string): ChartView {
    const found = fixture.debugElement.queryAll(By.directive(ChartView)).map((debug) => debug.componentInstance as ChartView).find((chart) => chart.testId() === testId);
    expect(found, `no sda-chart ${testId}`).toBeDefined();
    return found!;
  }

  it('draws the stock-status doughnut from the report counts, each status in its own colour and share (#186 tester)', async () => {
    configure({ report: 'inventory' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/inventory').flush(inventory(1, 1));
    await settle();

    const chart = chartNamed('inventory-chart');
    const spec = chart.spec() as DoughnutSpec;
    expect(spec.slices.map(({ key, value, share, token }) => ({ key, value, share, token }))).toEqual([
      { key: 'InStock', value: 10, share: 76.9, token: '--stock-in' },
      { key: 'LowStock', value: 1, share: 7.7, token: '--stock-low' },
      { key: 'OutOfStock', value: 2, share: 15.4, token: '--stock-out' },
    ]);
    expect(chart.summary()).toBe('Active variants by stock status: 10 variants in stock, 1 variant low, 2 variants out of stock.');
  });

  it('draws gross revenue, not net, one bar per row, and names the highest row and the gross total (#186 tester)', async () => {
    configure({});
    await open(['Reports.View']);
    const row = (start: string, grossRevenue: number, netRevenue: number) => ({ start, end: start, orders: 1, paidOrders: 1, grossRevenue, itemsRevenue: grossRevenue, deliveryRevenue: 0, refundsCompleted: grossRevenue - netRevenue, netRevenue });
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/sales').flush({
      ...SALES,
      rows: [row('2026-10-01', 1000, 1000), row('2026-10-02', 5000, 2000), row('2026-10-03', 3000, 3000)],
      totals: { ...row('2026-09-16', 9000, 6000), end: '2026-10-15' },
    });
    await settle();

    const chart = chartNamed('sales-chart');
    const spec = chart.spec();
    expect(spec.kind === 'bar' ? spec.values : null).toEqual([1000, 5000, 3000]);
    expect(chart.summary()).toMatch(/^Gross revenue by day, Rs\. 9,000\.00 in all\. Highest: 2 Oct 2026, Rs\. 5,000\.00\.$/);
  });

  // ---- #186b: the customers report's buyers chart ---------------------------------------------------------------

  it('draws first-time and returning buyers stacked, one bar per day, with the same figures in its table (#186b)', async () => {
    configure({ report: 'customers' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers').flush(customerPage(1, 3));
    const chartRequest = http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers/buyers');
    expect(chartRequest.request.params.get('from')).toBe('2026-09-16');
    expect(chartRequest.request.params.get('to')).toBe('2026-10-15');
    expect(chartRequest.request.params.has('page')).toBe(false);
    // One buyer paid on both days: three buyers in the period, not four.
    chartRequest.flush(buyers([{ start: '2026-10-01', buyers: 1, firstTimeBuyers: 1 }, { start: '2026-10-02', buyers: 3, firstTimeBuyers: 1 }], { buyers: 3, firstTimeBuyers: 2 }));
    await settle();

    const chart = chartNamed('customers-chart');
    const spec = chart.spec() as StackedBarSpec;
    expect(spec.kind).toBe('stacked');
    expect(spec.labels).toEqual(['1 Oct 2026', '2 Oct 2026']);
    expect(spec.series.map(({ key, values, token }) => ({ key, values, token }))).toEqual([
      { key: 'firstTime', values: [1, 1], token: '--chart-1' },
      { key: 'returning', values: [0, 2], token: '--chart-2' },
    ]);
    expect(chart.summary()).toBe('Buyers by day: 3 buyers in all, 2 first-time and 1 returning; the most in one day was 3 buyers, 2 Oct 2026.');
    expect(byTestId('customers-chart-totals')!.textContent).toContain('3 buyers: 2 first-time, 1 returning');
    expect(byTestId('customers-chart-defs')!.textContent).toContain('first paid order in this shop, ever');

    const rows = [...byTestId('customers-chart-table')!.querySelectorAll('tbody tr')].map((row) => [...row.children].map((cell) => cell.textContent!.trim()));
    expect(rows).toEqual([
      ['1 Oct 2026', '1', '1', '0'],
      ['2 Oct 2026', '3', '1', '2'],
      ['Whole period (each buyer once)', '3', '2', '1'],
    ]);
  });

  it('asks for the chart again on Show, with the search, but not when the page turns (#186b)', async () => {
    configure({ report: 'customers' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers').flush(customerPage(1, 30));
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers/buyers').flush(buyers([{ start: '2026-10-01', buyers: 30, firstTimeBuyers: 30 }]));
    await settle();

    byTestId<HTMLButtonElement>('customers-next')!.click();
    await settle();
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers').flush(customerPage(2, 30));
    http.expectNone((candidate) => candidate.url === '/api/admin/reports/customers/buyers');
    await settle();

    const search = byTestId<HTMLInputElement>('customers-search')!;
    search.value = ' nimali ';
    search.dispatchEvent(new Event('input'));
    byTestId<HTMLButtonElement>('customers-apply')!.click();
    await settle();

    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers').flush(customerPage(1, 1));
    const again = http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers/buyers');
    expect(again.request.params.get('search')).toBe('nimali');
    again.flush(buyers([{ start: '2026-10-01', buyers: 1, firstTimeBuyers: 0 }]));
    await settle();

    expect((chartNamed('customers-chart').spec() as StackedBarSpec).series[1].values).toEqual([1]);
  });

  it('keeps the table when only the chart fails, and says the chart could not be loaded (#186b)', async () => {
    configure({ report: 'customers' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers').flush(customerPage(1, 1));
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers/buyers').flush(null, { status: 500, statusText: 'Server Error' });
    await settle();

    expect(byTestId('customers-table')).not.toBeNull();
    expect(byTestId('customers-chart-problem')!.textContent).toContain('The buyers chart could not be loaded.');
    expect(byTestId('customers-chart-card')).toBeNull();
  });

  it('shows no chart, only the sentence, when nobody paid (#186b)', async () => {
    configure({ report: 'customers' });
    await open(['Reports.View']);
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers').flush({ ...customerPage(1, 0), rows: [], totalRevenue: 0 });
    http.expectOne((candidate) => candidate.url === '/api/admin/reports/customers/buyers').flush(buyers([{ start: '2026-10-01', buyers: 0, firstTimeBuyers: 0 }]));
    await settle();

    expect(byTestId('customers-empty')).not.toBeNull();
    expect(byTestId('customers-chart-card')).toBeNull();
  });
});
