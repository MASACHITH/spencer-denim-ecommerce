import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { BaseChartDirective } from 'ng2-charts';

import { ChartView } from './chart';
import { DoughnutSpec } from './chart-config';

/** #186: the one chart wrapper — text legend, accessible name, empty period, and the figures table. */
describe('ChartView (#186)', () => {
  let fixture: ComponentFixture<ChartView>;
  let element: HTMLElement;

  const spec = (values: number[]): DoughnutSpec => ({
    kind: 'doughnut',
    cutout: 65,
    slices: [
      { key: 'paid', label: 'Paid', value: values[0], display: `${values[0]} orders`, share: 80, token: '--status-paid' },
      { key: 'awaiting', label: 'Awaiting payment', value: values[1], display: `${values[1]} order`, share: 20 },
    ],
  });

  afterEach(() => element?.remove());

  async function render(values: number[]): Promise<void> {
    fixture = TestBed.createComponent(ChartView);
    fixture.componentRef.setInput('spec', spec(values));
    fixture.componentRef.setInput('summary', 'Orders by status. Largest: Paid, 4 orders (80.0%).');
    fixture.componentRef.setInput('testId', 'status');
    fixture.componentRef.setInput('emptyText', 'No orders were placed in these days.');
    fixture.componentRef.setInput('table', {
      caption: 'Orders by status',
      testId: 'status-table',
      headers: ['Status', 'Orders'],
      rows: [
        { key: 'paid', label: 'Paid', cells: [String(values[0])] },
        { key: 'awaiting', label: 'Awaiting payment', cells: [String(values[1])] },
      ],
    });
    element = fixture.nativeElement as HTMLElement;
    // The theme's tokens. On the host rather than :root because jsdom does not inherit custom properties.
    element.style.setProperty('--status-paid', '#2e7d4f');
    element.style.setProperty('--chart-1', '#1f3a5f');
    element.style.setProperty('--chart-2', '#b0773a');
    document.body.appendChild(element);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const byTestId = (id: string) => element.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  it('is an image with a one-sentence name, given the slice values to draw', async () => {
    await render([4, 1]);

    const canvas = byTestId('status')!;
    expect(canvas.tagName).toBe('CANVAS');
    expect(canvas.getAttribute('role')).toBe('img');
    expect(canvas.getAttribute('aria-label')).toBe('Orders by status. Largest: Paid, 4 orders (80.0%).');

    const chart = fixture.debugElement.query(By.directive(BaseChartDirective)).injector.get(BaseChartDirective);
    expect(chart.data!.datasets[0].data).toEqual([4, 1]);
    // Colours from the theme's tokens: the status's own, then the categorical order for its place (second → chart-2).
    expect(chart.data!.datasets[0].backgroundColor).toEqual(['#2e7d4f', '#b0773a']);
  });

  it('has a legend in real text — name, figure and share — with the same colours as the slices', async () => {
    await render([4, 1]);

    const items = [...byTestId('status-legend')!.querySelectorAll('li')];
    expect(items.map((item) => [...item.querySelectorAll('span:not(.chart__dot)')].map((part) => part.textContent!.trim()))).toEqual([
      ['Paid', '4 orders', '80.0%'],
      ['Awaiting payment', '1 order', '20.0%'],
    ]);
    expect((items[0].querySelector('.chart__dot') as HTMLElement).style.background).toBe('rgb(46, 125, 79)');
    // #186 tester: each dot is its own slice's colour, not the first slice's for all (the second is chart-2).
    expect((items[1].querySelector('.chart__dot') as HTMLElement).style.background).toBe('rgb(176, 119, 58)');
  });

  it('says an empty period in words, draws no canvas, and still offers the figures table', async () => {
    await render([0, 0]);

    expect(byTestId('status')).toBeNull();
    expect(byTestId('status-legend')).toBeNull();
    expect(byTestId('status-empty')!.textContent).toContain('No orders were placed in these days.');
    expect(byTestId('status-table')).not.toBeNull();
  });

  it('puts the figures in a table under "Show the figures as a table"', async () => {
    await render([4, 1]);

    const details = element.querySelector('details')!;
    expect(details.querySelector('summary')!.textContent).toBe('Show the figures as a table');
    const table = byTestId('status-table') as HTMLTableElement;
    expect(table.caption!.textContent).toBe('Orders by status');
    expect([...table.tBodies[0].rows].map((row) => row.textContent!.trim())).toEqual(['Paid4', 'Awaiting payment1']);
  });
});
