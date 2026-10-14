import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { SalesTrend } from '../../core/reporting/reporting.model';
import { SalesTrendChart } from './sales-trend';

function trend(interval: 'Week' | 'Month', revenue: number[]): SalesTrend {
  return {
    interval,
    window: { from: '2026-08-01', to: '2026-10-15', days: 76 },
    currency: 'LKR',
    points: revenue.map((grossRevenue, index) => ({
      start: ['2026-08-01', '2026-09-01', '2026-10-01'][index],
      end: ['2026-08-31', '2026-09-30', '2026-10-15'][index],
      grossRevenue,
      paidOrders: grossRevenue > 0 ? 1 : 0,
      orders: index + 1,
    })),
  };
}

describe('SalesTrendChart (ADM-04)', () => {
  let fixture: ComponentFixture<SalesTrendChart>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function byTestId<T extends Element = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(SalesTrendChart);
    fixture.componentRef.setInput('to', '2026-10-15');
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);
    await settle();
  });

  afterEach(() => {
    http.verify();
    element.remove();
  });

  it('draws the chart and a table of the same points, with a one-sentence summary', async () => {
    const request = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');
    expect(request.request.params.get('interval')).toBe('Month');
    expect(request.request.params.get('to')).toBe('2026-10-15');
    request.flush(trend('Month', [0, 3000, 1500]));
    await settle();

    const chart = byTestId<SVGElement>('trend-chart')!;
    expect(chart.getAttribute('role')).toBe('img');
    expect(chart.getAttribute('aria-label')).toMatch(/^Gross revenue by month, .* Highest: Sept? 2026, Rs\. 3,000\.00\.$/);

    // A zero month is a zero-height bar; the tallest is the full height.
    const heights = [...chart.querySelectorAll('rect')].map((bar) => Number(bar.getAttribute('height')));
    expect(heights).toEqual([0, 100, 50]);

    const rows = byTestId<HTMLTableElement>('trend-table')!.tBodies[0].rows;
    expect(rows).toHaveLength(3);
    expect(rows[1].textContent).toContain('Rs. 3,000.00');
  });

  it('asks for weeks when Weekly is pressed, and shows orders from the same points when Orders is', async () => {
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend').flush(trend('Month', [0, 3000, 1500]));
    await settle();

    byTestId<HTMLButtonElement>('trend-orders')!.click();
    await settle();
    http.expectNone((candidate) => candidate.url === '/api/admin/dashboard/trend');
    expect(byTestId('trend-chart')!.getAttribute('aria-label')).toContain('Orders placed by month');
    expect([...byTestId('trend-chart')!.querySelectorAll('rect')].map((bar) => Math.round(Number(bar.getAttribute('height'))))).toEqual([
      33,
      67,
      100,
    ]);

    byTestId<HTMLButtonElement>('trend-week')!.click();
    await settle();
    expect(byTestId('trend-week')!.getAttribute('aria-pressed')).toBe('true');
    const weekly = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');
    expect(weekly.request.params.get('interval')).toBe('Week');
    weekly.flush(trend('Week', [0, 0, 0]));
    await settle();

    // Back to revenue, which is zero in every week: said in words, not drawn as an empty chart.
    byTestId<HTMLButtonElement>('trend-revenue')!.click();
    await settle();

    expect(byTestId('trend-chart')!.getAttribute('aria-label')).toContain('none in any week');
  });

  it('keeps only the latest answer when the interval changes before the first one arrives', async () => {
    const monthly = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');

    byTestId<HTMLButtonElement>('trend-week')!.click();
    await settle();
    const weekly = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');

    weekly.flush(trend('Week', [1, 2, 3]));
    monthly.flush(trend('Month', [9, 9, 9]));
    await settle();

    expect(byTestId('trend-window')!.textContent).toContain('by week');
  });
});
