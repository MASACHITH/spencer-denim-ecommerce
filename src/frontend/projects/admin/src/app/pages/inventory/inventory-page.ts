import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { InventoryApi } from '../../core/inventory/inventory-api';
import {
  INVENTORY_PERMISSIONS,
  INVENTORY_STATUS_FILTERS,
  InventoryFilterOptions,
  InventoryListPage,
  InventoryQuery,
  InventoryRow,
  InventorySort,
  InventorySortDirection,
  InventoryStatusFilter,
  STATUS_LABELS,
  StockChangeResult,
  StockChangeRowResult,
  statusOf,
} from '../../core/inventory/inventory.model';
import { Announcer, canGo, showing } from '../reports/report-shared';
import { BulkChangeForm } from './bulk-change-form';
import { MovementHistory } from './movement-history';
import { StockChangeForm } from './stock-change-form';
import { ThresholdForm } from './threshold-form';

const PAGE_SIZE = 25;

const SORTS: readonly { readonly value: string; readonly label: string }[] = [
  { value: 'Product:Asc', label: 'Product, A to Z' },
  { value: 'Product:Desc', label: 'Product, Z to A' },
  { value: 'Sku:Asc', label: 'SKU' },
  { value: 'Available:Asc', label: 'Fewest available first' },
  { value: 'Available:Desc', label: 'Most available first' },
  { value: 'OnHand:Desc', label: 'Most on hand first' },
  { value: 'Reserved:Desc', label: 'Most reserved first' },
];

type EditorMode = 'stock' | 'threshold' | 'history' | 'bulk';

interface Editor {
  readonly mode: EditorMode;
  readonly variantId: number | null;
}

interface Access {
  readonly view: boolean;
  readonly edit: boolean;
}

/**
 * Inventory (ADM-12, W1-05, #190; doc 11 P2 part A): stock per variant, the bulk update, the low-stock
 * threshold and each variant's movement history.
 *
 * **What a role cannot use is not offered** (the API enforces the same): `Inventory.View` sees the list
 * and the history — Order Staff, per doc 02's role table — and only `Inventory.Edit` sees the change,
 * threshold and bulk controls.
 *
 * **One editor at a time, in a panel above the table rather than inside it.** The table is wider than a
 * phone and scrolls inside its card (the 320 px rule), so a form inside a table row would be as wide as the
 * table and half off the screen. The panel takes focus when it opens and gives it back to the button that
 * opened it when it closes.
 *
 * **A change patches the row from the server's answer instead of reloading the page**, so the row a staff
 * member just changed stays where it is (and under the keyboard) even when it no longer matches the status
 * filter. The counts are the ones the page loaded with, and the hint says so.
 */
