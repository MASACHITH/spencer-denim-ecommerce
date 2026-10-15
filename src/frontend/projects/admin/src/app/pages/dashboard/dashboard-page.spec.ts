import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AdminDashboard, ComparedFigure, LowStock, SalesTrend } from '../../core/reporting/reporting.model';
import { DashboardPage } from './dashboard-page';

const WHOAMI = '/api/admin/auth/whoami';

/** 15 October 2026, 18:31 UTC: already 16 October in Colombo, still the 15th in UTC. */
const NOW = Date.parse('2026-10-15T18:31:00Z');

function compared(current: number | null, previous: number | null, change: number | null, percentChange: number | null): ComparedFigure {
  return { current, previous, change, percentChange };
}

function dashboard(overrides: Partial<AdminDashboard> = {}): AdminDashboard {
  return {
    period: { from: '2026-09-17', to: '2026-10-16', days: 30 },
    previousPeriod: { from: '2026-08-18', to: '2026-09-16', days: 30 },
    currency: 'LKR',
    kpis: {
      grossRevenue: compared(125000, 0, 125000, null),
      refundsCompleted: compared(0, 0, 0, null),
      netRevenue: compared(125000, 0, 125000, null),
      orders: compared(0, 4, -4, -100),
      paidOrders: compared(3, 2, 1, 50),
      averageOrderValue: compared(41666.67, null, null, null),
    },
    departments: [
      { id: 1, name: 'Women', units: 4, revenue: 90000, sharePercent: 75 },
      { id: 2, name: 'Men', units: 1, revenue: 30000, sharePercent: 25 },
      { id: 3, name: 'Kids', units: 0, revenue: 0, sharePercent: 0 },
    ],
    topCategories: [{ id: 9, name: 'Women · Dresses', units: 4, revenue: 90000, sharePercent: 75 }],
    topProducts: [
      { productId: 5, name: 'Linen wrap dress', productCode: 'SD-0005', units: 4, revenue: compared(90000, 0, 90000, null) },
    ],
    orderStatuses: [
      { status: 'PendingPayment', label: 'Awaiting payment', count: 1 },
      { status: 'Paid', label: 'Paid', count: 3 },
    ],
    paymentMethods: [
      { code: 'BankTransfer', name: 'Bank transfer', paidOrders: 2, revenue: 80000 },
      { code: 'PayPal', name: 'PayPal', paidOrders: 1, revenue: 45000 },
    ],
    buyersByDistrict: {
      distinctBuyers: 2,
      districts: [
        { districtId: 4, name: 'Kandy', buyers: 2, paidOrders: 2, revenue: 80000 },
        { districtId: 7, name: 'Galle', buyers: 1, paidOrders: 1, revenue: 45000 },
      ],
    },
    ...overrides,
  };
}

const TREND: SalesTrend = {
  interval: 'Month',
  window: { from: '2025-11-01', to: '2026-10-16', days: 350 },
  currency: 'LKR',
  points: [
    { start: '2025-11-01', end: '2025-11-30', grossRevenue: 0, paidOrders: 0, orders: 0 },
    { start: '2026-10-01', end: '2026-10-16', grossRevenue: 125000, paidOrders: 3, orders: 4 },
  ],
};

const LOW: LowStock = { defaultThreshold: 5, totalCount: 0, rows: [] };

