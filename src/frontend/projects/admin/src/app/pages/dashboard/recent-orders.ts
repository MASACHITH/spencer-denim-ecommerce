import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { AdminOrderApi } from '../../core/orders/admin-order-api';
import { AdminOrderListRow } from '../../core/orders/admin-order.model';
import { colomboTime, money } from '../../core/orders/order-formatting';

const SHOWN = 10;

/**
 * Searchable recent orders (ADM-08; doc 11 P16 "Search by number, email, phone").
 *
 * **It is the admin order list, not a copy of it**: the same endpoint (`GET /api/admin/orders`, newest first,
 * ten rows), the same search classification, and each row links to the existing order screen at
 * `/orders/{token}`. The dashboard shows this widget only to a role holding `Orders.View`, because that is
 * the permission the list and the order screen both need — a widget that always answered 403 would be a
 * defect, however right the refusal.
 */
@Component({
  selector: 'sda-recent-orders',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="recent__search" role="search" aria-label="Search orders" (submit)="search($event)">
      <label class="adm-field">
        <span class="adm-label">Order number, email or telephone</span>
        <input class="adm-input" type="search" name="search" autocomplete="off" maxlength="100" data-testid="recent-search" [value]="term()" (input)="term.set(value($event))" />
      </label>
      <div class="adm-actions">
        <button class="adm-btn" type="submit" data-testid="recent-search-submit">Search</button>
        <a class="adm-link" routerLink="/orders">All orders</a>
      </div>
    </form>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="recent-problem">
        <p>{{ failure.message }}</p>
        <div class="adm-actions"><button class="adm-btn" type="button" (click)="load()">Try again</button></div>
      </div>
    } @else if (rows(); as answered) {
      @if (answered.length === 0) {
        <p class="adm-muted" data-testid="recent-empty">{{ searched() ? 'No order matches that search.' : 'No orders have been placed yet.' }}</p>
      } @else {
        <ul class="recent__list" data-testid="recent-orders">
          @for (row of answered; track row.orderPublicId) {
            <li class="recent__row">
              <a class="adm-link recent__number" [routerLink]="['/orders', row.orderPublicId]">{{ row.orderNumber }}</a>
              <span class="recent__who">{{ row.customerName }}</span>
              <span class="adm-pill">{{ row.statusLabel }}</span>
              <span class="recent__money">{{ total(row) }}</span>
              <span class="adm-muted recent__when">{{ when(row.placedAtUtc) }}</span>
            </li>
          }
        </ul>
      }
    } @else {
      <p class="adm-muted">Loading orders…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-2); min-width: 0; }
    .recent__search { display: grid; gap: var(--space-1); }
    .recent__list { display: grid; gap: 0; margin: 0; padding: 0; list-style: none; }
    .recent__row {
      display: flex; flex-wrap: wrap; align-items: center; gap: 0 var(--space-2);
      padding-block: var(--space-0-5); border-bottom: 1px solid var(--color-border); min-width: 0;
    }
    .recent__number { font-weight: var(--weight-semibold); }
    .recent__who { flex: 1 1 8rem; min-width: 0; overflow-wrap: anywhere; }
    .recent__money { font-variant-numeric: tabular-nums; }
    .recent__when { font-size: var(--text-small); }
  `,
})
export class RecentOrders {
  private readonly api = inject(AdminOrderApi);

  protected readonly term = signal('');
  protected readonly searched = signal(false);
  protected readonly rows = signal<readonly AdminOrderListRow[] | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  private requests = 0;

  constructor() {
    this.load();
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement | null)?.value ?? '';
  }

  protected search(event: Event): void {
    event.preventDefault();
    this.load();
  }

  protected total(row: AdminOrderListRow): string {
    return money(row.grandTotal, row.currency);
  }

  protected when(utc: string): string {
    return colomboTime(utc);
  }

  load(): void {
    const request = ++this.requests;
    const search = this.term().trim();

    this.problem.set(null);
    this.rows.set(null);

    this.api.list({ search: search || null, sort: 'Newest', page: 1, pageSize: SHOWN }).subscribe({
      next: (page) => {
        if (request === this.requests) {
          this.searched.set(search.length > 0);
          this.rows.set(page.rows);
        }
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          this.problem.set(toCatalogFailure(error));
        }
      },
    });
  }
}
