import { ChangeDetectionStrategy, Component, Injector, inject, input, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { money } from '../../core/orders/order-formatting';
import { PeriodQuery, ReportingApi } from '../../core/reporting/reporting-api';
import { addDays, colomboToday, count, formatDay, presetPeriod } from '../../core/reporting/reporting-format';
import { CustomerReportPage } from '../../core/reporting/reporting.model';
import { Announcer, canGo, periodProblem, rehomeFocus, saveFile, showing } from './report-shared';

const PAGE_SIZE = 25;

/**
 * The customers report (ADM-19): everyone who paid for an order in the period, by email address, highest
 * spend first — guests included, because a guest is a customer who did not register. Spend is gross: an
 * order refunded later still counts, as on the Customers screen.
 */
@Component({
  selector: 'sda-customer-report',
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

  private requests = 0;

  constructor() {
    this.load(1);
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
    this.announcer.say('Preparing the export…');

    const { period, search } = this.applied();
    this.api.exportCustomers(period, search).subscribe({
      next: (file) => {
        this.exporting.set(false);
        saveFile(this.document, file.blob, file.fileName);
        this.announcer.say(`Export downloaded as ${file.fileName}.`);
      },
      error: (error: unknown) => {
        this.exporting.set(false);
        this.announcer.say(`The export failed. ${toCatalogFailure(error).message}`);
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
