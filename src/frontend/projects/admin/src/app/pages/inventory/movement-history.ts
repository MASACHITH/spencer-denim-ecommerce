import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { InventoryApi } from '../../core/inventory/inventory-api';
import { InventoryHistoryPage, InventoryMovementRow, MOVEMENT_LABELS } from '../../core/inventory/inventory.model';
import { colomboTime } from '../../core/orders/order-formatting';

const PAGE_SIZE = 20;

/**
 * One variant's movement history, newest first (doc 11 P2 step 4: type, change, resulting on hand and
 * reserved, reason, who, when). Manual changes name the staff member; the automatic ones name the order.
 */
@Component({
  selector: 'sda-movement-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (problem(); as failure) {
      <p class="adm-state adm-state--error" role="alert" data-testid="history-problem">{{ failure.message }}</p>
    } @else if (history(); as answered) {
      @if (answered.totalCount === 0) {
        <p class="adm-muted" data-testid="history-empty">
          No stock movements are recorded for this variant yet. Changes made on this screen, and orders placed, paid,
          expired or cancelled from now on, will appear here.
        </p>
      } @else {
        <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Stock movements">
          <table class="adm-table" data-testid="history-table">
            <caption class="sr-only">Stock movements, newest first</caption>
            <thead>
              <tr>
                <th scope="col">When</th>
                <th scope="col">What</th>
                <th scope="col" class="adm-num">On hand</th>
                <th scope="col" class="adm-num">Reserved</th>
                <th scope="col">Reason</th>
                <th scope="col">By</th>
              </tr>
            </thead>
            <tbody>
              @for (row of answered.rows; track row.movementId) {
                <tr [attr.data-testid]="'movement-' + row.movementId">
                  <td>{{ when(row.createdAtUtc) }}</td>
                  <th scope="row">{{ label(row) }}</th>
                  <td class="adm-num">{{ signed(row.onHandDelta) }} → {{ row.onHandAfter }}</td>
                  <td class="adm-num">{{ signed(row.reservedDelta) }} → {{ row.reservedAfter }}</td>
                  <td class="hist__wrap">{{ row.reason ?? '' }}</td>
                  <td class="hist__wrap">{{ row.createdBy ?? 'The shop' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <nav class="adm-actions hist__paging" aria-label="History pages">
          <button class="adm-btn" type="button" data-testid="history-newer" [attr.aria-disabled]="answered.page <= 1 ? 'true' : null" (click)="go(answered.page - 1)">Newer</button>
          <p class="adm-muted" data-testid="history-paging">{{ range(answered) }}</p>
          <button class="adm-btn" type="button" data-testid="history-older" [attr.aria-disabled]="answered.page * answered.pageSize >= answered.totalCount ? 'true' : null" (click)="go(answered.page + 1)">Older</button>
        </nav>
      }
    } @else {
      <p class="adm-muted">Loading history…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-1); min-width: 0; }
    .adm-table-scroll { position: relative; }
    .hist__wrap { overflow-wrap: anywhere; min-width: 8rem; }
    .hist__paging { align-items: center; }
  `,
})
export class MovementHistory implements OnInit {
  readonly variantId = input.required<number>();

  private readonly api = inject(InventoryApi);

  protected readonly history = signal<InventoryHistoryPage | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  ngOnInit(): void {
    this.load(1);
  }

  protected when(utc: string): string {
    return colomboTime(utc);
  }

  protected label(row: InventoryMovementRow): string {
    const label = MOVEMENT_LABELS[row.movementType] ?? row.movementType;
    return row.orderNumber ? `${label}, order ${row.orderNumber}` : label;
  }

  protected signed(value: number): string {
    return value > 0 ? `+${value}` : String(value);
  }

  protected range(page: InventoryHistoryPage): string {
    const first = (page.page - 1) * page.pageSize + 1;
    return `${first}–${Math.min(page.page * page.pageSize, page.totalCount)} of ${page.totalCount}`;
  }

  protected go(target: number): void {
    const page = this.history();

    if (page === null || target < 1 || (target - 1) * page.pageSize >= page.totalCount) {
      return;
    }

    this.load(target);
  }

  private load(page: number): void {
    this.problem.set(null);

    this.api.history(this.variantId(), page, PAGE_SIZE).subscribe({
      next: (answered) => this.history.set(answered),
      error: (error: unknown) => this.problem.set(toCatalogFailure(error)),
    });
  }
}