describe('DashboardPage (ADM-04, ADM-05, ADM-07, ADM-08)', () => {
  let fixture: ComponentFixture<DashboardPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  /** Opens the page as a role holding `permissions`, answering every request it makes. */
  async function open(permissions: string[] | 'all', payload = dashboard()): Promise<TestRequest | null> {
    fixture = TestBed.createComponent(DashboardPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);

    await settle();
    http.expectOne(WHOAMI).flush({
      grantsAllPermissions: permissions === 'all',
      permissions: permissions === 'all' ? [] : permissions,
    });
    await settle();

    const request = http.match((candidate) => candidate.url === '/api/admin/dashboard')[0] ?? null;
    request?.flush(payload);
    await settle();

    for (const trend of http.match((candidate) => candidate.url === '/api/admin/dashboard/trend')) {
      trend.flush(TREND);
    }

    for (const low of http.match((candidate) => candidate.url === '/api/admin/dashboard/low-stock')) {
      low.flush(LOW);
    }

    for (const orders of http.match((candidate) => candidate.url === '/api/admin/orders')) {
      orders.flush({ rows: [], page: 1, pageSize: 10, totalCount: 0, sort: 'Newest', statusCounts: {} });
    }

    await settle();
    return request;
  }

  beforeEach(() => {
    vi.spyOn(Date, 'now').mockReturnValue(NOW);

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    element?.remove();
    vi.restoreAllMocks();
  });

  it('asks for the last 30 Colombo days, today included, and the trend to the same day', async () => {
    const request = await open('all');

    // 18:31 UTC on the 15th is the 16th in Colombo.
    expect(request!.request.params.get('from')).toBe('2026-09-17');
    expect(request!.request.params.get('to')).toBe('2026-10-16');
    // The month names are the browser's ("Sep" or "Sept"); the days are what is under test.
    expect(byTestId('dashboard-period')!.textContent).toMatch(/17 Sept? 2026 – 16 Oct 2026 \(30 days\), compared with 18 Aug 2026 – 16 Sept? 2026/);
  });

  it('words a rise from nothing, a fall to nothing and an average with nothing to compare', async () => {
    await open('all');

    expect(byTestId('kpi-gross')!.textContent).toContain('Rs. 125,000.00');
    expect(byTestId('kpi-gross')!.textContent).toContain('Up from none in the previous period');
    expect(byTestId('kpi-gross')!.textContent).not.toContain('%');
    expect(byTestId('kpi-orders')!.textContent).toContain('−100.0% on the previous period');
    expect(byTestId('kpi-paid')!.textContent).toContain('+50.0% on the previous period');
    expect(byTestId('kpi-aov')!.textContent).toContain('Nothing to compare with the previous period');
    expect(byTestId('kpi-aov')!.textContent).toContain('Previous period: —');
  });

  it('has no conversion rate and no traffic widget: the analytics tables do not exist', async () => {
    await open('all');

    expect(element.textContent).not.toMatch(/conversion|traffic|visits|sessions/i);
  });

  it('renders every breakdown as a table with its figures, not only bars', async () => {
    await open('all');

    const departments = byTestId<HTMLTableElement>('dashboard-departments')!;
    expect(departments.tagName).toBe('TABLE');
    expect(departments.querySelector('caption')!.textContent).toContain('Sales by department');
    expect(departments.tBodies[0].rows).toHaveLength(3);
    expect(departments.tBodies[0].rows[0].textContent).toContain('Women');
    expect(departments.tBodies[0].rows[0].textContent).toContain('Rs. 90,000.00');
    expect(departments.tBodies[0].rows[0].textContent).toContain('75.0%');

    // A zero is a zero-length bar beside a printed zero.
    const kids = departments.tBodies[0].rows[2];
    expect(kids.textContent).toContain('Rs. 0.00');
    expect((kids.querySelector('.bar-table__bar') as HTMLElement).style.width).toBe('0%');

    expect(byTestId('dashboard-products')!.textContent).toContain('New');
    expect(byTestId('dashboard-statuses')!.textContent).toContain('Awaiting payment');
    expect(byTestId('dashboard-methods')!.textContent).toContain('PayPal');
  });

  it('labels buyers by district as delivery districts, distinct from the customer list', async () => {
    await open('all');

    expect(byTestId('dashboard-buyers')!.textContent).toContain('2 buyers');
    expect(byTestId('dashboard-buyers')!.textContent).toContain('delivered');
    expect(byTestId('dashboard-buyers')!.textContent).toContain('not the default district on a customer account');
    expect(byTestId('dashboard-districts')!.textContent).toContain('Kandy');
  });

  it('fetches nothing for a role without Dashboard.View and says why', async () => {
    const request = await open(['Customers.View']);

    expect(request).toBeNull();
    expect(byTestId('dashboard-forbidden')).not.toBeNull();
    expect(byTestId('kpis')).toBeNull();
    expect(byTestId('dashboard-reports-link')).toBeNull();
  });

  it('shows recent orders only to a role with Orders.View, and the Reports link only with Reports.View', async () => {
    await open(['Dashboard.View']);

    expect(element.textContent).not.toContain('Recent orders');
    expect(byTestId('dashboard-reports-link')).toBeNull();
    expect(byTestId('kpis')).not.toBeNull();
  });

  it('asks the order list for the ten newest when Orders.View is held', async () => {
    fixture = TestBed.createComponent(DashboardPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne(WHOAMI).flush({ grantsAllPermissions: false, permissions: ['Dashboard.View', 'Orders.View', 'Reports.View'] });
    await settle();

    const orders = http.expectOne((candidate) => candidate.url === '/api/admin/orders');
    expect(orders.request.params.get('pageSize')).toBe('10');
    expect(orders.request.params.get('sort')).toBe('Newest');
    orders.flush({ rows: [], page: 1, pageSize: 10, totalCount: 0, sort: 'Newest', statusCounts: {} });

    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard').flush(dashboard());
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend').flush(TREND);
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/low-stock').flush(LOW);
    await settle();

    expect(byTestId('recent-empty')!.textContent).toContain('No orders have been placed yet.');
    expect(byTestId('dashboard-reports-link')).not.toBeNull();
  });

  it('refuses a custom period that ends before it starts, without asking the server', async () => {
    await open('all');

    const preset = byTestId<HTMLSelectElement>('dashboard-preset')!;
    preset.value = 'custom';
    preset.dispatchEvent(new Event('change'));
    await settle();

    const from = byTestId<HTMLInputElement>('dashboard-from')!;
    from.value = '2026-10-10';
    from.dispatchEvent(new Event('change'));
    const to = byTestId<HTMLInputElement>('dashboard-to')!;
    to.value = '2026-10-01';
    to.dispatchEvent(new Event('change'));

    byTestId<HTMLButtonElement>('dashboard-apply')!.click();
    await settle();

    http.expectNone((candidate) => candidate.url === '/api/admin/dashboard');
    expect(byTestId('dashboard-period-error')!.textContent).toContain('The end date is before the start date.');
  });

  it('sends a preset as Colombo days and reloads the trend to its last day', async () => {
    await open('all');

    const preset = byTestId<HTMLSelectElement>('dashboard-preset')!;
    preset.value = 'lastMonth';
    preset.dispatchEvent(new Event('change'));
    await settle();

    const request = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard');
    expect(request.request.params.get('from')).toBe('2026-09-01');
    expect(request.request.params.get('to')).toBe('2026-09-30');
    request.flush(dashboard());

    const trend = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');
    expect(trend.request.params.get('to')).toBe('2026-09-30');
    trend.flush(TREND);
    await settle();
  });

  it('signs out with aria-disabled, never [disabled], so focus is not thrown away', async () => {
    await open('all');
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const button = byTestId<HTMLButtonElement>('dashboard-logout')!;
    button.focus();
    button.click();
    await settle();

    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(button);

    // A second press while signing out sends nothing more: one token request, then one sign-out.
    button.click();
    await settle();
    http.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 't' });
    http.expectOne('/api/admin/auth/logout').flush(null);
  });
});
