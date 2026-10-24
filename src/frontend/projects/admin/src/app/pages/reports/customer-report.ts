import { ChangeDetectionStrategy, Component, Injector, computed, inject, input, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { money } from '../../core/orders/order-formatting';
import { PeriodQuery, ReportingApi } from '../../core/reporting/reporting-api';
import { addDays, colomboToday, count, formatDay, presetPeriod } from '../../core/reporting/reporting-format';
import { CustomerBuyers, CustomerBuyersPoint, CustomerReportPage } from '../../core/reporting/reporting.model';
import { ChartTable, ChartView } from '../../core/charts/chart';
import { StackedBarSpec } from '../../core/charts/chart-config';
import { Announcer, ExportNote, canGo, exportFailed, exportSucceeded, periodProblem, rehomeFocus, saveFile, showing } from './report-shared';
import { ExportNoteView } from './export-note';

const PAGE_SIZE = 25;

/**
 * The customers report (ADM-19): everyone who paid for an order in the period, by email address, highest
 * spend first — guests included, because a guest is a customer who did not register. Spend is gross: an
 * order refunded later still counts, as on the Customers screen.
 *
 * **A stacked bar chart of buyers per day, week or month sits above the table** (#186b), each bar split into
 * first-time and returning buyers, with the figures as a table under it. It comes from its own request
 * (`/customers/buyers`, same period and search), made when the filters are applied and not on every page turn.
 */
@Component({
  selector: 'sda-customer-report',
  imports: [ChartView, ExportNoteView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="rpt__filters" aria-label="Customers report filters" (submit)="apply($event)">
      <label class="adm-field">
        <span class="adm-label">Paid from</span>
        <input class="adm-input" type="date" name="from" data-testid="customers-from" [value]="from()" (change)="from.set(read($event))" />
      </label>
      <label class="adm-field">
        <span class="adm-label">Paid to</span>
        <input class="adm-input" type="date" name="to" data-testid="customers-to" [value]="to()" (change)="to.set(read($event))" />
      </label>
      <label class="adm-field">
        <span class="adm-label">Email address starts with</span>
        <input class="adm-input" type="search" name="search" maxlength="100" autocomplete="off" data-testid="customers-search" [value]="search()" (input)="search.set(read($event))" />
      </label>
      <div class="adm-actions"><button class="adm-btn--primary" type="submit" data-testid="customers-apply">Show</button></div>
      @if (error()) {
        <p class="adm-field-error rpt__wide" data-testid="customers-error">{{ error() }}</p>
      }
    </form>

    @if (canExport()) {
      <div class="adm-actions">
        <button class="adm-btn" type="button" data-testid="customers-export" [attr.aria-disabled]="exporting() ? 'true' : null" (click)="exportCsv()">
          {{ exporting() ? 'Preparing the file…' : 'Export CSV' }}
        </button>
      </div>
      <sda-export-note [note]="exportNote()" testId="customers-export-note" />
      <p class="adm-panel__hint">The file holds names and email addresses. Every export is recorded in the audit log against your name.</p>
    }

    <p class="sr-only" role="status" tabindex="-1" data-testid="customers-status">{{ announcer.text() }}</p>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="customers-problem"><p>{{ failure.message }}</p></div>
    } @else if (page(); as answered) {
      <p class="adm-panel__hint" data-testid="customers-summary">
        {{ number(answered.totalCount) }} {{ answered.totalCount === 1 ? 'buyer' : 'buyers' }}, {{ rupees(answered.totalRevenue) }} gross revenue between {{ day(answered.period.from) }} and {{ day(answered.period.to) }}.
      </p>
      @if (answered.totalCount === 0) {
        <p class="adm-muted" data-testid="customers-empty">Nobody paid for an order in these days.</p>
      } @else {
        @if (buyersProblem(); as failure) {
          <p class="adm-muted" data-testid="customers-chart-problem">The buyers chart could not be loaded. {{ failure.message }}</p>
        } @else if (buyers(); as chartData) {
          <div class="adm-card" data-testid="customers-chart-card">
            <div>
              <h3 class="adm-card__title">Buyers by {{ chartData.interval.toLowerCase() }}, first-time and returning</h3>
              <p class="adm-card__sub" data-testid="customers-chart-totals">{{ totalsLine(chartData) }}</p>
            </div>
            <sda-chart testId="customers-chart" [spec]="chart()" [summary]="chartSummary()" [table]="chartTable()" [height]="220" emptyText="Nobody paid for an order in these days." />
            <p class="adm-panel__hint rpt__defs" data-testid="customers-chart-defs">
              A buyer is an email address with at least one paid order in the days shown (an order refunded later still counts). First-time: their first paid order in this shop, ever, is in those days. Returning: they had paid for an order here before those days began. Someone who paid in two {{ plural(chartData.interval) }} is in both bars, and once in the totals.
            </p>
          </div>
        }
        <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Customers report">
          <table class="adm-table" data-testid="customers-table">
            <caption class="sr-only">Buyers, highest spend first</caption>
            <thead>
              <tr>
                <th scope="col">Email</th>
                <th scope="col">Name</th>
                <th scope="col">Delivery district</th>
                <th scope="col">Account</th>
                <th scope="col" class="adm-num">Paid orders</th>
                <th scope="col" class="adm-num">Gross revenue</th>
                <th scope="col">First paid</th>
                <th scope="col">Last paid</th>
              </tr>
            </thead>
            <tbody>
              @for (row of answered.rows; track row.email) {
                <tr>
                  <th scope="row" class="rpt__email">{{ row.email }}</th>
                  <td>{{ row.name }}</td>
                  <td>{{ row.district }}</td>
                  <td>{{ row.hasAccount ? 'Yes' : 'Guest' }}</td>
                  <td class="adm-num">{{ number(row.paidOrders) }}</td>
                  <td class="adm-num">{{ rupees(row.grossRevenue) }}</td>
                  <td>{{ day(row.firstPaidOn) }}</td>
                  <td>{{ day(row.lastPaidOn) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <nav class="adm-actions rpt__paging" aria-label="Pages">
          <button class="adm-btn" type="button" data-testid="customers-prev" [attr.aria-disabled]="answered.page <= 1 ? 'true' : null" (click)="go(answered.page - 1)">Previous</button>
          <p class="adm-muted" data-testid="customers-paging">{{ range(answered) }}</p>
          <button class="adm-btn" type="button" data-testid="customers-next" [attr.aria-disabled]="answered.page * answered.pageSize >= answered.totalCount ? 'true' : null" (click)="go(answered.page + 1)">Next</button>
        </nav>
      }
    } @else {
      <p class="adm-muted">Loading buyers…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-2); min-width: 0; }
    .rpt__filters { display: grid; gap: var(--space-2); align-items: end; grid-template-columns: repeat(auto-fit, minmax(min(100%, 11rem), 1fr)); }
    .rpt__wide { grid-column: 1 / -1; margin: 0; }
    .rpt__paging { align-items: center; }
    .rpt__email { overflow-wrap: anywhere; }
    .rpt__defs { margin: 0; }
  `,
})
export class CustomerReportView {
  readonly canExport = input(false);

  private readonly api = inject(ReportingApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly announcer = new Announcer(this.injector);

  private readonly initial = presetPeriod('last30', colomboToday());

  protected readonly from = signal(this.initial.from);
  protected readonly to = signal(this.initial.to);
  protected readonly search = signal('');
  protected readonly error = signal<string | null>(null);
  private readonly applied = signal<{ period: PeriodQuery; search: string }>({ period: this.initial, search: '' });

  protected readonly page = signal<CustomerReportPage | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly exporting = signal(false);
  protected readonly exportNote = signal<ExportNote | null>(null);

  protected readonly buyers = signal<CustomerBuyers | null>(null);
  protected readonly buyersProblem = signal<CatalogFailure | null>(null);

  private requests = 0;
  private buyerRequests = 0;

  constructor() {
    this.load(1);
    this.loadBuyers();
  }

  /** First-time at the bottom in blue, returning on top in amber: a vivid pair that stays apart for colour-blind readers. */
  protected readonly chart = computed<StackedBarSpec>(() => {
    const points = this.buyers()?.points ?? [];

    return {
      kind: 'stacked',
      labels: points.map((point) => this.span(point)),
      series: [
        { key: 'firstTime', label: 'First-time buyers', values: points.map((point) => point.firstTimeBuyers), token: '--chart-1' },
        { key: 'returning', label: 'Returning buyers', values: points.map((point) => point.returningBuyers), token: '--chart-2' },
      ],
      format: (value) => this.buyerCount(value),
      tick: (value) => count(value),
    };
  });

  protected readonly chartSummary = computed(() => {
    const answered = this.buyers();
    const busiest = answered?.points.reduce<CustomerBuyersPoint | null>((top, point) => (top === null || point.buyers > top.buyers ? point : top), null) ?? null;

    return !answered || busiest === null || busiest.buyers <= 0
      ? 'Buyers by period: nobody paid for an order in these days.'
      : `Buyers by ${answered.interval.toLowerCase()}: ${this.buyerCount(answered.buyers)} in all, ${count(answered.firstTimeBuyers)} first-time and ${count(answered.returningBuyers)} returning; the most in one ${answered.interval.toLowerCase()} was ${this.buyerCount(busiest.buyers)}, ${this.span(busiest)}.`;
  });

  protected readonly chartTable = computed<ChartTable | null>(() => {
    const answered = this.buyers();

    if (!answered) {
      return null;
    }

    const cells = (figures: { buyers: number; firstTimeBuyers: number; returningBuyers: number }) => [count(figures.buyers), count(figures.firstTimeBuyers), count(figures.returningBuyers)];

    return {
      caption: `Buyers by ${answered.interval.toLowerCase()}, first-time and returning`,
      testId: 'customers-chart-table',
      headers: [answered.interval, 'Buyers', 'First-time', 'Returning'],
      rows: [
        ...answered.points.map((point) => ({ key: point.start, label: this.span(point), cells: cells(point) })),
        { key: 'period', label: 'Whole period (each buyer once)', cells: cells(answered) },
      ],
    };
  });

  protected totalsLine(answered: CustomerBuyers): string {
    return `${this.buyerCount(answered.buyers)}: ${count(answered.firstTimeBuyers)} first-time, ${count(answered.returningBuyers)} returning`;
  }

  protected plural(interval: string): string {
    return ({ Day: 'days', Week: 'weeks', Month: 'months' } as Record<string, string>)[interval] ?? 'periods';
  }

  private span(point: CustomerBuyersPoint): string {
    return point.start === point.end ? formatDay(point.start) : `${formatDay(point.start)} – ${formatDay(point.end)}`;
  }

  private buyerCount(value: number): string {
    return `${count(value)} ${value === 1 ? 'buyer' : 'buyers'}`;
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | null)?.value ?? '';
  }

  protected apply(event: Event): void {
    event.preventDefault();

    const problem = periodProblem(this.from(), this.to(), addDays);
    this.error.set(problem);

    if (problem) {
      this.announcer.say(problem);
      return;
    }

    this.applied.set({ period: { from: this.from(), to: this.to() }, search: this.search().trim() });
    this.load(1);
    this.loadBuyers();
  }

  protected go(target: number): void {
    const answered = this.page();

    // aria-disabled plus this refusal, never [disabled] (#109).
    if (answered === null || !canGo(target, answered.pageSize, answered.totalCount)) {
      return;
    }

    this.load(target);
  }

  protected exportCsv(): void {
    if (this.exporting()) {
      return;
    }

    this.exporting.set(true);
    this.exportNote.set(null);
    this.announcer.say('Preparing the export…');

    const { period, search } = this.applied();
    this.api.exportCustomers(period, search).subscribe({
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

  protected range(answered: CustomerReportPage): string {
    return showing(answered.page, answered.pageSize, answered.totalCount);
  }

  protected day(value: string): string {
    return formatDay(value);
  }

  protected rupees(amount: number): string {
    return money(amount, this.page()?.currency ?? 'LKR');
  }

  protected number(value: number): string {
    return count(value);
  }

  /** The chart for the applied filters; a failure here leaves the table alone and says so in the card's place. */
  private loadBuyers(): void {
    const request = ++this.buyerRequests;
    const { period, search } = this.applied();

    this.buyers.set(null);
    this.buyersProblem.set(null);

    this.api.customerBuyers(period, search).subscribe({
      next: (answered) => {
        if (request === this.buyerRequests) {
          this.buyers.set(answered);
        }
      },
      error: (error: unknown) => {
        if (request === this.buyerRequests) {
          this.buyersProblem.set(toCatalogFailure(error));
        }
      },
    });
  }

  /** The current page stays until the next arrives, so the paging buttons are never removed under the keyboard. */
  private load(target: number): void {
    const request = ++this.requests;
    const { period, search } = this.applied();

    this.problem.set(null);

    this.api.customers(period, search, target, PAGE_SIZE).subscribe({
      next: (answered) => {
        if (request === this.requests) {
          this.page.set(answered);
          this.announcer.say(`${answered.totalCount} ${answered.totalCount === 1 ? 'buyer' : 'buyers'}. Showing ${this.range(answered)}.`);
        }
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          const failure = toCatalogFailure(error);
          const focused = this.document.activeElement as HTMLElement | null;
          this.page.set(null);
          this.problem.set(failure);
          this.announcer.say(failure.message);
          rehomeFocus(this.document, this.injector, focused, 'customers-status');
        }
      },
    });
  }
}
