import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { money } from '../../core/orders/order-formatting';
import { PeriodQuery, ReportingApi } from '../../core/reporting/reporting-api';
import {
  PERIOD_PRESETS,
  PeriodPreset,
  addDays,
  colomboToday,
  count,
  formatPeriod,
  presetPeriod,
} from '../../core/reporting/reporting-format';
import { AdminDashboard, REPORTING_PERMISSIONS } from '../../core/reporting/reporting.model';
import { BarRow, BarTable } from './bar-table';
import { KpiGrid } from './kpi-grid';
import { LowStockPanel } from './low-stock-panel';
import { RecentOrders } from './recent-orders';
import { SalesTrendChart } from './sales-trend';

interface Access {
  readonly dashboard: boolean;
  readonly orders: boolean;
  readonly reports: boolean;
}

/**
 * The dashboard (ADM-04, ADM-05, ADM-07, ADM-08; doc 11 P16 steps 1–2).
 *
 * **What is on it, and what deliberately is not.** KPIs against the previous period, the twelve-month trend,
 * sales by department, top categories and products, order status, payment methods, buyers by delivery
 * district, recent orders and low stock. **Not traffic sources (ADM-06)** and no conversion rate: both need
 * the `analytics` tables, which do not exist, and a chart of zeros would read as "no visitors". The alert
 * banners are the shell's (`AlertBanner`, W4-02a), above this page as above every admin page — the
 * dashboard does not render a second one.
 *
 * **Every widget is a table with figures** (the bars are decoration on the table) or, for the trend, a chart
 * with a table one tap away, so nothing is only a picture.
 *
 * **The role decides what is offered** (the API enforces the same): without `Dashboard.View` nothing is
 * fetched; recent orders appear only with `Orders.View`; the Reports link only with `Reports.View`.
 */
