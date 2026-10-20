import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';

import { ChartTable, ChartView } from '../../core/charts/chart';
import { ChartSpec, compactRupees } from '../../core/charts/chart-config';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { money } from '../../core/orders/order-formatting';
import { ReportingApi } from '../../core/reporting/reporting-api';
import { bucketLabel, count, formatPeriod } from '../../core/reporting/reporting-format';
import { SalesTrend, SalesTrendPoint, TrendInterval } from '../../core/reporting/reporting.model';

/**
 * The Revenue &amp; Orders card (ADM-04): twelve months, monthly or by ISO week, ending on the period's last day.
 * It fetches for itself, so the dashboard only says which day it ends on.
 *
 * **Drawn as the client demo draws it** (#186): gross revenue as a filled, smoothed line on the left axis, and
 * paid orders as a dashed line on a second axis on the right. Both are counted on the day an order was paid, so
 * the two lines describe the same orders; orders *placed* (paid or not) are a different count, and are in the
 * table rather than on a third line. Each axis is labelled with what it measures, and the series names are a
 * text key above the chart, so the two scales cannot be read against each other by mistake.
 *
 * **The chart has a table beside it, not instead of it**: the canvas is `role="img"` with a one-sentence summary,
 * and every figure is a real table under "Show the figures as a table". Both are drawn from the same points.
 */
@Component({
  selector: 'sda-sales-trend',
  imports: [ChartView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="adm-card__hd">
      <div>
        <h2 class="adm-card__title" id="dash-trend">Revenue and orders</h2>
        <p class="adm-card__sub" data-testid="trend-window">{{ windowText() }}</p>
      </div>
      <div class="trend__group" role="group" aria-label="Group by">
        <button class="adm-btn" type="button" data-testid="trend-month" [attr.aria-pressed]="interval() === 'Month'" (click)="setInterval('Month')">Monthly</button>
        <button class="adm-btn" type="button" data-testid="trend-week" [attr.aria-pressed]="interval() === 'Week'" (click)="setInterval('Week')">Weekly</button>
      </div>
    </div>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="trend-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind !== 'forbidden') {
          <div class="adm-actions"><button class="adm-btn" type="button" (click)="load()">Try again</button></div>
        }
      </div>
    } @else if (spec(); as drawn) {
      <sda-chart
        testId="trend-chart"
        [spec]="drawn"
        [summary]="summary()"
        [height]="260"
        [table]="table()"
        [emptyText]="emptyText()"
      />
    } @else {
      <p class="adm-muted">Loading the trend…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-2); min-width: 0; }
    .trend__group { display: flex; }
    .trend__group .adm-btn { padding-inline: var(--space-1-5); }
    .trend__group .adm-btn[aria-pressed='true'] { color: var(--color-on-primary); background: var(--chart-accent); border-color: var(--chart-accent); }
  `,
})
export class SalesTrendChart {
  /** The last day the trend covers, `yyyy-MM-dd` (Colombo): the end of the dashboard's period. */
  readonly to = input.required<string>();

  private readonly api = inject(ReportingApi);

  protected readonly interval = signal<TrendInterval>('Month');
  protected readonly trend = signal<SalesTrend | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  protected readonly windowText = computed(() => {
    const answered = this.trend();
    return answered
      ? `${answered.interval === 'Month' ? 'Monthly' : 'Weekly (Monday to Sunday)'}, ${formatPeriod(answered.window)}`
      : `${this.interval() === 'Month' ? 'Monthly' : 'Weekly'} trend, last twelve months`;
  });

  protected readonly spec = computed<ChartSpec | null>(() => {
    const answered = this.trend();

    if (!answered) {
      return null;
    }

    return {
      kind: 'line',
      labels: answered.points.map((point) => this.label(point)),
      series: [
        {
          key: 'revenue',
          label: 'Gross revenue (Rs.)',
          values: answered.points.map((point) => point.grossRevenue),
          axis: 'left',
          token: '--chart-accent',
          fill: true,
          dashed: false,
          format: (value) => this.rupees(value),
          tick: compactRupees,
        },
        {
          key: 'paid',
          label: 'Paid orders',
          values: answered.points.map((point) => point.paidOrders),
          axis: 'right',
          token: '--chart-2',
          fill: false,
          dashed: true,
          format: (value) => `${count(value)} ${value === 1 ? 'order' : 'orders'}`,
          tick: (value) => count(value),
        },
      ],
    };
  });

  protected readonly table = computed<ChartTable | null>(() => {
    const answered = this.trend();

    return answered
      ? {
          caption: this.summary(),
          testId: 'trend-table',
          headers: [answered.interval === 'Month' ? 'Month' : 'Week starting', 'Gross revenue', 'Paid orders', 'Orders placed'],
          rows: answered.points.map((point) => ({
            key: point.start,
            label: this.label(point),
            cells: [this.rupees(point.grossRevenue), count(point.paidOrders), count(point.orders)],
          })),
        }
      : null;
  });

  protected readonly emptyText = computed(() => {
    const answered = this.trend();
    return answered ? `No sales in ${formatPeriod(answered.window)}.` : '';
  });

  /** One sentence: what is drawn, over which days, and its highest point. */
  protected readonly summary = computed(() => {
    const answered = this.trend();

    if (!answered || answered.points.length === 0) {
      return '';
    }

    const by = answered.interval === 'Month' ? 'month' : 'week';
    const highest = answered.points.reduce((best, point) => (point.grossRevenue > best.grossRevenue ? point : best));
    const paid = answered.points.reduce((total, point) => total + point.paidOrders, 0);

    return highest.grossRevenue <= 0
      ? `Gross revenue and paid orders by ${by}, ${formatPeriod(answered.window)}: none in any ${by}.`
      : `Gross revenue and paid orders by ${by}, ${formatPeriod(answered.window)}. Highest revenue: ${this.label(highest)}, ${this.rupees(highest.grossRevenue)}; ${count(paid)} paid ${paid === 1 ? 'order' : 'orders'} in all.`;
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
}
