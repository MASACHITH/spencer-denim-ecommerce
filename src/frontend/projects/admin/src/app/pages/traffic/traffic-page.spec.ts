import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { BaseChartDirective } from 'ng2-charts';

import { TrafficReport } from '../../core/traffic/traffic.model';
import { TrafficPage } from './traffic-page';

const WHOAMI = '/api/admin/auth/whoami';
const TRAFFIC = '/api/admin/traffic';

/** 15 October 2026, 18:31 UTC: already 16 October in Colombo. */
const NOW = Date.parse('2026-10-15T18:31:00Z');

function report(overrides: Partial<TrafficReport> = {}): TrafficReport {
  return {
    period: { from: '2026-10-14', to: '2026-10-16', days: 3 },
    previousPeriod: { from: '2026-10-11', to: '2026-10-13', days: 3 },
    liveFrom: '2026-10-13',
    visits: { current: 12, previous: 8, change: 4, percentChange: 50 },
    pageViews: { current: 40, previous: 40, change: 0, percentChange: 0 },
    botPageViews: 9,
    days: [
      { date: '2026-10-14', visits: 5, pageViews: 20, botPageViews: 3 },
      { date: '2026-10-15', visits: 7, pageViews: 20, botPageViews: 6 },
      { date: '2026-10-16', visits: 0, pageViews: 0, botPageViews: 0 },
    ],
    channels: [
      { channel: 'Direct', label: 'Direct', visits: 6, pageViews: 20, sharePercent: 50 },
      { channel: 'Organic', label: 'Organic', visits: 6, pageViews: 20, sharePercent: 50 },
      { channel: 'Social', label: 'Social', visits: 0, pageViews: 0, sharePercent: 0 },
      { channel: 'Referral', label: 'Referral', visits: 0, pageViews: 0, sharePercent: 0 },
      { channel: 'Email', label: 'Email', visits: 0, pageViews: 0, sharePercent: 0 },
      { channel: 'Paid', label: 'Paid', visits: 0, pageViews: 0, sharePercent: 0 },
      { channel: 'Other', label: 'Other', visits: 0, pageViews: 0, sharePercent: 0 },
    ],
    devices: [
      { device: 'Desktop', label: 'Desktop', visits: 4, pageViews: 10, sharePercent: 33.3 },
      { device: 'Mobile', label: 'Mobile', visits: 8, pageViews: 30, sharePercent: 66.7 },
      { device: 'Tablet', label: 'Tablet', visits: 0, pageViews: 0, sharePercent: 0 },
      { device: 'Other', label: 'Other', visits: 0, pageViews: 0, sharePercent: 0 },
    ],
    sources: [
      { source: 'www.google.com', visits: 6, pageViews: 20 },
      { source: '(direct)', visits: 6, pageViews: 20 },
    ],
    topPages: [
      { path: '/product/linen-shirt', pageType: 'Product', productName: 'Linen shirt', pageViews: 15, visits: 9 },
      { path: '/order/*', pageType: 'Order', productName: null, pageViews: 2, visits: 2 },
    ],
    ...overrides,
  };
}

const EMPTY = report({
  visits: { current: 0, previous: 0, change: 0, percentChange: null },
  pageViews: { current: 0, previous: 0, change: 0, percentChange: null },
  botPageViews: 0,
  days: [{ date: '2026-10-16', visits: 0, pageViews: 0, botPageViews: 0 }],
  sources: [],
  topPages: [],
});

