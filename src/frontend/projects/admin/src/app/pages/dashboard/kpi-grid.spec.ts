import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ComparedFigure, DashboardKpis } from '../../core/reporting/reporting.model';
import { KpiGrid } from './kpi-grid';

/**
 * W4-02 tester: **each card shows its own figure.** Every value below is different from every other, so a
 * card wired to the wrong KPI — net revenue showing gross, refunds showing net — cannot pass. Before this
 * spec only the gross card's value was asserted anywhere, and those two swaps survived every Vitest and
 * Playwright test.
 */
function figure(current: number | null, previous: number | null): ComparedFigure {
  const change = current === null || previous === null ? null : current - previous;
  const percentChange = change === null || previous === 0 || previous === null ? null : Math.round((change / previous) * 1000) / 10;

  return { current, previous, change, percentChange };
}

const KPIS: DashboardKpis = {
  grossRevenue: figure(91_000, 70_000),
  refundsCompleted: figure(2_400, 0),
  netRevenue: figure(88_600, 70_000),
  orders: figure(17, 20),
  paidOrders: figure(13, 11),
  averageOrderValue: figure(7_000, 6_363.64),
};

describe('KpiGrid (ADM-04): each card shows its own figure', () => {
  let fixture: ComponentFixture<KpiGrid>;
  let element: HTMLElement;

  beforeEach(async () => {
    fixture = TestBed.createComponent(KpiGrid);
    fixture.componentRef.setInput('kpis', KPIS);
    fixture.componentRef.setInput('currency', 'LKR');
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await fixture.whenStable();
  });

  function card(key: string): { value: string; previous: string; label: string } {
    const root = element.querySelector(`[data-testid="kpi-${key}"]`)!;

    return {
      label: root.querySelector('.kpi__label')!.textContent!.trim(),
      value: root.querySelector('.kpi__value')!.textContent!.trim(),
      previous: root.querySelector('.kpi__previous')!.textContent!.trim(),
    };
  }

  it.each([
    ['gross', 'Gross revenue', 'Rs. 91,000.00', 'Previous period: Rs. 70,000.00'],
    ['net', 'Net revenue', 'Rs. 88,600.00', 'Previous period: Rs. 70,000.00'],
    ['refunds', 'Refunds completed', 'Rs. 2,400.00', 'Previous period: Rs. 0.00'],
    ['orders', 'Orders', '17', 'Previous period: 20'],
    ['paid', 'Paid orders', '13', 'Previous period: 11'],
    ['aov', 'Average order value', 'Rs. 7,000.00', 'Previous period: Rs. 6,363.64'],
  ])('the %s card is labelled %s and shows %s', (key, label, value, previous) => {
    expect(card(key)).toEqual({ label, value, previous });
  });

  it('shows six cards and no conversion rate', () => {
    expect(element.querySelectorAll('.kpi')).toHaveLength(6);
    expect(element.textContent).not.toMatch(/conversion/i);
  });
});
