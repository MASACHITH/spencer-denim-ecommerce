import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { AdminCustomerApi } from '../../core/customers/admin-customer-api';
import {
  AdminCustomerDetail,
  AdminCustomerOrderPage,
} from '../../core/customers/admin-customer.model';
import { colomboDate, colomboTime, money } from '../../core/orders/order-formatting';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/** How many orders a page of the history holds. */
const ORDER_PAGE_SIZE = 10;

/**
 * One customer (ADM-15, doc 11 P11 step 16: "detail with addresses and order history").
 *
 * **The order history links to the admin order screen rather than rebuilding it.** Each row opens
 * `/orders/{token}`, which needs `Orders.View`; this screen needs only `Customers.View`, so a staff member
 * without the former sees the history and is refused the order itself, by the screen that owns it.
 *
 * **Only the account's own orders are listed** — the server matches on the account, never on the email
 * address, so a guest order under the same address appears only once doc 08 §3.1 D5 has linked it after
 * the customer confirmed the address. The note under the history says so, because "I placed an order
 * before I registered and it isn't there" is a real telephone call.
 *
 * **No delete, no anonymise, no edit.** Anonymisation is doc 11 P17 (`Customers.Delete`), a separate
 * task; ADM-15 does not include editing a customer.
 */
@Component({
  selector: 'sda-customer-detail-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="customer-back"><a class="adm-link" routerLink="/customers">All customers</a></p>

    @if (problem(); as failure) {
      <section class="adm-panel">
        <h1 class="adm-panel__title">Customer</h1>
        <div class="adm-state adm-state--error" role="alert">
          <p data-testid="customer-problem">{{ failure.message }}</p>
          @if (failure.kind !== 'forbidden' && failure.kind !== 'notFound') {
            <div class="adm-actions">
              <button class="adm-btn" type="button" data-testid="customer-retry" (click)="loadCustomer()">
                Try again
              </button>
            </div>
          }
        </div>
      </section>
    } @else if (customer(); as person) {
      <section class="adm-panel">
        <h1 class="adm-panel__title" data-testid="customer-name">{{ fullName() }}</h1>
        @if (!person.emailConfirmed) {
          <p class="adm-state adm-state--warning" data-testid="customer-unconfirmed">
            This customer has not confirmed their email address. Guest orders they placed with it before
            registering are added to their account only once they confirm it.
          </p>
        }
        @if (!person.isActive) {
          <p class="adm-state adm-state--warning">This account is disabled.</p>
        }
        <dl class="customer-facts">
          <dt>Email</dt>
          <dd data-testid="customer-email">{{ person.email }}</dd>
          <dt>Telephone</dt>
          <dd>{{ person.phone ?? 'Not given' }}</dd>
          <dt>Default district</dt>
          <dd data-testid="customer-district">{{ person.districtName ?? 'Not chosen' }}</dd>
          <dt>Registered</dt>
          <dd>{{ day(person.registeredAtUtc) }}</dd>
          <dt>Last signed in</dt>
          <dd>{{ person.lastSignInAtUtc ? time(person.lastSignInAtUtc) : 'Not recorded' }}</dd>
          <dt>Orders</dt>
          <dd data-testid="customer-order-count">{{ person.orderCount }}</dd>
          <dt>Lifetime spend</dt>
          <dd data-testid="customer-spend">{{ cash(person.lifetimeSpend, person.currency) }}</dd>
          <dt>Last order</dt>
          <dd>{{ person.lastOrderAtUtc ? day(person.lastOrderAtUtc) : 'None yet' }}</dd>
        </dl>
        <p class="adm-panel__hint">Lifetime spend is the total of this customer’s paid orders.</p>
      </section>

      <section class="adm-panel" aria-labelledby="customer-addresses-heading">
        <h2 class="adm-panel__title" id="customer-addresses-heading">Saved addresses</h2>
        @if (person.addresses.length === 0) {
          <p class="adm-muted" data-testid="customer-no-addresses">No saved addresses.</p>
        } @else {
          <ul class="customer-list" data-testid="customer-addresses">
            @for (address of person.addresses; track address.addressId) {
              <li class="customer-card" [attr.data-testid]="'address-' + address.addressId">
                <p class="customer-card__title">
                  {{ address.label ?? 'Address' }}
                  @if (address.isDefault) {
                    <span class="adm-pill" data-testid="address-default">Default</span>
                  }
                </p>
                <address class="customer-card__lines">
                  {{ address.recipientName }}<br />
                  {{ address.line1 }}<br />
                  @if (address.line2) {
                    {{ address.line2 }}<br />
                  }
                  {{ address.city }}@if (address.districtName) {, {{ address.districtName }}}@if (address.region) {, {{ address.region }}}
                  @if (address.postalCode) {
                    {{ address.postalCode }}
                  }
                  <br />
                  <span [attr.data-testid]="'address-country-' + address.addressId">{{ address.countryName }}</span><br />
                  @if (address.landmark) {
                    Near {{ address.landmark }}<br />
                  }
                  {{ address.phone }}
                </address>
              </li>
            }
          </ul>
        }
      </section>

      <section class="adm-panel" aria-labelledby="customer-orders-heading">
        <h2 class="adm-panel__title" id="customer-orders-heading">Order history</h2>
        <p class="adm-panel__hint">
          Orders on this account. A guest order placed with the same email address appears here once the
          customer has confirmed that address.
        </p>

        <!-- tabindex="-1": where focus goes when a page change destroys the link that had it (#109). -->
        <p class="adm-muted" role="status" tabindex="-1" data-testid="customer-orders-status">{{ said() }}</p>

        @if (ordersProblem(); as failure) {
          <div class="adm-state adm-state--error" role="alert">
            <p>{{ failure.message }}</p>
            <div class="adm-actions">
              <button class="adm-btn" type="button" data-testid="customer-orders-retry" (click)="reloadOrders()">
                Try again
              </button>
            </div>
          </div>
        } @else if (orders(); as history) {
          @if (history.totalCount === 0) {
            <p class="adm-muted" data-testid="customer-no-orders">No orders on this account yet.</p>
          } @else {
            <ul class="customer-list" data-testid="customer-orders">
              @for (order of history.rows; track order.orderPublicId) {
                <li class="customer-card" [attr.data-testid]="'customer-order-' + order.orderNumber">
                  <a
                    class="customer-card__open"
                    [routerLink]="['/orders', order.orderPublicId]"
                    [attr.data-testid]="'open-order-' + order.orderNumber"
                  >
                    {{ order.orderNumber }}
                  </a>
                  <dl class="customer-facts">
                    <dt>Status</dt>
                    <dd><span class="adm-pill">{{ order.statusLabel }}</span></dd>
                    <dt>Total</dt>
                    <dd>{{ cash(order.grandTotal, order.currency) }}</dd>
                    <dt>Placed</dt>
                    <dd>{{ day(order.placedAtUtc) }}</dd>
                  </dl>
                </li>
              }
            </ul>

            <nav class="adm-actions customer-paging" aria-label="Order pages">
              <button
                class="adm-btn"
                type="button"
                [attr.aria-disabled]="history.page <= 1 ? 'true' : null"
                data-testid="customer-orders-prev"
                (click)="goOrders(history.page - 1)"
              >
                Previous
              </button>
              <p class="adm-muted" data-testid="customer-orders-paging">
                {{ firstOrder() }}–{{ lastOrder() }} of {{ history.totalCount }}
              </p>
              <button
                class="adm-btn"
                type="button"
                [attr.aria-disabled]="lastOrder() >= history.totalCount ? 'true' : null"
                data-testid="customer-orders-next"
                (click)="goOrders(history.page + 1)"
              >
                Next
              </button>
            </nav>
          }
        } @else {
          <p class="adm-muted">Loading orders…</p>
        }
      </section>
    } @else {
      <section class="adm-panel">
        <h1 class="adm-panel__title">Customer</h1>
        <p class="adm-muted">Loading the customer…</p>
      </section>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .customer-back {
      margin: 0;
    }
    .customer-back .adm-link {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
    }
    .customer-facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .customer-facts dt {
      color: var(--color-text-muted);
    }
    .customer-facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .customer-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .customer-card {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .customer-card__title {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      align-items: center;
      margin: 0;
      font-weight: var(--weight-semibold);
    }
    .customer-card__lines {
      font-style: normal;
      font-size: var(--text-small);
      overflow-wrap: anywhere;
    }
    .customer-card__open {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      font-weight: var(--weight-semibold);
      color: inherit;
      overflow-wrap: anywhere;
    }
    .customer-paging {
      align-items: center;
    }
  `,
})
export class CustomerDetailPage {
  private readonly api = inject(AdminCustomerApi);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  private readonly customerId = Number(this.route.snapshot.paramMap.get('customerId'));

  protected readonly customer = signal<AdminCustomerDetail | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  protected readonly orders = signal<AdminCustomerOrderPage | null>(null);
  protected readonly ordersProblem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  protected readonly fullName = computed(() => {
    const person = this.customer();

    if (person === null) {
      return '';
    }

    return person.lastName ? `${person.firstName} ${person.lastName}` : person.firstName;
  });

  protected readonly firstOrder = computed(() => {
    const history = this.orders();
    return history === null || history.totalCount === 0 ? 0 : (history.page - 1) * history.pageSize + 1;
  });

  protected readonly lastOrder = computed(() => {
    const history = this.orders();
    return history === null ? 0 : Math.min(history.page * history.pageSize, history.totalCount);
  });

  constructor() {
    this.loadCustomer();
  }

  protected cash(amount: number, currency: string): string {
    return money(amount, currency);
  }

  protected day(utc: string): string {
    return colomboDate(utc);
  }

  protected time(utc: string): string {
    return colomboTime(utc);
  }

  loadCustomer(): void {
    this.problem.set(null);

    // An id that is not a whole number cannot be a customer; ask nobody.
    if (!Number.isInteger(this.customerId) || this.customerId <= 0) {
      this.problem.set({ kind: 'notFound', message: 'There is no such customer.' });
      return;
    }

    this.api.customer(this.customerId).subscribe({
      next: (person) => {
        this.customer.set(person);
        this.loadOrders(1);
      },
      error: (error: unknown) => {
        const failure = toCatalogFailure(error);
        this.problem.set(
          failure.kind === 'notFound' ? { ...failure, message: 'There is no such customer.' } : failure,
        );
      },
    });
  }

  protected goOrders(page: number): void {
    const history = this.orders();

    // aria-disabled plus this refusal, never [disabled] (#109).
    if (history === null || page < 1 || (page - 1) * history.pageSize >= history.totalCount) {
      return;
    }

    this.loadOrders(page, `Orders page ${page}.`);
  }

  protected reloadOrders(): void {
    this.loadOrders(this.orders()?.page ?? 1);
  }

  private loadOrders(page: number, announcement?: string): void {
    const focused = this.document.activeElement as HTMLElement | null;

    this.ordersProblem.set(null);

    if (announcement !== undefined) {
      this.announce(announcement);
    }

    this.api.orders(this.customerId, page, ORDER_PAGE_SIZE).subscribe({
      next: (history) => {
        this.orders.set(history);
        this.announce(
          history.totalCount === 0
            ? 'No orders on this account yet.'
            : `${history.totalCount} ${history.totalCount === 1 ? 'order' : 'orders'}, newest first. `
              + `Showing ${this.firstOrder()} to ${this.lastOrder()}.`,
        );
        this.rehomeFocusIfLost(focused);
      },
      error: (error: unknown) => {
        const failure = toCatalogFailure(error);
        this.ordersProblem.set(failure);
        this.announce(failure.message);
        this.rehomeFocusIfLost(focused);
      },
    });
  }

  /** The order list's rule: only if the element that had focus is gone; `afterNextRender`, never a microtask. */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="customer-orders-status"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
