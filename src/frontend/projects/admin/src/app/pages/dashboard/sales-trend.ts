import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { money } from '../../core/orders/order-formatting';
import { ReportingApi } from '../../core/reporting/reporting-api';
import { bucketLabel, count, formatPeriod } from '../../core/reporting/reporting-format';
import { SalesTrend, SalesTrendPoint, TrendInterval } from '../../core/reporting/reporting.model';

type Measure = 'revenue' | 'orders';

/** Width of one point in the chart's own coordinates; the SVG scales to its container. */
const STEP = 12;
const BAR = 8;
const HEIGHT = 100;

/**
 * The Revenue &amp; Orders trend (ADM-04): twelve months, monthly or by ISO week, ending on the period's last
 * day. It fetches for itself, so the dashboard only says which day it ends on.
 *
 * **The chart has a table beside it, not instead of it.** The SVG is `role="img"` with a one-sentence summary
 * (the window, the highest point and its value); the full figures are a real table one tap away, under
 * "Show the figures as a table". Both are drawn from the same points.
 *
 * **Revenue and orders are separate views of the same points**, not two scales on one chart: a line of
 * order counts over bars of rupees invites reading one against the other's axis.
 */
@Component({
  selector: 'sda-sales-trend',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="trend__controls">
      <div class="trend__group" role="group" aria-label="Group by">
        <button class="adm-btn" type="button" data-testid="trend-month" [attr.aria-pressed]="interval() === 'Month'" (click)="setInterval('Month')">Monthly</button>
        <button class="adm-btn" type="button" data-testid="trend-week" [attr.aria-pressed]="interval() === 'Week'" (click)="setInterval('Week')">Weekly</button>
      </div>
      <div class="trend__group" role="group" aria-label="Show">
        <button class="adm-btn" type="button" data-testid="trend-revenue" [attr.aria-pressed]="measure() === 'revenue'" (click)="measure.set('revenue')">Revenue</button>
        <button class="adm-btn" type="button" data-testid="trend-orders" [attr.aria-pressed]="measure() === 'orders'" (click)="measure.set('orders')">Orders</button>
      </div>
    </div>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="trend-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind !== 'forbidden') {
          <div class="adm-actions"><button class="adm-btn" type="button" (click)="load()">Try again</button></div>
        }
      </div>
    } @else if (trend(); as answered) {
      <p class="adm-panel__hint" data-testid="trend-window">{{ windowText() }}</p>
      <svg
        class="trend__chart"
        role="img"
        data-testid="trend-chart"
        [attr.aria-label]="summary()"
        [attr.viewBox]="'0 0 ' + answered.points.length * step + ' ' + (height + 2)"
        preserveAspectRatio="none"
      >
        <line x1="0" [attr.x2]="answered.points.length * step" [attr.y1]="height" [attr.y2]="height" class="trend__axis" />
        @for (bar of bars(); track bar.key) {
          <rect class="trend__bar" [attr.x]="bar.x" [attr.y]="bar.y" [attr.width]="barWidth" [attr.height]="bar.h" />
        }
      </svg>
      <div class="trend__ends" aria-hidden="true">
        <span>{{ label(answered.points[0]) }}</span>
        <span>{{ label(answered.points[answered.points.length - 1]) }}</span>
      </div>

      <details class="trend__table">
        <summary class="adm-link">Show the figures as a table</summary>
        <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Revenue and orders by period">
          <table class="adm-table" data-testid="trend-table">
            <caption class="sr-only">{{ summary() }}</caption>
            <thead>
              <tr>
                <th scope="col">{{ answered.interval === 'Month' ? 'Month' : 'Week starting' }}</th>
                <th scope="col" class="adm-num">Gross revenue</th>
                <th scope="col" class="adm-num">Paid orders</th>
                <th scope="col" class="adm-num">Orders placed</th>
              </tr>
            </thead>
            <tbody>
              @for (point of answered.points; track point.start) {
                <tr>
                  <th scope="row">{{ label(point) }}</th>
                  <td class="adm-num">{{ rupees(point.grossRevenue) }}</td>
                  <td class="adm-num">{{ number(point.paidOrders) }}</td>
                  <td class="adm-num">{{ number(point.orders) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </details>
    } @else {
      <p class="adm-muted">Loading the trend…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-1); min-width: 0; }
    .trend__controls { display: flex; flex-wrap: wrap; gap: var(--space-2); }
    .trend__group { display: flex; }
    .trend__group .adm-btn[aria-pressed='true'] { color: var(--color-on-primary); background: var(--color-primary); }
    .trend__chart { display: block; width: 100%; height: 160px; }
    .trend__bar { fill: var(--color-primary); }
    .trend__axis { stroke: var(--color-border-strong); stroke-width: 1; vector-effect: non-scaling-stroke; }
    .trend__ends { display: flex; justify-content: space-between; font-size: var(--text-small); color: var(--color-text-muted); }
  `,
})
export class SalesTrendChart {
  /** The last day the trend covers, `yyyy-MM-dd` (Colombo): the end of the dashboard's period. */
  readonly to = input.required<string>();

  private readonly api = inject(ReportingApi);

  protected readonly step = STEP;
  protected readonly barWidth = BAR;
  protected readonly height = HEIGHT;

  protected readonly interval = signal<TrendInterval>('Month');
  protected readonly measure = signal<Measure>('revenue');
  protected readonly trend = signal<SalesTrend | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  protected readonly bars = computed(() => {
    const points = this.trend()?.points ?? [];
    const values = points.map((point) => this.valueOf(point));
    const largest = Math.max(0, ...values);

    return points.map((point, index) => {
      const h = largest <= 0 ? 0 : (values[index] / largest) * HEIGHT;
      return { key: point.start, x: index * STEP + (STEP - BAR) / 2, y: HEIGHT - h, h };
    });
  });

  protected readonly windowText = computed(() => {
    const answered = this.trend();
    return answered ? `${formatPeriod(answered.window)}, ${answered.interval === 'Month' ? 'by month' : 'by week (Monday to Sunday)'}.` : '';
  });

  /** One sentence: what is drawn, over which days, and its highest point. */
  protected readonly summary = computed(() => {
    const answered = this.trend();

    if (!answered || answered.points.length === 0) {
      return '';
    }

    const what = this.measure() === 'revenue' ? 'Gross revenue' : 'Orders placed';
    const by = answered.interval === 'Month' ? 'month' : 'week';
    const highest = answered.points.reduce((best, point) => (this.valueOf(point) > this.valueOf(best) ? point : best));
    const top = this.valueOf(highest);

    return top <= 0
      ? `${what} by ${by}, ${formatPeriod(answered.window)}: none in any ${by}.`
      : `${what} by ${by}, ${formatPeriod(answered.window)}. Highest: ${this.label(highest)}, ${this.measure() === 'revenue' ? this.rupees(top) : this.number(top)}.`;
  });

  constructor() {
    effect(() => {
      this.to();
      this.interval();
      untracked(() => this.load());
    });
  }

  protected setInterval(interval: TrendInterval): void {
    this.interval.set(interval);
  }

  protected label(point: SalesTrendPoint): string {
    return bucketLabel(point.start, this.trend()?.interval ?? 'Month');
  }

  protected rupees(amount: number): string {
    return money(amount, this.trend()?.currency ?? 'LKR');
  }

  protected number(value: number): string {
    return count(value);
  }

  load(): void {
    // Only the latest request may answer: a slow monthly reply must not overwrite a weekly one asked later.
    const request = ++this.requests;

    this.problem.set(null);
    this.trend.set(null);

    this.api.trend(this.interval(), this.to()).subscribe({
      next: (answered) => {
        if (request === this.requests) {
          this.trend.set(answered);
        }
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          this.problem.set(toCatalogFailure(error));
        }
      },
    });
  }

  private requests = 0;

  private valueOf(point: SalesTrendPoint): number {
    return this.measure() === 'revenue' ? point.grossRevenue : point.orders;
  }
}