@Component({
  selector: 'sda-inventory-page',
  imports: [RouterLink, StockChangeForm, ThresholdForm, MovementHistory, BulkChangeForm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="inv">
      <h1 class="inv__title">Inventory</h1>
      <p class="sr-only" role="status" tabindex="-1" data-testid="inventory-said">{{ announcer.text() }}</p>

      @if (access() === null) {
        <p class="adm-muted">Loading…</p>
      } @else if (!access()!.view) {
        <p class="adm-state" data-testid="inventory-forbidden">
          Your role does not include inventory. <a class="adm-link" routerLink="/">Back to the dashboard</a>
        </p>
      } @else {
        <section class="adm-panel" aria-labelledby="inv-find">
          <h2 class="adm-panel__title" id="inv-find">Find variants</h2>
          <form class="inv__filters" role="search" aria-labelledby="inv-find" (submit)="apply($event)">
            <label class="adm-field">
              <span class="adm-label">Product name, SKU or code</span>
              <input class="adm-input" type="search" name="search" maxlength="100" autocomplete="off" data-testid="inventory-search" [value]="draft().search" (input)="edit({ search: read($event) })" />
            </label>
            <label class="adm-field">
              <span class="adm-label">Stock</span>
              <select class="adm-input" name="status" data-testid="inventory-status" (change)="edit({ status: $any(read($event)) })">
                @for (option of statuses; track option) {
                  <option [value]="option" [selected]="draft().status === option">{{ statusLabel(option) }}</option>
                }
              </select>
            </label>
            <label class="adm-field">
              <span class="adm-label">Department</span>
              <select class="adm-input" name="department" data-testid="inventory-department" (change)="chooseDepartment($event)">
                <option value="" [selected]="draft().departmentId === null">Every department</option>
                @for (department of filters()?.departments ?? []; track department.id) {
                  <option [value]="department.id" [selected]="draft().departmentId === department.id">{{ department.name }}</option>
                }
              </select>
            </label>
            <label class="adm-field">
              <span class="adm-label">Category</span>
              <select class="adm-input" name="category" data-testid="inventory-category" (change)="edit({ categoryId: numberOrNull(read($event)) })">
                <option value="" [selected]="draft().categoryId === null">Every category</option>
                @for (category of categories(); track category.id) {
                  <option [value]="category.id" [selected]="draft().categoryId === category.id">{{ indent(category.depth) }}{{ category.name }}</option>
                }
              </select>
            </label>
            <label class="adm-field">
              <span class="adm-label">Order</span>
              <select class="adm-input" name="sort" data-testid="inventory-sort" (change)="chooseSort($event)">
                @for (option of sorts; track option.value) {
                  <option [value]="option.value" [selected]="sortValue() === option.value">{{ option.label }}</option>
                }
              </select>
            </label>
            <div class="adm-actions inv__go">
              <button class="adm-btn--primary" type="submit" data-testid="inventory-apply">Show</button>
              <button class="adm-btn" type="button" data-testid="inventory-clear" (click)="clear()">Clear</button>
            </div>
          </form>
          @if (page(); as answered) {
            <p class="adm-panel__hint" data-testid="inventory-counts">
              When this page loaded: {{ answered.counts.inStock }} in stock · {{ answered.counts.lowStock }} low ·
              {{ answered.counts.outOfStock }} out of stock. Low is 1 to the variant's own threshold, or the shop default
              of {{ answered.defaultThreshold }}; out of stock is never counted as low. Available is on hand less what
              unpaid orders hold.
            </p>
          }
          @if (!access()!.edit) {
            <p class="adm-muted" data-testid="inventory-read-only">Your role can see stock but not change it.</p>
          }
        </section>

        @if (bulkResult(); as result) {
          <section class="adm-panel" aria-labelledby="inv-bulk-result" data-testid="bulk-result">
            <h2 class="adm-panel__title" id="inv-bulk-result" tabindex="-1">
              {{ result.appliedCount }} saved, {{ result.notAppliedCount }} not changed
            </h2>
            @if (result.notAppliedCount > 0) {
              <ul class="inv__results">
                @for (row of notApplied(result); track row.variantId) {
                  <li [attr.data-testid]="'bulk-row-' + row.variantId"><strong>{{ skuOf(row.variantId) }}</strong>: {{ row.message }}</li>
                }
              </ul>
            }
            <div class="adm-actions"><button class="adm-btn" type="button" data-testid="bulk-result-close" (click)="bulkResult.set(null)">Close</button></div>
          </section>
        }

        @if (editor(); as open) {
          <section class="adm-panel" aria-labelledby="inv-editor-title" data-testid="inventory-editor">
            <h2 class="adm-panel__title" id="inv-editor-title" tabindex="-1">{{ editorTitle() }}</h2>
            @switch (open.mode) {
              @case ('stock') {
                @if (editorRow(); as row) {
                  <sda-stock-change-form [row]="row" (answered)="stockAnswered($event)" (cancelled)="close()" />
                }
              }
              @case ('threshold') {
                @if (editorRow(); as row) {
                  <sda-threshold-form [row]="row" [defaultThreshold]="page()?.defaultThreshold ?? 0" (saved)="thresholdSaved($event)" (cancelled)="close()" />
                }
              }
              @case ('history') {
                <sda-movement-history [variantId]="open.variantId!" />
                <div class="adm-actions"><button class="adm-btn" type="button" data-testid="history-close" (click)="close()">Close</button></div>
              }
              @case ('bulk') {
                <sda-bulk-change-form [rows]="selectedRows()" (applied)="bulkApplied($event)" (cancelled)="close()" />
              }
            }
          </section>
        }

        @if (access()!.edit && selected().size > 0) {
          <div class="adm-panel inv__bar" data-testid="bulk-bar">
            <p>{{ selected().size }} selected</p>
            <div class="adm-actions">
              <button class="adm-btn--primary" type="button" data-testid="bulk-open" (click)="open('bulk', null, $event)">Change stock for selected</button>
              <button class="adm-btn" type="button" data-testid="bulk-clear" (click)="selected.set(empty)">Clear selection</button>
            </div>
          </div>
        }

        @if (problem(); as failure) {
          <div class="adm-state adm-state--error" role="alert" data-testid="inventory-problem">
            <p>{{ failure.message }}</p>
            @if (failure.kind !== 'forbidden') {
              <div class="adm-actions"><button class="adm-btn" type="button" (click)="load(query().page)">Try again</button></div>
            }
          </div>
        } @else if (page(); as answered) {
          @if (answered.totalCount === 0) {
            <p class="adm-muted" data-testid="inventory-empty">No variant matches these filters.</p>
          } @else {
            <section class="adm-panel" aria-labelledby="inv-stock">
            <h2 class="adm-panel__title" id="inv-stock">Variants</h2>
            <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Stock per variant">
              <table class="adm-table" data-testid="inventory-table">
                <caption class="sr-only">Stock per variant</caption>
                <thead>
                  <tr>
                    @if (access()!.edit) {
                      <th scope="col">
                        <label class="adm-check inv__pick">
                          <input type="checkbox" data-testid="select-page" [checked]="allSelected()" (change)="selectPage($event)" />
                          <span class="sr-only">Select every variant on this page</span>
                        </label>
                      </th>
                    }
                    <th scope="col">Product</th>
                    <th scope="col">SKU</th>
                    <th scope="col">Colour / size</th>
                    <th scope="col" class="adm-num">On hand</th>
                    <th scope="col" class="adm-num">Reserved</th>
                    <th scope="col" class="adm-num">Available</th>
                    <th scope="col" class="adm-num">Threshold</th>
                    <th scope="col">Status</th>
                    <th scope="col"><span class="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of answered.rows; track row.variantId) {
                    <tr [attr.data-testid]="'variant-' + row.variantId">
                      @if (access()!.edit) {
                        <td>
                          <label class="adm-check inv__pick">
                            <input type="checkbox" [attr.data-testid]="'select-' + row.variantId" [checked]="selected().has(row.variantId)" [disabled]="!row.hasStockRecord" (change)="toggle(row.variantId, $event)" />
                            <span class="sr-only">Select {{ row.sku }}</span>
                          </label>
                        </td>
                      }
                      <th scope="row" class="inv__product">
                        {{ row.productName }}
                        <span class="adm-muted inv__meta">{{ row.productCode }} · {{ row.departmentName }}{{ row.categoryName ? ' · ' + row.categoryName : '' }}</span>
                      </th>
                      <td class="inv__wrap">{{ row.sku }}</td>
                      <td>{{ row.colour }} / {{ row.size }}</td>
                      <td class="adm-num" [attr.data-testid]="'on-hand-' + row.variantId">{{ row.onHand }}</td>
                      <td class="adm-num">{{ row.reserved }}</td>
                      <td class="adm-num" [attr.data-testid]="'available-' + row.variantId">{{ row.available }}</td>
                      <td class="adm-num" [attr.data-testid]="'threshold-' + row.variantId">{{ row.threshold }}{{ row.ownThreshold === null ? ' (default)' : '' }}</td>
                      <td>
                        <span class="adm-pill" [class.adm-pill--hidden]="row.stockStatus === 'OutOfStock'" [class.inv__in]="row.stockStatus === 'InStock'" [class.inv__low]="row.stockStatus === 'LowStock'" [class.inv__out]="row.stockStatus === 'OutOfStock'" [attr.data-testid]="'status-' + row.variantId">{{ statusLabel(row.stockStatus) }}</span>
                      </td>
                      <td>
                        <div class="adm-actions inv__row-actions">
                          @if (access()!.edit && row.hasStockRecord) {
                            <button class="adm-btn inv__act inv__act--stock" type="button" [attr.data-testid]="'change-' + row.variantId" [attr.aria-label]="'Change stock for ' + row.sku" (click)="open('stock', row.variantId, $event)">Stock</button>
                            <button class="adm-btn inv__act inv__act--threshold" type="button" [attr.data-testid]="'set-threshold-' + row.variantId" [attr.aria-label]="'Low-stock threshold for ' + row.sku" (click)="open('threshold', row.variantId, $event)">Threshold</button>
                          }
                          <button class="adm-btn inv__act inv__act--history" type="button" [attr.data-testid]="'history-' + row.variantId" [attr.aria-label]="'Stock history for ' + row.sku" (click)="open('history', row.variantId, $event)">History</button>
                        </div>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>

            <nav class="adm-actions inv__paging" aria-label="Pages">
              <button class="adm-btn" type="button" data-testid="inventory-prev" [attr.aria-disabled]="answered.page <= 1 ? 'true' : null" (click)="go(answered.page - 1)">Previous</button>
              <p class="adm-muted" data-testid="inventory-paging">{{ range(answered) }}</p>
              <button class="adm-btn" type="button" data-testid="inventory-next" [attr.aria-disabled]="answered.page * answered.pageSize >= answered.totalCount ? 'true' : null" (click)="go(answered.page + 1)">Next</button>
            </nav>
            </section>
          }
        } @else {
          <p class="adm-muted">Loading stock…</p>
        }
      }
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .inv { display: grid; gap: var(--space-3); max-width: 1400px; margin-inline: auto; min-width: 0; }
    .inv > *, .inv .adm-panel > * { min-width: 0; }
    /* The visually hidden labels are absolutely positioned; without this they escape the scroller's clip
       and widen the page by the table's width at 320 px. */
    .adm-table-scroll { position: relative; }
    .inv__title { font-size: var(--text-h2); }
    .inv__filters { display: grid; gap: var(--space-2); align-items: end; grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr)); }
    .inv__go, .inv__paging, .inv__bar { align-items: center; }
    .inv__bar { display: flex; flex-wrap: wrap; justify-content: space-between; gap: var(--space-1); }
    .inv__product { min-width: 6rem; overflow-wrap: anywhere; }
    .inv__meta { display: block; font-size: var(--text-small); }
    .inv__wrap { overflow-wrap: anywhere; min-width: 4rem; }
    .inv__row-actions { flex-wrap: nowrap; white-space: nowrap; gap: var(--space-0-5); }
    /* Compact row buttons (tester #190, D4a): the three must fit beside the figures at 1440 px and up
       without the table scrolling inside its card. Still 44 px tall and at least 44 px wide (min-width
       from adm-btn). Below 1440 px the table may scroll inside its card; it never scrolls the page. */
    .inv__act { padding-inline: var(--space-0-5); letter-spacing: normal; }
    /* Colour per job, so a row of three buttons reads at a glance (UAT feedback, 2026-10-24: they were three
       identical white boxes). Stock changes the count (blue), Threshold sets the low-stock line (teal), History only
       looks (slate). Each colour is a tint with its own darker shade for the label, mixed with the page's text
       colour so it stays above 4.5:1 in the dark theme too; the border carries the hue for anyone who cannot tell
       tints apart, and the words are still on the button. */
    .inv__act {
      --act: var(--status-cancelled);
      font-weight: var(--weight-semibold);
      color: color-mix(in srgb, var(--act) 62%, var(--color-text));
      border-color: var(--act);
      background: color-mix(in srgb, var(--act) 10%, var(--color-surface));
    }
    .inv__act:hover { background: color-mix(in srgb, var(--act) 22%, var(--color-surface)); }
    .inv__act--stock { --act: var(--status-dispatched); }
    .inv__act--threshold { --act: var(--status-delivered); }
    .inv__act--history { --act: var(--status-cancelled); }
    .inv__pick { min-width: var(--tap); justify-content: center; }
    /* The stock status, in the same three colours the dashboard's Low stock card uses (--stock-in / -low / -out). */
    .inv__in, .inv__low, .inv__out { font-weight: var(--weight-semibold); }
    .inv__in { --tone: var(--stock-in); }
    .inv__low { --tone: var(--stock-low); }
    .inv__out { --tone: var(--stock-out); }
    .inv__in, .inv__low, .inv__out {
      color: color-mix(in srgb, var(--tone) 60%, var(--color-text));
      border-color: var(--tone);
      background: color-mix(in srgb, var(--tone) 12%, var(--color-surface));
    }
    .inv__results { margin: 0; padding-inline-start: var(--space-2); display: grid; gap: var(--space-0-5); overflow-wrap: anywhere; }
  `,
})
export class InventoryPage {
  private readonly api = inject(InventoryApi);
  private readonly auth = inject(AdminAuthApi);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly announcer = new Announcer(this.injector);
  protected readonly statuses = INVENTORY_STATUS_FILTERS;
  protected readonly sorts = SORTS;
  protected readonly empty: ReadonlySet<number> = new Set<number>();

  protected readonly access = signal<Access | null>(null);
  protected readonly filters = signal<InventoryFilterOptions | null>(null);
  protected readonly draft = signal<InventoryQuery>(this.initialQuery());
  protected readonly query = signal<InventoryQuery>(this.draft());
  protected readonly page = signal<InventoryListPage | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly selected = signal<ReadonlySet<number>>(this.empty);
  protected readonly editor = signal<Editor | null>(null);
  protected readonly bulkResult = signal<StockChangeResult | null>(null);

  /** Rows by id, so a bulk result can still name a row after the page changed under it. */
  private readonly known = new Map<number, InventoryRow>();
  private trigger: HTMLElement | null = null;
  private requests = 0;

  protected readonly categories = computed(() => {
    const department = this.draft().departmentId;
    return (this.filters()?.categories ?? []).filter((category) => department === null || category.departmentId === department);
  });

  protected readonly sortValue = computed(() => `${this.draft().sort}:${this.draft().direction}`);

  protected readonly editorRow = computed(() => {
    const id = this.editor()?.variantId;
    return id == null ? null : (this.page()?.rows.find((row) => row.variantId === id) ?? null);
  });

  protected readonly selectedRows = computed(() =>
    (this.page()?.rows ?? []).filter((row) => this.selected().has(row.variantId)),
  );

  protected readonly allSelected = computed(() => {
    const rows = (this.page()?.rows ?? []).filter((row) => row.hasStockRecord);
    return rows.length > 0 && rows.every((row) => this.selected().has(row.variantId));
  });

  protected readonly editorTitle = computed(() => {
    const open = this.editor();
    const row = this.editorRow();

    if (open?.mode === 'bulk') {
      return `Change stock for ${this.selected().size} selected`;
    }

    const name = row ? `${row.sku} · ${row.productName}, ${row.colour} / ${row.size}` : '';

    switch (open?.mode) {
      case 'stock':
        return `Change stock: ${name}`;
      case 'threshold':
        return `Low-stock threshold: ${name}`;
      default:
        return `Stock history: ${name}`;
    }
  });

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) => {
        const holds = (permission: string) => session.grantsAllPermissions || session.permissions.includes(permission);
        this.start({ view: holds(INVENTORY_PERMISSIONS.view), edit: holds(INVENTORY_PERMISSIONS.edit) });
      },
      // The session could not be read: let the API decide, as the other screens do.
      error: () => this.start({ view: true, edit: false }),
    });
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';
  }

  protected numberOrNull(value: string): number | null {
    return value === '' ? null : Number(value);
  }

  protected statusLabel(status: InventoryStatusFilter): string {
    return STATUS_LABELS[status];
  }

  protected indent(depth: number): string {
    return '  '.repeat(Math.max(0, depth - 1));
  }

  protected range(answered: InventoryListPage): string {
    return showing(answered.page, answered.pageSize, answered.totalCount);
  }

  protected skuOf(variantId: number): string {
    return this.known.get(variantId)?.sku ?? `Variant ${variantId}`;
  }

  protected notApplied(result: StockChangeResult): readonly StockChangeRowResult[] {
    return result.rows.filter((row) => row.outcome !== 'Applied');
  }

  protected edit(change: Partial<InventoryQuery>): void {
    this.draft.update((current) => ({ ...current, ...change }));
  }

  protected chooseDepartment(event: Event): void {
    const departmentId = this.numberOrNull(this.read(event));
    const category = (this.filters()?.categories ?? []).find((candidate) => candidate.id === this.draft().categoryId);

    this.edit({
      departmentId,
      categoryId: category && departmentId !== null && category.departmentId !== departmentId ? null : this.draft().categoryId,
    });
  }

  protected chooseSort(event: Event): void {
    const [sort, direction] = this.read(event).split(':') as [InventorySort, InventorySortDirection];
    this.edit({ sort, direction });
  }

  protected apply(event: Event): void {
    event.preventDefault();
    this.query.set({ ...this.draft(), page: 1 });
    this.load(1);
  }

  protected clear(): void {
    const fresh: InventoryQuery = { ...this.blankQuery(), status: 'All' };
    this.draft.set(fresh);
    this.query.set(fresh);
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

  protected toggle(variantId: number, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const next = new Set(this.selected());

    if (checked) {
      next.add(variantId);
    } else {
      next.delete(variantId);
    }

    this.selected.set(next);
  }

  protected selectPage(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const rows = (this.page()?.rows ?? []).filter((row) => row.hasStockRecord);
    this.selected.set(checked ? new Set(rows.map((row) => row.variantId)) : this.empty);
  }

  protected open(mode: EditorMode, variantId: number | null, event: Event): void {
    this.trigger = event.currentTarget as HTMLElement | null;
    this.bulkResult.set(null);
    this.editor.set({ mode, variantId });

    afterNextRender(() => this.document.getElementById('inv-editor-title')?.focus(), { injector: this.injector });
  }

  protected close(): void {
    this.editor.set(null);
    this.restoreFocus();
  }

  protected stockAnswered(answer: StockChangeRowResult): void {
    this.patch(answer);

    if (answer.outcome === 'Applied') {
      const row = this.known.get(answer.variantId);
      this.announcer.say(`Saved. ${row?.sku ?? 'The variant'} now has ${answer.onHand} on hand, ${answer.available} available.`);
      this.close();
    } else {
      this.announcer.say(answer.message ?? 'Nothing was changed.');
    }
  }

  protected thresholdSaved(row: InventoryRow): void {
    this.replace(row);
    this.announcer.say(
      row.ownThreshold === null
        ? `Saved. ${row.sku} uses the shop default threshold of ${row.threshold}.`
        : `Saved. ${row.sku} is low at ${row.threshold} available or fewer.`,
    );
    this.close();
  }

  protected bulkApplied(result: StockChangeResult): void {
    for (const answer of result.rows) {
      this.patch(answer);
    }

    this.selected.set(new Set(result.rows.filter((row) => row.outcome !== 'Applied').map((row) => row.variantId)));
    this.bulkResult.set(result);
    this.editor.set(null);
    this.announcer.say(`${result.appliedCount} saved, ${result.notAppliedCount} not changed.`);

    afterNextRender(() => this.document.getElementById('inv-bulk-result')?.focus(), { injector: this.injector });
  }

  load(target: number): void {
    const request = ++this.requests;
    const query = { ...this.query(), page: target };
    this.query.set(query);
    this.problem.set(null);

    this.api.list(query).subscribe({
      next: (answered) => {
        if (request !== this.requests) {
          return;
        }

        for (const row of answered.rows) {
          this.known.set(row.variantId, row);
        }

        this.page.set(answered);
        this.selected.set(this.empty);

        if (this.editor()?.mode !== 'history' || !answered.rows.some((row) => row.variantId === this.editor()?.variantId)) {
          this.editor.set(null);
        }

        this.announcer.say(`${answered.totalCount} ${answered.totalCount === 1 ? 'variant' : 'variants'}. Showing ${this.range(answered)}.`);
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          const failure = toCatalogFailure(error);
          this.page.set(null);
          this.problem.set(failure);
          this.announcer.say(failure.message);
        }
      },
    });
  }

  private start(access: Access): void {
    this.access.set(access);

    if (!access.view) {
      return;
    }

    this.api.filters().subscribe({
      next: (filters) => this.filters.set(filters),
      // The list still works without its filter choices; the selects simply offer "Every …".
      error: () => this.filters.set(null),
    });

    this.load(1);
  }

  /** Puts a change's figures on its row; the status is recomputed from them with the server's rule. */
  private patch(answer: StockChangeRowResult): void {
    const row = this.page()?.rows.find((candidate) => candidate.variantId === answer.variantId);

    if (!row || answer.onHand === null || answer.reserved === null || answer.available === null) {
      return;
    }

    this.replace({
      ...row,
      onHand: answer.onHand,
      reserved: answer.reserved,
      available: answer.available,
      stockStatus: statusOf(answer.available, row.threshold),
    });
  }

  private replace(row: InventoryRow): void {
    this.known.set(row.variantId, row);
    this.page.update((answered) =>
      answered === null
        ? null
        : { ...answered, rows: answered.rows.map((candidate) => (candidate.variantId === row.variantId ? row : candidate)) },
    );
  }

  private restoreFocus(): void {
    const trigger = this.trigger;
    this.trigger = null;

    afterNextRender(
      () => {
        if (trigger?.isConnected) {
          trigger.focus();
        } else {
          this.document.querySelector<HTMLElement>('[data-testid="inventory-said"]')?.focus();
        }
      },
      { injector: this.injector },
    );
  }

  private blankQuery(): InventoryQuery {
    return {
      search: '',
      status: 'All',
      departmentId: null,
      categoryId: null,
      sort: 'Product',
      direction: 'Asc',
      page: 1,
      pageSize: PAGE_SIZE,
    };
  }

  /** `?status=LowStock` from the dashboard's low-stock panel opens the list already filtered. */
  private initialQuery(): InventoryQuery {
    const status = this.route.snapshot.queryParamMap.get('status') as InventoryStatusFilter | null;
    return { ...this.blankQuery(), status: status && INVENTORY_STATUS_FILTERS.includes(status) ? status : 'All' };
  }
}
