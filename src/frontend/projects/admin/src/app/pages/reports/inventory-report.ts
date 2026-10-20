import { ChangeDetectionStrategy, Component, Injector, OnInit, computed, inject, input, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { ReportingApi } from '../../core/reporting/reporting-api';
import { count } from '../../core/reporting/reporting-format';
import { INVENTORY_FILTERS, InventoryReportPage, InventoryStockFilter } from '../../core/reporting/reporting.model';
import { Announcer, canGo, rehomeFocus, saveFile, showing } from './report-shared';
import { ChartTable, ChartView } from '../../core/charts/chart';
import { DoughnutSpec, shareText, sharesOf } from '../../core/charts/chart-config';

const PAGE_SIZE = 25;

const FILTER_LABELS: Record<InventoryStockFilter, string> = {
  All: 'Every variant',
  InStock: 'In stock',
  LowStock: 'Low stock',
  OutOfStock: 'Out of stock',
};

/**
 * The inventory report (ADM-19): every active variant's stock now, filtered by stock status and searchable.
 * Live — it has no period. The export is every matching variant, not this page.
 *
 * **A doughnut of in stock / low / out of stock sits above the table** (#186), from the report's own `counts`,
 * which cover every active variant whatever the filter and search — so the chart says so.
 */
@Component({
  selector: 'sda-inventory-report',
  imports: [ChartView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="rpt__filters" aria-label="Inventory report filters" (submit)="apply($event)">
      <label class="adm-field">
        <span class="adm-label">Stock</span>
        <select class="adm-input" name="status" data-testid="inventory-status" (change)="draftStatus.set($any(read($event)))">
          @for (option of filters; track option) {
            <option [value]="option" [selected]="draftStatus() === option">{{ label(option) }}</option>
          }
        </select>
      </label>
      <label class="adm-field">
        <span class="adm-label">Product name, SKU or code</span>
        <input class="adm-input" type="search" name="search" maxlength="100" autocomplete="off" data-testid="inventory-search" [value]="draftSearch()" (input)="draftSearch.set(read($event))" />
      </label>
      <div class="adm-actions"><button class="adm-btn--primary" type="submit" data-testid="inventory-apply">Show</button></div>
    </form>

    @if (canExport()) {
      <div class="adm-actions">
        <button class="adm-btn" type="button" data-testid="inventory-export" [attr.aria-disabled]="exporting() ? 'true' : null" (click)="exportCsv()">
          {{ exporting() ? 'Preparing the file…' : 'Export CSV' }}
        </button>
      </div>
    }

    <p class="sr-only" role="status" tabindex="-1" data-testid="inventory-status-message">{{ announcer.text() }}</p>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="inventory-problem"><p>{{ failure.message }}</p></div>
    } @else if (page(); as answered) {
      <div class="adm-card">
        <div>
          <h3 class="adm-card__title">Stock status</h3>
          <p class="adm-card__sub">Every active variant now, whatever the filter</p>
        </div>
        <sda-chart testId="inventory-chart" [spec]="chart()" [summary]="chartSummary()" [height]="180" [table]="chartTable()" emptyText="There are no active variants yet." />
      </div>
      <p class="adm-panel__hint" data-testid="inventory-counts">
        {{ number(answered.counts.inStock) }} in stock · {{ number(answered.counts.lowStock) }} low · {{ number(answered.counts.outOfStock) }} out of stock.
        Low means 1 to the variant's threshold available; the shop default threshold is {{ answered.defaultThreshold }}.
        Available is on hand less what unpaid orders are holding.
      </p>
      @if (answered.totalCount === 0) {
        <p class="adm-muted" data-testid="inventory-empty">No variant matches.</p>
      } @else {
        <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Inventory report">
          <table class="adm-table" data-testid="inventory-table">
            <caption class="sr-only">Stock per variant</caption>
            <thead>
              <tr>
                <th scope="col">Product</th>
                <th scope="col">SKU</th>
                <th scope="col">Colour / size</th>
                <th scope="col">Department</th>
                <th scope="col" class="adm-num">On hand</th>
                <th scope="col" class="adm-num">Reserved</th>
                <th scope="col" class="adm-num">Available</th>
                <th scope="col" class="adm-num">Threshold</th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              @for (row of answered.rows; track row.variantId) {
                <tr [attr.data-testid]="'variant-' + row.variantId">
                  <th scope="row">{{ row.productName }}</th>
                  <td>{{ row.sku }}</td>
                  <td>{{ row.colour }} / {{ row.size }}</td>
                  <td>{{ row.department }}</td>
                  <td class="adm-num">{{ number(row.onHand) }}</td>
                  <td class="adm-num">{{ number(row.reserved) }}</td>
                  <td class="adm-num">{{ number(row.available) }}</td>
                  <td class="adm-num">{{ row.threshold }}{{ row.thresholdIsDefault ? ' (default)' : '' }}</td>
                  <td><span class="adm-pill" [class.adm-pill--hidden]="row.stockStatus !== 'InStock'">{{ label(row.stockStatus) }}</span></td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <nav class="adm-actions rpt__paging" aria-label="Pages">
          <button class="adm-btn" type="button" data-testid="inventory-prev" [attr.aria-disabled]="answered.page <= 1 ? 'true' : null" (click)="go(answered.page - 1)">Previous</button>
          <p class="adm-muted" data-testid="inventory-paging">{{ range(answered) }}</p>
          <button class="adm-btn" type="button" data-testid="inventory-next" [attr.aria-disabled]="answered.page * answered.pageSize >= answered.totalCount ? 'true' : null" (click)="go(answered.page + 1)">Next</button>
        </nav>
      }
    } @else {
      <p class="adm-muted">Loading stock…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-2); min-width: 0; }
    .rpt__filters { display: grid; gap: var(--space-2); align-items: end; grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr)); }
    .rpt__paging { align-items: center; }
  `,
})
export class InventoryReportView implements OnInit {
  readonly canExport = input(false);

  /** The stock filter to open on, from the dashboard's low-stock link (`?status=LowStock`). */
  readonly initialStatus = input<InventoryStockFilter>('All');

  private readonly api = inject(ReportingApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly announcer = new Announcer(this.injector);
  protected readonly filters = INVENTORY_FILTERS;

  protected readonly draftStatus = signal<InventoryStockFilter>('All');
  protected readonly draftSearch = signal('');
  private readonly applied = signal<{ status: InventoryStockFilter; search: string }>({ status: 'All', search: '' });

  protected readonly page = signal<InventoryReportPage | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly exporting = signal(false);

  private requests = 0;

  ngOnInit(): void {
    const status = INVENTORY_FILTERS.includes(this.initialStatus()) ? this.initialStatus() : 'All';
    this.draftStatus.set(status);
    this.applied.set({ status, search: '' });
    this.load(1);
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';
  }

  protected label(filter: InventoryStockFilter): string {
    return FILTER_LABELS[filter];
  }

  private readonly statuses = computed(() => {
    const counts = this.page()?.counts ?? { inStock: 0, lowStock: 0, outOfStock: 0 };
    const rows = [
      { key: 'InStock', label: 'In stock', value: counts.inStock, token: '--stock-in' },
      { key: 'LowStock', label: 'Low stock', value: counts.lowStock, token: '--stock-low' },
      { key: 'OutOfStock', label: 'Out of stock', value: counts.outOfStock, token: '--stock-out' },
    ];
    const shares = sharesOf(rows.map((row) => row.value));
    return rows.map((row, index) => ({ ...row, share: shares[index], display: `${count(row.value)} ${row.value === 1 ? 'variant' : 'variants'}` }));
  });

  protected readonly chart = computed<DoughnutSpec>(() => ({ kind: 'doughnut', cutout: 65, slices: this.statuses() }));

  protected readonly chartTable = computed<ChartTable>(() => ({
    caption: 'Active variants by stock status',
    testId: 'inventory-chart-table',
    headers: ['Status', 'Variants', 'Share'],
    rows: this.statuses().map((row) => ({ key: row.key, label: row.label, cells: [count(row.value), shareText(row.share)] })),
  }));

  protected readonly chartSummary = computed(() => {
    const [inStock, low, out] = this.statuses();
    return `Active variants by stock status: ${inStock.display} in stock, ${low.display} low, ${out.display} out of stock.`;
  });

  protected number(value: number): string {
    return count(value);
  }

  protected range(answered: InventoryReportPage): string {
    return showing(answered.page, answered.pageSize, answered.totalCount);
  }

  protected apply(event: Event): void {
    event.preventDefault();
    this.applied.set({ status: this.draftStatus(), search: this.draftSearch().trim() });
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

    const { status, search } = this.applied();
    this.api.exportInventory(status, search).subscribe({
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

  /**
   * The current page stays on screen until the next one arrives, so the Previous and Next buttons are never
   * removed under the keyboard. If the request fails the table goes, and focus is re-homed on the status
   * line after the next render — `afterNextRender`, never `queueMicrotask` (#109).
   */
  private load(target: number): void {
    const request = ++this.requests;
    const { status, search } = this.applied();

    this.problem.set(null);

    this.api.inventory(status, search, target, PAGE_SIZE).subscribe({
      next: (answered) => {
        if (request === this.requests) {
          this.page.set(answered);
          this.announcer.say(`${answered.totalCount} ${answered.totalCount === 1 ? 'variant' : 'variants'}. Showing ${this.range(answered)}.`);
        }
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          const failure = toCatalogFailure(error);
          const focused = this.document.activeElement as HTMLElement | null;
          this.page.set(null);
          this.problem.set(failure);
          this.announcer.say(failure.message);
          rehomeFocus(this.document, this.injector, focused, 'inventory-status-message');
        }
      },
    });
  }
}