describe('TrafficPage (ADM-20)', () => {
  let fixture: ComponentFixture<TrafficPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  async function open(permissions: string[] | 'all', payload: TrafficReport | null = report()): Promise<void> {
    fixture = TestBed.createComponent(TrafficPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);
    await settle();

    http.expectOne(WHOAMI).flush({ grantsAllPermissions: permissions === 'all', permissions: permissions === 'all' ? [] : permissions });
    await settle();

    if (payload) {
      http.expectOne((candidate) => candidate.url === TRAFFIC).flush(payload);
      await settle();
    }
  }

  beforeEach(() => {
    vi.spyOn(Date, 'now').mockReturnValue(NOW);
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    element?.remove();
    vi.restoreAllMocks();
  });

  it('asks for the last 30 Colombo days by default', async () => {
    fixture = TestBed.createComponent(TrafficPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne(WHOAMI).flush({ grantsAllPermissions: true, permissions: [] });
    await settle();

    const request = http.expectOne((candidate) => candidate.url === TRAFFIC);
    expect(request.request.params.get('from')).toBe('2026-09-17');
    expect(request.request.params.get('to')).toBe('2026-10-16');
    request.flush(report());
    await settle();
  });

  it('tells a role without Traffic.View so, and asks the API for nothing', async () => {
    await open(['Dashboard.View', 'Orders.View'], null);

    expect(byTestId('traffic-forbidden')).not.toBeNull();
    expect(http.match((candidate) => candidate.url === TRAFFIC)).toHaveLength(0);
  });

  it('shows visits, page views and crawlers apart, against the previous period, and says which days are live', async () => {
    await open(['Traffic.View']);

    expect(byTestId('traffic-visits')!.textContent).toContain('12');
    expect(byTestId('traffic-visits')!.textContent).toContain('+50.0% on the previous period');
    expect(byTestId('traffic-page-views')!.textContent).toContain('No change from the previous period');
    expect(byTestId('traffic-bots')!.textContent).toContain('9');
    expect(byTestId('traffic-definitions')!.textContent).toMatch(/Days from 13 Oct 2026 are counted live/);
    expect(byTestId('traffic-definitions')!.textContent).toContain('No IP address and no visitor ID are kept');
  });

  it('gives every breakdown as a table with its figures', async () => {
    await open('all');

    const tables = ['traffic-days', 'traffic-channels', 'traffic-devices', 'traffic-top-sources', 'traffic-top-pages'];
    for (const id of tables) {
      const table = byTestId<HTMLTableElement>(id)!;
      expect(table.tagName).toBe('TABLE');
      expect(table.querySelector('caption')!.textContent!.length).toBeGreaterThan(0);
    }

    expect(byTestId<HTMLTableElement>('traffic-days')!.tBodies[0].rows).toHaveLength(3);
    expect(byTestId<HTMLTableElement>('traffic-channels')!.tBodies[0].rows).toHaveLength(7);
    expect(byTestId<HTMLTableElement>('traffic-channels')!.tBodies[0].rows[1].textContent).toContain('search engine');
    expect(byTestId<HTMLTableElement>('traffic-devices')!.tBodies[0].rows[1].textContent).toContain('66.7%');

    const pages = byTestId<HTMLTableElement>('traffic-top-pages')!.tBodies[0].rows;
    expect(pages[0].textContent).toContain('Linen shirt (/product/linen-shirt)');
    expect(pages[0].textContent).toContain('Product');
    expect(pages[1].textContent).toContain('Order confirmation');
    expect(pages[1].textContent).toContain('/order/*');
  });

  it('draws visits and page views by day, and channels and devices as doughnuts, from the same figures (#186)', async () => {
    await open('all');

    const byId = (id: string) => fixture.debugElement.query(By.css(`canvas[data-testid="${id}"]`)).injector.get(BaseChartDirective);

    expect(byId('traffic-days-chart').data!.datasets.map((set) => set.data)).toEqual([[5, 7, 0], [20, 20, 0]]);
    expect(byId('traffic-channels-chart').data!.datasets[0].data).toEqual([6, 6, 0, 0, 0, 0, 0]);
    expect(byId('traffic-devices-chart').data!.datasets[0].data).toEqual([4, 8, 0, 0]);
    expect(byTestId('traffic-days-chart')!.getAttribute('aria-label')).toMatch(/Busiest: 15 Oct 2026, 7 visits and 20 page views\./);
    // Shares in the legend are the API's own.
    expect(byTestId('traffic-devices-chart-legend')!.textContent).toContain('66.7%');
  });

  it('says an empty period is empty instead of drawing zeros', async () => {
    await open('all', EMPTY);

    expect(byTestId('traffic-empty')!.textContent).toContain('No visits were recorded in these days.');
    expect(byTestId('traffic-channels')).toBeNull();
    expect(byTestId('traffic-top-pages')).toBeNull();
    expect(byTestId('traffic-status')!.textContent).toContain('No visits were recorded');
  });

  it('sends a preset as Colombo days', async () => {
    await open('all');

    const preset = byTestId<HTMLSelectElement>('traffic-preset')!;
    preset.value = 'last7';
    preset.dispatchEvent(new Event('change'));
    await settle();

    const request = http.expectOne((candidate) => candidate.url === TRAFFIC);
    expect(request.request.params.get('from')).toBe('2026-10-10');
    expect(request.request.params.get('to')).toBe('2026-10-16');
    request.flush(report());
    await settle();
  });

  it('refuses a custom period longer than 366 days before asking', async () => {
    await open('all');

    const preset = byTestId<HTMLSelectElement>('traffic-preset')!;
    preset.value = 'custom';
    preset.dispatchEvent(new Event('change'));
    await settle();

    const from = byTestId<HTMLInputElement>('traffic-from')!;
    from.value = '2025-01-01';
    from.dispatchEvent(new Event('change'));
    const to = byTestId<HTMLInputElement>('traffic-to')!;
    to.value = '2026-10-16';
    to.dispatchEvent(new Event('change'));
    byTestId<HTMLButtonElement>('traffic-apply')!.click();
    await settle();

    expect(byTestId('traffic-period-error')!.textContent).toContain('at most 366 days');
    expect(http.match((candidate) => candidate.url === TRAFFIC)).toHaveLength(0);
  });

  it('keeps only the latest answer when periods change quickly', async () => {
    await open('all');

    const preset = byTestId<HTMLSelectElement>('traffic-preset')!;
    preset.value = 'last7';
    preset.dispatchEvent(new Event('change'));
    preset.value = 'thisMonth';
    preset.dispatchEvent(new Event('change'));
    await settle();

    const [stale, latest] = http.match((candidate) => candidate.url === TRAFFIC);
    latest.flush(report({ botPageViews: 2 }));
    stale.flush(report({ botPageViews: 777 }));
    await settle();

    expect(byTestId('traffic-bots')!.textContent).toContain('2');
    expect(byTestId('traffic-bots')!.textContent).not.toContain('777');
  });

  it('offers a retry when the API fails', async () => {
    await open('all', null);
    http.expectOne((candidate) => candidate.url === TRAFFIC).flush(null, { status: 500, statusText: 'Server Error' });
    await settle();

    expect(byTestId('traffic-problem')).not.toBeNull();
    (byTestId('traffic-problem')!.querySelector('button') as HTMLButtonElement).click();
    await settle();

    http.expectOne((candidate) => candidate.url === TRAFFIC).flush(report());
    await settle();
    expect(byTestId('traffic-problem')).toBeNull();
  });
});
