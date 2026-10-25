import { ChangeDetectionStrategy, Component, Injector, computed, inject, input, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { money } from '../../core/orders/order-formatting';
import { PeriodQuery, ReportingApi } from '../../core/reporting/reporting-api';
import { addDays, colomboToday, count, formatDay, presetPeriod } from '../../core/reporting/reporting-format';
import { SalesGrouping, SalesReport, SalesReportRow } from '../../core/reporting/reporting.model';
import { Announcer, ExportNote, exportFailed, exportSucceeded, periodProblem, saveFile } from './report-shared';
import { ExportNoteView } from './export-note';
import { ChartView } from '../../core/charts/chart';
import { BarSpec, compactRupees } from '../../core/charts/chart-config';

/**
 * The sales report (ADM-19): one row per day, ISO week or month, and a total.
 *
 * The columns say how the figures fit together: <b>items + delivery = gross revenue</b>, and net revenue is
 * gross less the refunds that completed on those days. Department figures on the dashboard are the items
 * column, which is why they do not add up to gross revenue.
 *
 * **A bar chart of gross revenue sits above the table** (#186), one bar per row of it; the table under it is the
 * chart's figures, so the chart needs no folded copy of its own.
 */
@Component({
  selector: 'sda-sales-report',
  imports: [ChartView, ExportNoteView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="rpt__filters" aria-label="Sales report filters" (submit)="apply($event)">
      <label class="adm-field">
        <span class="adm-label">From</span>
        <input class="adm-input" type="date" name="from" data-testid="sales-from" [value]="from()" (change)="from.set(read($event))" />
      </label>
      <label class="adm-field">
        <span class="adm-label">To</span>
        <input class="adm-input" type="date" name="to" data-testid="sales-to" [value]="to()" (change)="to.set(read($event))" />
      </label>
      <label class="adm-field">
        <span class="adm-label">One row per</span>
        <select class="adm-input" name="groupBy" data-testid="sales-group" (change)="groupBy.set($any(read($event)))">
          <option value="Day" [selected]="groupBy() === 'Day'">Day</option>
          <option value="Week" [selected]="groupBy() === 'Week'">Week (Monday to Sunday)</option>
          <option value="Month" [selected]="groupBy() === 'Month'">Month</option>
        </select>
      </label>
      <div class="adm-actions"><button class="adm-btn--primary" type="submit" data-testid="sales-apply">Show</button></div>
      @if (error()) {
        <p class="adm-field-error rpt__wide" data-testid="sales-error">{{ error() }}</p>
      }
      <p class="adm-panel__hint rpt__wide">Colombo days, both included, at most 366. Revenue is counted on the day an order was paid; orders on the day it was placed.</p>
    </form>

    @if (canExport()) {
      <div class="adm-actions">
        <button class="adm-btn" type="button" data-testid="sales-export" [attr.aria-disabled]="exporting() ? 'true' : null" (click)="exportCsv()">
          {{ exporting() ? 'Preparing the file…' : 'Export CSV' }}
        </button>
      </div>
      <sda-export-note [note]="exportNote()" testId="sales-export-note" />
    }

    <p class="sr-only" role="status" data-testid="sales-status">{{ announcer.text() }}</p>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="sales-problem"><p>{{ failure.message }}</p></div>
    } @else if (report(); as answered) {
      <div class="adm-card">
        <div>
          <h3 class="adm-card__title">Gross revenue by {{ answered.groupBy.toLowerCase() }}</h3>
          <p class="adm-card__sub">Orders by the day they were paid; the figures are in the table below</p>
        </div>
        <sda-chart testId="sales-chart" [spec]="chart()" [summary]="chartSummary()" [height]="220" />
      </div>
      <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Sales report">
        <table class="adm-table" data-testid="sales-table">
          <caption class="sr-only">Sales from {{ day(answered.period.from) }} to {{ day(answered.period.to) }}, one row per {{ answered.groupBy.toLowerCase() }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ answered.groupBy }}</th>
              <th scope="col" class="adm-num">Orders placed</th>
              <th scope="col" class="adm-num">Paid orders</th>
              <th scope="col" class="adm-num">Gross revenue</th>
              <th scope="col" class="adm-num">Items after promo</th>
              <th scope="col" class="adm-num">Delivery</th>
              <th scope="col" class="adm-num">Refunds completed</th>
              <th scope="col" class="adm-num">Net revenue</th>
            </tr>
          </thead>
          <tbody>
            @for (row of answered.rows; track row.start) {
              <tr>
                <th scope="row">{{ span(row) }}</th>
                <td class="adm-num">{{ number(row.orders) }}</td>
                <td class="adm-num">{{ number(row.paidOrders) }}</td>
                <td class="adm-num">{{ rupees(row.grossRevenue) }}</td>
                <td class="adm-num">{{ rupees(row.itemsRevenue) }}</td>
                <td class="adm-num">{{ rupees(row.deliveryRevenue) }}</td>
                <td class="adm-num">{{ rupees(row.refundsCompleted) }}</td>
                <td class="adm-num">{{ rupees(row.netRevenue) }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr data-testid="sales-totals">
              <th scope="row">Total</th>
              <td class="adm-num">{{ number(answered.totals.orders) }}</td>
              <td class="adm-num">{{ number(answered.totals.paidOrders) }}</td>
              <td class="adm-num">{{ rupees(answered.totals.grossRevenue) }}</td>
              <td class="adm-num">{{ rupees(answered.totals.itemsRevenue) }}</td>
              <td class="adm-num">{{ rupees(answered.totals.deliveryRevenue) }}</td>
              <td class="adm-num">{{ rupees(answered.totals.refundsCompleted) }}</td>
              <td class="adm-num">{{ rupees(answered.totals.netRevenue) }}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    } @else {
      <p class="adm-muted">Loading the sales report…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-2); min-width: 0; }
    .rpt__filters { display: grid; gap: var(--space-2); align-items: end; grid-template-columns: repeat(auto-fit, minmax(min(100%, 11rem), 1fr)); }
    .rpt__wide { grid-column: 1 / -1; margin: 0; }
  `,
})
export class SalesReportView {
  /** Offered only to a role holding `Reports.Export`: an Export button that always answers 403 is a defect. */
  readonly canExport = input(false);

  private readonly api = inject(ReportingApi);
  private readonly document = inject(DOCUMENT);

  protected readonly announcer = new Announcer(inject(Injector));

  private readonly initial = presetPeriod('last30', colomboToday());

  protected readonly from = signal(this.initial.from);
  protected readonly to = signal(this.initial.to);
  protected readonly groupBy = signal<SalesGrouping>('Day');
  protected readonly error = signal<string | null>(null);

  /** What the table shows and the export exports — changed only by Show. */
  private readonly applied = signal<{ period: PeriodQuery; groupBy: SalesGrouping }>({ period: this.initial, groupBy: 'Day' });

  protected readonly report = signal<SalesReport | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly exporting = signal(false);
  protected readonly exportNote = signal<ExportNote | null>(null);

  private requests = 0;

  constructor() {
    this.load();
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';
  }

  protected apply(event: Event): void {
    event.preventDefault();

    const problem = periodProblem(this.from(), this.to(), addDays);
    this.error.set(problem);

    if (problem) {
      this.announcer.say(problem);
      return;
    }

    this.applied.set({ period: { from: this.from(), to: this.to() }, groupBy: this.groupBy() });
    this.load();
  }

  protected exportCsv(): void {
    // aria-disabled plus this refusal, never [disabled] (#109).
    if (this.exporting()) {
      return;
    }

    this.exporting.set(true);
    this.exportNote.set(null);
    this.announcer.say('Preparing the export…');

    const { period, groupBy } = this.applied();
    this.api.exportSales(period, groupBy).subscribe({
      next: (file) => {
        this.exporting.set(false);
        saveFile(this.document, file.blob, file.fileName);
        this.announcer.say(`Export downloaded as ${file.fileName}.`);
        this.exportNote.set(exportSucceeded(file.fileName));
      },
      error: (error: unknown) => {
        this.exporting.set(false);
        this.announcer.say(`The export failed. ${toCatalogFailure(error).message}`);
        this.exportNote.set(exportFailed(toCatalogFailure(error).message));
      },
    });
  }

  protected readonly chart = computed<BarSpec>(() => {
    const rows = this.report()?.rows ?? [];

    return {
      kind: 'bar',
      indexAxis: 'x',
      highlightFirst: false,
      seriesLabel: 'Gross revenue',
      labels: rows.map((row) => this.span(row)),
      values: rows.map((row) => row.grossRevenue),
      format: (value) => this.rupees(value),
      tick: compactRupees,
    };
  });

  protected readonly chartSummary = computed(() => {
    const answered = this.report();
    const best = answered?.rows.reduce<SalesReportRow | null>((top, row) => (top === null || row.grossRevenue > top.grossRevenue ? row : top), null) ?? null;

    return !answered || best === null || best.grossRevenue <= 0
      ? 'Gross revenue by period: none in these days.'
      : `Gross revenue by ${answered.groupBy.toLowerCase()}, ${this.rupees(answered.totals.grossRevenue)} in all. Highest: ${this.span(best)}, ${this.rupees(best.grossRevenue)}.`;
  });

  protected span(row: SalesReportRow): string {
    return row.start === row.end ? formatDay(row.start) : `${formatDay(row.start)} – ${formatDay(row.end)}`;
  }

  protected day(value: string): string {
    return formatDay(value);
  }

  protected rupees(amount: number): string {
    return money(amount, this.report()?.currency ?? 'LKR');
  }

  protected number(value: number): string {
    return count(value);
  }

  private load(): void {
    this.exportNote.set(null);
    const request = ++this.requests;
    const { period, groupBy } = this.applied();

    this.problem.set(null);
    this.report.set(null);

    this.api.sales(period, groupBy).subscribe({
      next: (answered) => {
        if (request === this.requests) {
          this.report.set(answered);
          this.announcer.say(`Sales report: ${answered.rows.length} ${answered.rows.length === 1 ? 'row' : 'rows'} and a total.`);
        }
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          const failure = toCatalogFailure(error);
          this.problem.set(failure);
          this.announcer.say(failure.message);
        }
      },
    });
  }
}
