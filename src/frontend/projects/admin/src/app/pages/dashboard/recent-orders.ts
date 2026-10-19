import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
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
 *
 * **Laid out as the client demo's Recent Orders table** (#186), with the status as a badge in the status's own
 * colour. The demo's Items and District columns are left out: the order list does not return either, and the
 * dashboard does not invent them.
 */
@Component({
  selector: 'sda-recent-orders',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="adm-card__hd">
      <div>
        <h2 class="adm-card__title" id="dash-recent">Recent orders</h2>
        <p class="adm-card__sub">Newest first, paid or not</p>
      </div>
      <form class="recent__search" role="search" aria-label="Search orders" (submit)="search($event)">
        <label class="adm-field">
          <span class="adm-label">Order number, email or telephone</span>
          <input class="adm-input" type="search" name="search" autocomplete="off" maxlength="100" data-testid="recent-search" [value]="term()" (input)="term.set(value($event))" />
        </label>
        <button class="adm-btn" type="submit" data-testid="recent-search-submit">Search</button>
        <a class="adm-link" routerLink="/orders" data-testid="recent-all">View all orders</a>
      </form>
    </div>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="recent-problem">
        <p>{{ failure.message }}</p>
        <div class="adm-actions"><button class="adm-btn" type="button" (click)="load()">Try again</button></div>
      </div>
    } @else if (rows(); as answered) {
      @if (answered.length === 0) {
        <p class="adm-muted" data-testid="recent-empty">{{ searched() ? 'No order matches that search.' : 'No orders have been placed yet.' }}</p>
      } @else {
        <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Recent orders">
          <table class="adm-table" data-testid="recent-orders">
            <caption class="sr-only">{{ searched() ? 'Orders matching the search, newest first' : 'The ten newest orders' }}</caption>
            <thead>
              <tr>
                <th scope="col">Order</th>
                <th scope="col">Customer</th>
                <th scope="col">Placed</th>
                <th scope="col" class="adm-num">Total</th>
                <th scope="col">Payment</th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              @for (row of answered; track row.orderPublicId) {
                <tr>
                  <th scope="row"><a class="adm-link recent__number" [routerLink]="['/orders', row.orderPublicId]">{{ row.orderNumber }}</a></th>
                  <td class="recent__who"><span>{{ row.customerName }}</span><span class="recent__sub">{{ row.customerEmail }}</span></td>
                  <td class="recent__when">{{ when(row.placedAtUtc) }}</td>
                  <td class="adm-num">{{ total(row) }}</td>
                  <td>{{ payment(row.paymentProviderCode) }}</td>
                  <td><span class="adm-badge" [attr.data-status]="row.status">{{ row.statusLabel }}</span></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    } @else {
      <p class="adm-muted">Loading orders…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-2); min-width: 0; }
    .recent__search { display: flex; flex-wrap: wrap; align-items: end; gap: var(--space-1); min-width: 0; }
    .recent__search .adm-field { flex: 1 1 14rem; }
    .recent__number { font-variant-numeric: tabular-nums; white-space: nowrap; }
    .recent__who { min-width: 10rem; overflow-wrap: anywhere; }
    .recent__who span { display: block; }
    .recent__sub { font-size: var(--text-label); color: var(--color-text-muted); }
    .recent__when { white-space: nowrap; }
    td, th { vertical-align: middle; }
  `,
})
export class RecentOrders {
  /** Payment method names by code, from the dashboard's own payment breakdown; the code when it has none. */
  readonly paymentNames = input<Readonly<Record<string, string>>>({});

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

  protected payment(code: string): string {
    return this.paymentNames()[code] ?? code;
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
