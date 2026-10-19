import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { By } from '@angular/platform-browser';
import { BaseChartDirective } from 'ng2-charts';

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

  /** What the chart was given to draw: the datasets Chart.js receives. */
  function drawn(): { label: string; data: number[]; yAxisID: string }[] {
    const chart = fixture.debugElement.query(By.directive(BaseChartDirective)).injector.get(BaseChartDirective);
    return (chart.data!.datasets as { label: string; data: number[]; yAxisID: string }[]).map(({ label, data, yAxisID }) => ({ label, data, yAxisID }));
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

    const chart = byTestId('trend-chart')!;
    expect(chart.tagName).toBe('CANVAS');
    expect(chart.getAttribute('role')).toBe('img');
    expect(chart.getAttribute('aria-label')).toMatch(/^Gross revenue and paid orders by month, .* Highest revenue: Sept? 2026, Rs\. 3,000\.00; 2 paid orders in all\.$/);

    // #186: revenue on the left axis and paid orders on the right, each the API's own figure for each month.
    expect(drawn()).toEqual([
      { label: 'Gross revenue (Rs.)', data: [0, 3000, 1500], yAxisID: 'y' },
      { label: 'Paid orders', data: [0, 1, 1], yAxisID: 'y1' },
    ]);
    // The series names are text beside the canvas, not drawn on it.
    expect(byTestId('trend-chart-keys')!.textContent).toMatch(/Gross revenue \(Rs\.\)\s*Paid orders/);

    const rows = byTestId<HTMLTableElement>('trend-table')!.tBodies[0].rows;
    expect(rows).toHaveLength(3);
    expect(rows[1].textContent).toContain('Rs. 3,000.00');
  });

  it('asks for weeks when Weekly is pressed, and says so in words when there were no sales', async () => {
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend').flush(trend('Month', [0, 3000, 1500]));
    await settle();

    byTestId<HTMLButtonElement>('trend-week')!.click();
    await settle();
    expect(byTestId('trend-week')!.getAttribute('aria-pressed')).toBe('true');
    expect(byTestId('trend-month')!.getAttribute('aria-pressed')).toBe('false');
    const weekly = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');
    expect(weekly.request.params.get('interval')).toBe('Week');
    weekly.flush(trend('Week', [0, 0, 0]));
    await settle();

    // Zero in every week: said in words, not drawn as a flat line — and the table is still there.
    expect(byTestId('trend-chart')).toBeNull();
    expect(byTestId('trend-chart-empty')!.textContent).toContain('No sales in');
    expect(byTestId<HTMLTableElement>('trend-table')!.tBodies[0].rows).toHaveLength(3);
  });

  it('puts each figure in its own column of the table (W4-02 tester)', async () => {
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend').flush(trend('Month', [0, 3000, 1500]));
    await settle();

    // September: Rs. 3,000.00 gross, 1 paid order, 2 orders placed — three different numbers.
    const cells = [...byTestId<HTMLTableElement>('trend-table')!.tBodies[0].rows[1].cells].map((cell) => cell.textContent!.trim());
    expect(cells.slice(1)).toEqual(['Rs. 3,000.00', '1', '2']);
  });

  it('keeps only the latest answer when the interval changes before the first one arrives', async () => {
    const monthly = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');

    byTestId<HTMLButtonElement>('trend-week')!.click();
    await settle();
    const weekly = http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend');

    weekly.flush(trend('Week', [1, 2, 3]));
    monthly.flush(trend('Month', [9, 9, 9]));
    await settle();

    expect(byTestId('trend-window')!.textContent).toContain('Weekly');
    expect(drawn()[0].data).toEqual([1, 2, 3]);
  });

  it('heads the first column Month or Week starting, as the interval is (#186 tester)', async () => {
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend').flush(trend('Month', [0, 3000, 1500]));
    await settle();
    expect(byTestId<HTMLTableElement>('trend-table')!.tHead!.rows[0].cells[0].textContent!.trim()).toBe('Month');

    byTestId<HTMLButtonElement>('trend-week')!.click();
    await settle();
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend').flush(trend('Week', [1, 2, 3]));
    await settle();
    expect(byTestId<HTMLTableElement>('trend-table')!.tHead!.rows[0].cells[0].textContent!.trim()).toBe('Week starting');
  });

  it('names no highest month in the table caption when every month is zero (#186 tester)', async () => {
    http.expectOne((candidate) => candidate.url === '/api/admin/dashboard/trend').flush(trend('Month', [0, 0, 0]));
    await settle();

    const caption = byTestId<HTMLTableElement>('trend-table')!.caption!.textContent!;
    expect(caption).toContain('none in any month');
    expect(caption).not.toContain('Highest');
  });
});