@Component({
  selector: 'sda-dashboard-page',
  imports: [RouterLink, KpiGrid, BarTable, SalesTrendChart, RecentOrders, LowStockPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="dash">
      <h1 class="dash__title">Dashboard</h1>

      <!-- One live region, outside every block an update replaces. tabindex="-1" so focus can land here. -->
      <p class="sr-only" role="status" tabindex="-1" data-testid="dashboard-status">{{ said() }}</p>

      @if (access() === null) {
        <p class="adm-muted">Loading…</p>
      } @else if (!access()!.dashboard) {
        <p class="adm-state" data-testid="dashboard-forbidden">
          Your role does not include the dashboard figures. The sections you can open are listed below.
        </p>
      } @else {
        <form class="dash__period adm-panel" aria-label="Period" (submit)="applyCustom($event)">
          <label class="adm-field">
            <span class="adm-label">Period</span>
            <select class="adm-input" name="preset" data-testid="dashboard-preset" (change)="choosePreset($event)">
              @for (option of presets; track option.value) {
                <option [value]="option.value" [selected]="preset() === option.value">{{ option.label }}</option>
              }
            </select>
          </label>
          @if (preset() === 'custom') {
            <label class="adm-field">
              <span class="adm-label">From</span>
              <input class="adm-input" type="date" name="from" data-testid="dashboard-from" [value]="customFrom()" (change)="edit('from', $event)" />
            </label>
            <label class="adm-field">
              <span class="adm-label">To</span>
              <input class="adm-input" type="date" name="to" data-testid="dashboard-to" [value]="customTo()" (change)="edit('to', $event)" />
            </label>
            <div class="adm-actions">
              <button class="adm-btn--primary" type="submit" data-testid="dashboard-apply">Show</button>
            </div>
          }
          @if (formError()) {
            <p class="adm-field-error dash__wide" data-testid="dashboard-period-error">{{ formError() }}</p>
          }
          <p class="adm-panel__hint dash__wide" data-testid="dashboard-period">
            {{ comparedWith() || 'Days are Colombo days, both ends included.' }}
          </p>
        </form>

        @if (problem(); as failure) {
          <div class="adm-state adm-state--error" data-testid="dashboard-problem">
            <p>{{ failure.message }}</p>
            @if (failure.kind !== 'forbidden') {
              <div class="adm-actions"><button class="adm-btn" type="button" (click)="reload()">Try again</button></div>
            }
          </div>
        }

        <section class="adm-panel" aria-labelledby="dash-kpis">
          <h2 class="adm-panel__title" id="dash-kpis">Key figures</h2>
          @if (dashboard(); as answered) {
            <sda-kpi-grid [kpis]="answered.kpis" [currency]="answered.currency" />
          } @else if (!problem()) {
            <p class="adm-muted">Loading figures…</p>
          }
        </section>

        <div class="dash__grid">
          <section class="adm-panel dash__wide" aria-labelledby="dash-trend">
            <h2 class="adm-panel__title" id="dash-trend">Revenue and orders, twelve months</h2>
            <sda-sales-trend [to]="period().to" />
          </section>

          @if (dashboard(); as answered) {
            <section class="adm-panel" aria-labelledby="dash-departments">
              <h2 class="adm-panel__title" id="dash-departments">Sales by department</h2>
              <p class="adm-panel__hint">
                Item sales after promo codes, on orders paid in these days, by the department each item was bought
                under. Delivery is left out, so these add up to less than gross revenue; orders refunded later are
                still counted.
              </p>
              <sda-bar-table caption="Sales by department" testId="dashboard-departments" [headers]="['Department', 'Units', 'Sales', 'Share']" [rows]="departments()" />
            </section>

            <section class="adm-panel" aria-labelledby="dash-categories">
              <h2 class="adm-panel__title" id="dash-categories">Top categories</h2>
              @if (categories().length === 0) {
                <p class="adm-muted">Nothing was sold in these days.</p>
              } @else {
                <sda-bar-table caption="Top categories by sales" testId="dashboard-categories" [headers]="['Category', 'Units', 'Sales', 'Share']" [rows]="categories()" />
              }
            </section>

            <section class="adm-panel dash__wide" aria-labelledby="dash-products">
              <h2 class="adm-panel__title" id="dash-products">Top products</h2>
              <p class="adm-panel__hint">Item sales after promo codes, against the previous period.</p>
              @if (products().length === 0) {
                <p class="adm-muted">Nothing was sold in these days.</p>
              } @else {
                <sda-bar-table caption="Top products by sales, with the previous period" testId="dashboard-products" [headers]="['Product', 'Units', 'Sales', 'Previous period', 'Change']" [rows]="products()" />
              }
            </section>

            <section class="adm-panel" aria-labelledby="dash-statuses">
              <h2 class="adm-panel__title" id="dash-statuses">Order status</h2>
              <p class="adm-panel__hint">Orders placed in these days, by where they are now.</p>
              <sda-bar-table caption="Orders placed in the period, by status now" testId="dashboard-statuses" [headers]="['Status', 'Orders']" [rows]="statuses()" />
            </section>

            <section class="adm-panel" aria-labelledby="dash-methods">
              <h2 class="adm-panel__title" id="dash-methods">Payment methods</h2>
              <p class="adm-panel__hint">Orders paid in these days.</p>
              <sda-bar-table caption="Paid orders by payment method" testId="dashboard-methods" [headers]="['Method', 'Paid orders', 'Revenue']" [rows]="methods()" />
            </section>

            <section class="adm-panel dash__wide" aria-labelledby="dash-districts">
              <h2 class="adm-panel__title" id="dash-districts">Buyers by delivery district</h2>
              <p class="adm-panel__hint" data-testid="dashboard-buyers">
                {{ number(answered.buyersByDistrict.distinctBuyers) }}
                {{ answered.buyersByDistrict.distinctBuyers === 1 ? 'buyer' : 'buyers' }} (different email addresses) paid
                in these days. Each district counts the buyers who had a paid order <strong>delivered</strong> there — not
                the default district on a customer account — so a buyer who used two districts is in both.
              </p>
              @if (districts().length > 0) {
                <sda-bar-table caption="Buyers by delivery district" testId="dashboard-districts" [headers]="['District', 'Buyers', 'Paid orders', 'Revenue']" [rows]="districts()" />
              }
            </section>
          }

          @if (access()!.orders) {
            <section class="adm-panel" aria-labelledby="dash-recent">
              <h2 class="adm-panel__title" id="dash-recent">Recent orders</h2>
              <sda-recent-orders />
            </section>
          }

          <section class="adm-panel" aria-labelledby="dash-low">
            <h2 class="adm-panel__title" id="dash-low">Low stock</h2>
            <sda-low-stock [canSeeReports]="access()!.reports" />
          </section>
        </div>
      }

      <nav class="adm-panel" aria-labelledby="dash-sections">
        <h2 class="adm-panel__title" id="dash-sections">Admin sections</h2>
        <div class="dash__links">
          <a class="adm-link" routerLink="/orders">Orders</a>
          <a class="adm-link" routerLink="/orders/bank-transfers">Bank transfers</a>
          <a class="adm-link" routerLink="/orders/couriers">Couriers</a>
          <a class="adm-link" routerLink="/customers">Customers</a>
          <a class="adm-link" routerLink="/products">Products</a>
          <a class="adm-link" routerLink="/catalog">Departments and categories</a>
          <a class="adm-link" routerLink="/marketing/promo-codes">Promo codes</a>
          <a class="adm-link" routerLink="/marketing/sale-campaigns">Sale campaigns</a>
          <a class="adm-link" routerLink="/marketing/newsletter">Newsletter subscribers</a>
          @if (access()?.reports) {
            <a class="adm-link" routerLink="/reports" data-testid="dashboard-reports-link">Reports</a>
          }
          <a class="adm-link" routerLink="/settings">Settings</a>
          <a class="adm-link" routerLink="/audit-log">Audit log</a>
          <a class="adm-link" routerLink="/users">Staff users</a>
          <a class="adm-link" routerLink="/roles">Roles</a>
        </div>
        <div class="adm-actions">
          <button class="adm-btn" type="button" data-testid="dashboard-logout" [attr.aria-disabled]="loggingOut() ? 'true' : null" (click)="logout()">
            {{ loggingOut() ? 'Signing out…' : 'Sign out' }}
          </button>
        </div>
      </nav>
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .dash { display: grid; gap: var(--space-3); max-width: 1400px; margin-inline: auto; min-width: 0; }
    .dash__title { font-size: var(--text-h2); }
    .dash__period { grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr)); align-items: end; }
    .dash__wide { grid-column: 1 / -1; margin: 0; }
    .dash__grid { display: grid; gap: var(--space-3); grid-template-columns: minmax(0, 1fr); }
    .dash__grid > * { min-width: 0; align-content: start; }
    .dash__links { display: flex; flex-wrap: wrap; gap: 0 var(--space-2); }
    @media (min-width: 1024px) {
      .dash__grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    }
  `,
})
export class DashboardPage {
  private readonly api = inject(ReportingApi);
  private readonly auth = inject(AdminAuthApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  protected readonly presets = PERIOD_PRESETS;
  protected readonly preset = signal<PeriodPreset>('last30');
  protected readonly customFrom = signal('');
  protected readonly customTo = signal('');
  protected readonly formError = signal<string | null>(null);

  /** The period on screen. Starts as the server's own default: the last 30 days including today. */
  protected readonly period = signal<PeriodQuery>(presetPeriod('last30', colomboToday()));

  protected readonly access = signal<Access | null>(null);
  protected readonly dashboard = signal<AdminDashboard | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');
  protected readonly loggingOut = signal(false);

  protected readonly comparedWith = computed(() => {
    const answered = this.dashboard();
    return answered
      ? `${formatPeriod(answered.period)} (${answered.period.days} ${answered.period.days === 1 ? 'day' : 'days'}), compared with ${formatPeriod(answered.previousPeriod)}.`
      : '';
  });

  protected readonly departments = computed(() =>
    (this.dashboard()?.departments ?? []).map((row) => this.shareRow(row.id, row.name, row.units, row.revenue, row.sharePercent)),
  );

  protected readonly categories = computed(() =>
    (this.dashboard()?.topCategories ?? []).map((row) => this.shareRow(row.id, row.name, row.units, row.revenue, row.sharePercent)),
  );

  protected readonly products = computed<readonly BarRow[]>(() =>
    (this.dashboard()?.topProducts ?? []).map((row) => ({
      key: row.productId,
      label: row.productCode ? `${row.name} (${row.productCode})` : row.name,
      cells: [count(row.units), this.rupees(row.revenue.current ?? 0), this.rupees(row.revenue.previous ?? 0), this.percent(row.revenue.percentChange, row.revenue.change)],
      value: row.revenue.current ?? 0,
    })),
  );

  protected readonly statuses = computed<readonly BarRow[]>(() =>
    (this.dashboard()?.orderStatuses ?? []).map((row) => ({ key: row.status, label: row.label, cells: [count(row.count)], value: row.count })),
  );

  protected readonly methods = computed<readonly BarRow[]>(() =>
    (this.dashboard()?.paymentMethods ?? []).map((row) => ({
      key: row.code,
      label: row.name,
      cells: [count(row.paidOrders), this.rupees(row.revenue)],
      value: row.revenue,
    })),
  );

  protected readonly districts = computed<readonly BarRow[]>(() =>
    (this.dashboard()?.buyersByDistrict.districts ?? []).map((row) => ({
      key: row.districtId,
      label: row.name,
      cells: [count(row.buyers), count(row.paidOrders), this.rupees(row.revenue)],
      value: row.buyers,
    })),
  );

  private requests = 0;

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) => {
        const holds = (permission: string) => session.grantsAllPermissions || session.permissions.includes(permission);
        const access: Access = {
          dashboard: holds(REPORTING_PERMISSIONS.dashboard),
          orders: holds(REPORTING_PERMISSIONS.orders),
          reports: holds(REPORTING_PERMISSIONS.reports),
        };

        this.access.set(access);

        if (access.dashboard) {
          this.load();
        }
      },
      // The session could not be read: show the widgets and let the API decide, as the other screens do.
      error: () => {
        this.access.set({ dashboard: true, orders: true, reports: true });
        this.load();
      },
    });
  }

  protected choosePreset(event: Event): void {
    const value = (event.target as HTMLSelectElement | null)?.value as PeriodPreset | undefined;

    if (!value) {
      return;
    }

    this.preset.set(value);
    this.formError.set(null);

    if (value === 'custom') {
      this.customFrom.set(this.period().from);
      this.customTo.set(this.period().to);
      return;
    }

    this.period.set(presetPeriod(value, colomboToday()));
    this.load();
  }

  protected edit(field: 'from' | 'to', event: Event): void {
    const value = (event.target as HTMLInputElement | null)?.value ?? '';
    (field === 'from' ? this.customFrom : this.customTo).set(value);
  }

  protected applyCustom(event: Event): void {
    event.preventDefault();

    const from = this.customFrom();
    const to = this.customTo();
    const problem = !from || !to
      ? 'Choose both a start and an end date.'
      : from > to
        ? 'The end date is before the start date.'
        : addDays(from, 365) < to
          ? 'A period can be at most 366 days long.'
          : null;

    this.formError.set(problem);

    if (problem) {
      this.announce(problem);
      return;
    }

    this.period.set({ from, to });
    this.load();
  }

  protected reload(): void {
    this.load();
  }

  protected rupees(amount: number): string {
    return money(amount, this.dashboard()?.currency ?? 'LKR');
  }

  protected number(value: number): string {
    return count(value);
  }

  protected logout(): void {
    // aria-disabled plus this refusal, never [disabled]: a disabled button throws focus to <body> (#109).
    if (this.loggingOut()) {
      return;
    }

    this.loggingOut.set(true);
    const leave = () => {
      this.loggingOut.set(false);
      void this.router.navigateByUrl('/login');
    };

    this.auth.logout().subscribe({ next: leave, error: leave });
  }

  private load(): void {
    const request = ++this.requests;

    this.problem.set(null);
    this.dashboard.set(null);
    this.announce('Loading the dashboard…');

    this.api.dashboard(this.period()).subscribe({
      next: (answered) => {
        if (request !== this.requests) {
          return;
        }

        this.dashboard.set(answered);
        this.announce(`Showing ${this.comparedWith()}`);
      },
      error: (error: unknown) => {
        if (request !== this.requests) {
          return;
        }

        const failure = toCatalogFailure(error);
        this.problem.set(failure);
        this.announce(failure.message);
      },
    });
  }

  private shareRow(id: number, name: string, units: number, revenue: number, share: number): BarRow {
    return {
      key: id,
      label: name,
      cells: [count(units), this.rupees(revenue), `${share.toLocaleString('en-LK', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`],
      value: revenue,
    };
  }

  private percent(percentChange: number | null, change: number | null): string {
    if (change === null || change === 0) {
      return 'No change';
    }

    if (percentChange === null) {
      return 'New';
    }

    return `${percentChange > 0 ? '+' : '−'}${Math.abs(percentChange).toLocaleString('en-LK', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
  }

  /** The one live region; never inside a block an update removes (a message written there is not heard). */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
