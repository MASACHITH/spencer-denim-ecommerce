import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ComparedFigure, DashboardKpis } from '../../core/reporting/reporting.model';
import { AlertStrip } from './alert-strip';
import { KpiGrid, tone } from './kpi-grid';
import { TodayBanner } from './today-banner';

/** #186: the demo's widgets that are new on this dashboard — KPI colours, the alert strip, the Today banner. */

const NOW = Date.parse('2026-10-15T18:31:00Z'); // the 16th in Colombo
const TODAY = '2026-10-16';

function figure(current: number, previous: number): ComparedFigure {
  const change = current - previous;
  return { current, previous, change, percentChange: previous === 0 ? null : Math.round((change / previous) * 1000) / 10 };
}

describe('KPI tone (#186): green for good news, red for bad, reversed when a rise is bad', () => {
  it('calls a rise good and a fall bad for an ordinary figure', () => {
    expect(tone(figure(10, 5))).toBe('good');
    expect(tone(figure(5, 10))).toBe('bad');
    expect(tone(figure(5, 5))).toBe('flat');
  });

  it('reverses both for a figure where a rise is bad', () => {
    expect(tone(figure(10, 5), true)).toBe('bad');
    expect(tone(figure(5, 10), true)).toBe('good');
  });

  it('colours rising refunds as bad news and rising revenue as good on the cards themselves', async () => {
    const kpis: DashboardKpis = {
      grossRevenue: figure(200, 100),
      refundsCompleted: figure(50, 10),
      netRevenue: figure(150, 90),
      orders: figure(3, 6),
      paidOrders: figure(2, 2),
      averageOrderValue: figure(100, 50),
    };
    const fixture = TestBed.createComponent(KpiGrid);
    fixture.componentRef.setInput('kpis', kpis);
    fixture.componentRef.setInput('currency', 'LKR');
    fixture.detectChanges();
    await fixture.whenStable();

    const toneOf = (key: string) => (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="kpi-${key}"] .kpi__change`)!.getAttribute('data-tone');
    expect([toneOf('gross'), toneOf('refunds'), toneOf('orders'), toneOf('paid')]).toEqual(['good', 'bad', 'bad', 'flat']);
    // The arrow still says which way it went: refunds went up.
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="kpi-refunds"] .kpi__change')!.textContent).toContain('▲');
  });
});

describe('AlertStrip (#186)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(orders: boolean, reports: boolean, counts: Record<string, number>, low: number) {
    const fixture = TestBed.createComponent(AlertStrip);
    fixture.componentRef.setInput('canSeeOrders', orders);
    fixture.componentRef.setInput('canSeeReports', reports);
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne('/api/admin/dashboard/low-stock').flush({ defaultThreshold: 5, totalCount: low, rows: [] });
    if (orders) {
      const list = http.expectOne((request) => request.url === '/api/admin/orders');
      expect(list.request.params.get('pageSize')).toBe('1');
      list.flush({ rows: [], page: 1, pageSize: 1, totalCount: 0, sort: 'Newest', statusCounts: counts });
    }

    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('counts paid orders not yet dispatched as Paid plus Processing over the whole table, and the live low-stock total', async () => {
    const element = await render(true, true, { Paid: 2, Processing: 3, Shipped: 40, PendingPayment: 7 }, 6);

    expect(element.querySelector('[data-testid="alert-strip-dispatch"]')!.textContent).toMatch(/5\s*paid orders not yet dispatched/);
    expect(element.querySelector('[data-testid="alert-strip-dispatch"] a')!.getAttribute('href')).toBe('/orders');
    expect(element.querySelector('[data-testid="alert-strip-low-stock"]')!.textContent).toMatch(/6\s*variants low on stock/);
    expect(element.querySelector('[data-testid="alert-strip-low-stock"] a')!.getAttribute('href')).toBe('/reports?report=inventory&status=LowStock');
  });

  it('does not ask for orders without Orders.View, and does not link a role without reports to them', async () => {
    const element = await render(false, false, {}, 1);

    expect(element.querySelector('[data-testid="alert-strip-dispatch"]')).toBeNull();
    expect(element.querySelector('[data-testid="alert-strip-low-stock"]')!.textContent).toMatch(/1\s*variant low on stock/);
    expect(element.querySelector('[data-testid="alert-strip-low-stock"] a')).toBeNull();
  });

  it('is not there at all when nothing is waiting', async () => {
    const element = await render(true, true, { Paid: 0, Shipped: 4 }, 0);

    expect(element.querySelector('[data-testid="alert-strip"]')).toBeNull();
  });
});

describe('TodayBanner (#186)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    vi.spyOn(Date, 'now').mockReturnValue(NOW);
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  async function render(traffic: boolean) {
    const fixture = TestBed.createComponent(TodayBanner);
    fixture.componentRef.setInput('canSeeTraffic', traffic);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it("asks for today's Colombo day only, and shows revenue paid today and orders placed today", async () => {
    const fixture = await render(true);

    const day = http.expectOne((request) => request.url === '/api/admin/dashboard');
    expect([day.request.params.get('from'), day.request.params.get('to')]).toEqual([TODAY, TODAY]);
    const visits = http.expectOne((request) => request.url === '/api/admin/dashboard/traffic-sources');
    expect([visits.request.params.get('from'), visits.request.params.get('to')]).toEqual([TODAY, TODAY]);

    day.flush({ currency: 'LKR', kpis: { grossRevenue: figure(18_400, 0), orders: figure(3, 0) } });
    visits.flush({ visits: 7 });
    fixture.detectChanges();
    await fixture.whenStable();

    const text = (key: string) => (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="today-${key}"]`)!.textContent;
    expect(text('revenue')).toContain('Rs. 18,400.00');
    expect(text('orders')).toContain('3');
    expect(text('visits')).toContain('7');
    expect((fixture.nativeElement as HTMLElement).textContent).not.toMatch(/live|new customers/i);
  });

  it('neither shows nor asks for visits without Traffic.View, and shows a dash when the figures could not be read', async () => {
    const fixture = await render(false);

    http.expectNone((request) => request.url === '/api/admin/dashboard/traffic-sources');
    http.expectOne((request) => request.url === '/api/admin/dashboard').flush('no', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="today-visits"]')).toBeNull();
    expect(element.querySelector('[data-testid="today-revenue"]')!.textContent).toContain('—');
  });
});
