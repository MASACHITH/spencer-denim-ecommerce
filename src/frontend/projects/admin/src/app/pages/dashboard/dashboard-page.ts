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
import { AdminDashboard, ComparedFigure, REPORTING_PERMISSIONS } from '../../core/reporting/reporting.model';
import { ChartTable, ChartView } from '../../core/charts/chart';
import { BarSpec, DoughnutSpec, compactRupees, shareText, sharesOf } from '../../core/charts/chart-config';
import { STATUS_TOKENS } from '../../core/charts/chart-palette';
import { direction } from '../../core/reporting/reporting-format';
import { AlertStrip } from './alert-strip';
import { KpiGrid, tone } from './kpi-grid';
import { TodayBanner } from './today-banner';
import { LowStockPanel } from './low-stock-panel';
import { RecentOrders } from './recent-orders';
import { SalesTrendChart } from './sales-trend';
import { TrafficSourcesPanel } from './traffic-sources';
import { TRAFFIC_PERMISSION } from '../../core/traffic/traffic.model';
import { INVENTORY_PERMISSIONS } from '../../core/inventory/inventory.model';
import { REVIEW_PERMISSIONS } from '../../core/reviews/reviews.model';

interface Access {
  readonly dashboard: boolean;
  readonly orders: boolean;
  readonly reports: boolean;
  readonly traffic: boolean;
  readonly inventory: boolean;
  readonly reviews: boolean;
}

/**
 * The dashboard (ADM-04, ADM-05, ADM-07, ADM-08; doc 11 P16 steps 1–2).
 *
 * **What is on it, and what deliberately is not.** KPIs against the previous period, the twelve-month trend,
 * sales by department, top categories and products, order status, payment methods, buyers by delivery
 * district, recent orders, low stock, and — for a role with `Traffic.View` — traffic sources (ADM-06) with the
 * conversion rate (W4-03), which needed the `analytics` tables and so arrived after the rest. The alert
 * banners are the shell's (`AlertBanner`, W4-02a), above this page as above every admin page — the
 * dashboard does not render a second one.
 *
 * **Laid out as the client demo's dashboard** (#186): the Today banner and the alert strip, the KPI cards, then
 * Revenue &amp; Orders beside Sales by department, three small doughnuts (order status, traffic sources, payment
 * methods), top categories as horizontal bars, recent orders beside top products, and low stock beside the
 * customer overview. The demo's sections our API has no figures for are left out, not invented; each component
 * says which.
 *
 * **Nothing is only a picture**: every chart is `role="img"` with a one-sentence summary, a text legend, and its
 * figures as a table under "Show the figures as a table" (`ChartView`).
 *
 * **The role decides what is offered** (the API enforces the same): without `Dashboard.View` nothing is
 * fetched; recent orders appear only with `Orders.View`; the Reports link only with `Reports.View`.
 */
@Component({
  selector: 'sda-dashboard-page',
  imports: [RouterLink, KpiGrid, ChartView, SalesTrendChart, RecentOrders, LowStockPanel, TrafficSourcesPanel, TodayBanner, AlertStrip],
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
        <sda-today-banner [canSeeTraffic]="access()!.traffic" />
        <sda-alert-strip [canSeeOrders]="access()!.orders" [canSeeReports]="access()!.reports" />

        <form class="dash__period adm-card" aria-label="Period" (submit)="applyCustom($event)">
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

        <section class="dash__section" aria-labelledby="dash-kpis">
          <div class="dash__section-hd">
            <h2 class="dash__section-title" id="dash-kpis">Key figures</h2>
            <span class="adm-card__sub">against the previous period</span>
          </div>
          @if (dashboard(); as answered) {
            <sda-kpi-grid [kpis]="answered.kpis" [currency]="answered.currency" />
          } @else if (!problem()) {
            <p class="adm-muted">Loading figures…</p>
          }
        </section>

        <div class="dash__row dash__row--two">
          <section class="adm-card" aria-labelledby="dash-trend">
            <sda-sales-trend [to]="period().to" />
          </section>

          <section class="adm-card" aria-labelledby="dash-departments">
            <div>
              <h2 class="adm-card__title" id="dash-departments">Sales by department</h2>
              <p class="adm-card__sub">Share of item sales, orders paid in these days</p>
            </div>
            @if (dashboard()) {
              <sda-chart testId="dashboard-departments-chart" [spec]="departmentChart()" [summary]="departmentSummary()" [height]="200" [table]="departmentTable()" />
              <p class="adm-panel__hint">
                Item sales after promo codes, by the department each item was bought under. Delivery is left out, so
                these add up to less than gross revenue; orders refunded later are still counted.
              </p>
            } @else if (!problem()) {
              <p class="adm-muted">Loading…</p>
            }
          </section>
        </div>

        <div class="dash__row dash__row--three">
            <section class="adm-card" aria-labelledby="dash-statuses">
              <div>
                <h2 class="adm-card__title" id="dash-statuses">Order status</h2>
                <p class="adm-card__sub">Orders placed in these days, by where they are now</p>
              </div>
              @if (dashboard()) {
                <sda-chart testId="dashboard-statuses-chart" [spec]="statusChart()" [summary]="statusSummary()" [height]="160" [table]="statusTable()" emptyText="No orders were placed in these days." />
              } @else if (!problem()) {
                <p class="adm-muted">Loading…</p>
              }
            </section>

            @if (access()!.traffic) {
              <section class="adm-card" aria-labelledby="dash-traffic">
                <div>
                  <h2 class="adm-card__title" id="dash-traffic">Traffic sources</h2>
                  <p class="adm-card__sub">Where visits came from</p>
                </div>
                <sda-traffic-sources [period]="period()" />
              </section>
            }

            <section class="adm-card" aria-labelledby="dash-methods">
              <div>
                <h2 class="adm-card__title" id="dash-methods">Payment methods</h2>
                <p class="adm-card__sub">Orders paid in these days, by how they were paid</p>
              </div>
              @if (dashboard()) {
                <sda-chart testId="dashboard-methods-chart" [spec]="methodChart()" [summary]="methodSummary()" [height]="160" [table]="methodTable()" emptyText="No orders were paid in these days." />
              } @else if (!problem()) {
                <p class="adm-muted">Loading…</p>
              }
            </section>
          </div>

        @if (dashboard()) {

          <section class="adm-card" aria-labelledby="dash-categories">
            <div>
              <h2 class="adm-card__title" id="dash-categories">Top categories by sales</h2>
              <p class="adm-card__sub">Item sales after promo codes, orders paid in these days</p>
            </div>
            <sda-chart testId="dashboard-categories-chart" [spec]="categoryChart()" [summary]="categorySummary()" [height]="categoryHeight()" [table]="categoryTable()" />
          </section>
        }

        <div class="dash__row dash__row--tables">
          @if (access()!.orders) {
            <section class="adm-card" aria-labelledby="dash-recent">
              <sda-recent-orders [paymentNames]="paymentNames()" />
            </section>
          }

          @if (dashboard(); as answered) {
            <section class="adm-card" aria-labelledby="dash-products">
              <div>
                <h2 class="adm-card__title" id="dash-products">Top products</h2>
                <p class="adm-card__sub">Item sales after promo codes, against the previous period</p>
              </div>
              @if (answered.topProducts.length === 0) {
                <p class="adm-muted">No sales in this period.</p>
              } @else {
                <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Top products">
                  <table class="adm-table" data-testid="dashboard-products">
                    <caption class="sr-only">Top products by sales, with the change on the previous period</caption>
                    <thead>
                      <tr>
                        <th scope="col">Product</th>
                        <th scope="col" class="adm-num">Sold</th>
                        <th scope="col" class="adm-num">Sales</th>
                        <th scope="col" class="adm-num">Trend</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of answered.topProducts; track row.productId) {
                        <tr>
                          <th scope="row" class="dash__product"><span>{{ row.name }}</span><span class="dash__sub">{{ row.productCode }}</span></th>
                          <td class="adm-num">{{ number(row.units) }}</td>
                          <td class="adm-num">{{ rupees(row.revenue.current ?? 0) }}</td>
                          <td class="adm-num">
                            <span class="adm-trend" [attr.data-tone]="productTone(row.revenue)"><span aria-hidden="true">{{ arrow(row.revenue) }}</span> {{ percent(row.revenue.percentChange, row.revenue.change) }}</span>
                            <span class="dash__sub">was {{ rupees(row.revenue.previous ?? 0) }}</span>
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>
          }
        </div>

        <div class="dash__row dash__row--tables">
          <section class="adm-card" aria-labelledby="dash-low">
            <sda-low-stock [canSeeReports]="access()!.reports" [canSeeInventory]="access()!.inventory" />
          </section>

          @if (dashboard(); as answered) {
            <section class="adm-card" aria-labelledby="dash-customers">
              <div>
                <h2 class="adm-card__title" id="dash-customers">Customer overview</h2>
                <p class="adm-card__sub">Buyers who paid in these days</p>
              </div>
              <dl class="dash__stats">
                <div><dt>Buyers</dt><dd data-testid="dashboard-buyer-count">{{ number(answered.buyersByDistrict.distinctBuyers) }}</dd></div>
                <div><dt>Paid orders</dt><dd>{{ number(answered.kpis.paidOrders.current ?? 0) }}</dd></div>
              </dl>
              <p class="adm-panel__hint" data-testid="dashboard-buyers">
                {{ number(answered.buyersByDistrict.distinctBuyers) }}
                {{ answered.buyersByDistrict.distinctBuyers === 1 ? 'buyer' : 'buyers' }} (different email addresses) paid
                in these days. Each district counts the buyers who had a paid order <strong>delivered</strong> there — not
                the default district on a customer account — so a buyer who used two districts is in both.
              </p>
              @if (answered.buyersByDistrict.districts.length > 0) {
                <h3 class="dash__minor">Buyers by delivery district</h3>
                <ul class="dash__districts" data-testid="dashboard-district-bars">
                  @for (row of answered.buyersByDistrict.districts; track row.districtId) {
                    <li>
                      <span class="dash__district">{{ row.name }}</span>
                      <span class="dash__track" aria-hidden="true"><span [style.width.%]="districtWidth(row.buyers)"></span></span>
                      <span class="dash__count">{{ number(row.buyers) }}</span>
                    </li>
                  }
                </ul>
                <details class="dash__figures">
                  <summary class="adm-link">Show the figures as a table</summary>
                  <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Buyers by delivery district">
                    <table class="adm-table" data-testid="dashboard-districts">
                      <caption class="sr-only">Buyers by delivery district</caption>
                      <thead>
                        <tr><th scope="col">District</th><th scope="col" class="adm-num">Buyers</th><th scope="col" class="adm-num">Paid orders</th><th scope="col" class="adm-num">Revenue</th></tr>
                      </thead>
                      <tbody>
                        @for (row of answered.buyersByDistrict.districts; track row.districtId) {
                          <tr><th scope="row">{{ row.name }}</th><td class="adm-num">{{ number(row.buyers) }}</td><td class="adm-num">{{ number(row.paidOrders) }}</td><td class="adm-num">{{ rupees(row.revenue) }}</td></tr>
                        }
                      </tbody>
                    </table>
                  </div>
                </details>
              }
            </section>
          }
        </div>
      }

      <nav class="adm-card" aria-labelledby="dash-sections">
        <h2 class="adm-card__title" id="dash-sections">Admin sections</h2>
        <div class="dash__links">
          <a class="adm-link" routerLink="/orders">Orders</a>
          <a class="adm-link" routerLink="/orders/bank-transfers">Bank transfers</a>
          <a class="adm-link" routerLink="/orders/couriers">Couriers</a>
          <a class="adm-link" routerLink="/customers">Customers</a>
          <a class="adm-link" routerLink="/products">Products</a>
          @if (access()?.inventory) {
            <a class="adm-link" routerLink="/inventory" data-testid="dashboard-inventory-link">Inventory</a>
          }
          @if (access()?.reviews) {
            <a class="adm-link" routerLink="/reviews" data-testid="dashboard-reviews-link">Reviews</a>
          }
          <a class="adm-link" routerLink="/catalog">Departments and categories</a>
          <a class="adm-link" routerLink="/marketing/promo-codes">Promo codes</a>
          <a class="adm-link" routerLink="/marketing/sale-campaigns">Sale campaigns</a>
          <a class="adm-link" routerLink="/marketing/newsletter">Newsletter subscribers</a>
          <a class="adm-link" routerLink="/content/home" data-testid="dashboard-home-content-link">Homepage</a>
          <a class="adm-link" routerLink="/content/announcements" data-testid="dashboard-announcements-link">Announcement bar</a>
          <a class="adm-link" routerLink="/content/size-guides" data-testid="dashboard-size-guides-link">Size guides</a>
          <a class="adm-link" routerLink="/content/pages" data-testid="dashboard-content-pages-link">Content pages</a>
          @if (access()?.reports) {
            <a class="adm-link" routerLink="/reports" data-testid="dashboard-reports-link">Reports</a>
          }
          @if (access()?.traffic) {
            <a class="adm-link" routerLink="/traffic" data-testid="dashboard-traffic-link">Website traffic</a>
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
    .dash { display: grid; gap: var(--space-3); max-width: 1600px; margin-inline: auto; min-width: 0; }
    .dash__title { font-size: var(--text-h2); }
    .dash__period { grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr)); align-items: end; }
    .dash__wide { grid-column: 1 / -1; margin: 0; }
    .dash__section { display: grid; gap: var(--space-1-5); min-width: 0; }
    .dash__section-hd { display: flex; flex-wrap: wrap; align-items: baseline; gap: 0 var(--space-1-5); }
    .dash__section-title { font-size: var(--text-h4); font-weight: var(--weight-semibold); }
    .dash__row { display: grid; gap: var(--space-2); grid-template-columns: minmax(0, 1fr); }
    .dash__product { min-width: 9rem; overflow-wrap: anywhere; }
    .dash__product span { display: block; }
    .dash__sub { display: block; font-size: var(--text-label); color: var(--color-text-muted); font-weight: var(--weight-regular); }
    .dash__stats { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-1); margin: 0; }
    .dash__stats div { padding: var(--space-1-5) var(--space-1); text-align: center; background: var(--color-bg-alt); border-radius: 8px; }
    .dash__stats dt { font-size: var(--text-label); color: var(--color-text-muted); }
    .dash__stats dd { margin: 0; font-size: var(--text-h3); font-weight: var(--weight-semibold); }
    .dash__minor { font-size: var(--text-label); font-weight: var(--weight-semibold); letter-spacing: var(--tracking-label); text-transform: uppercase; color: var(--color-text-muted); }
    .dash__districts { display: grid; gap: var(--space-1); margin: 0; padding: 0; list-style: none; }
    .dash__districts li { display: flex; align-items: center; gap: var(--space-1-5); }
    .dash__district { flex: 0 0 7rem; overflow-wrap: anywhere; }
    .dash__track { flex: 1; height: 7px; border-radius: 999px; overflow: hidden; background: var(--chart-grid); }
    .dash__track span { display: block; height: 100%; background: var(--chart-accent); }
    .dash__count { min-width: 2rem; text-align: right; font-weight: var(--weight-semibold); }
    .dash__links { display: flex; flex-wrap: wrap; gap: 0 var(--space-2); }
    .dash__figures { min-width: 0; }
    @media (min-width: 900px) { .dash__row--three { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
    @media (min-width: 1100px) {
      .dash__row--two { grid-template-columns: minmax(0, 2fr) minmax(0, 1fr); }
      .dash__row--three { grid-template-columns: repeat(3, minmax(0, 1fr)); }
      .dash__row--tables { grid-template-columns: minmax(0, 3fr) minmax(0, 2fr); }
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

  /** Payment method names by code, for the Recent orders table's Payment column. */
  protected readonly paymentNames = computed<Readonly<Record<string, string>>>(() =>
    Object.fromEntries((this.dashboard()?.paymentMethods ?? []).map((row) => [row.code, row.name])),
  );

  protected readonly departmentChart = computed<DoughnutSpec>(() => ({
    kind: 'doughnut',
    cutout: 68,
    // The API's own shares (of item sales), not worked out again here.
    slices: (this.dashboard()?.departments ?? []).map((row) => ({ key: String(row.id), label: row.name, value: row.revenue, display: this.rupees(row.revenue), share: row.sharePercent })),
  }));

  protected readonly departmentTable = computed<ChartTable>(() => ({
    caption: 'Sales by department',
    testId: 'dashboard-departments',
    headers: ['Department', 'Units', 'Sales', 'Share'],
    rows: (this.dashboard()?.departments ?? []).map((row) => ({ key: row.id, label: row.name, cells: [count(row.units), this.rupees(row.revenue), shareText(row.sharePercent)] })),
  }));

  protected readonly departmentSummary = computed(() => largest('Item sales by department', this.departmentChart().slices, 'No sales in these days.'));

  protected readonly statusChart = computed<DoughnutSpec>(() => {
    const rows = this.dashboard()?.orderStatuses ?? [];
    const shares = sharesOf(rows.map((row) => row.count));

    return {
      kind: 'doughnut',
      cutout: 65,
      slices: rows.map((row, index) => ({ key: row.status, label: row.label, value: row.count, display: count(row.count), share: shares[index], token: STATUS_TOKENS[row.status] })),
    };
  });

  protected readonly statusTable = computed<ChartTable>(() => ({
    caption: 'Orders placed in the period, by status now',
    testId: 'dashboard-statuses',
    headers: ['Status', 'Orders', 'Share'],
    rows: this.statusChart().slices.map((slice) => ({ key: slice.key, label: slice.label, cells: [slice.display, shareText(slice.share)] })),
  }));

  protected readonly statusSummary = computed(() => largest('Orders placed in these days, by status now', this.statusChart().slices, 'No orders were placed in these days.'));

  protected readonly methodChart = computed<DoughnutSpec>(() => {
    const rows = this.dashboard()?.paymentMethods ?? [];
    const shares = sharesOf(rows.map((row) => row.paidOrders));

    return {
      kind: 'doughnut',
      cutout: 65,
      slices: rows.map((row, index) => ({ key: row.code, label: row.name, value: row.paidOrders, display: `${count(row.paidOrders)} paid`, share: shares[index] })),
    };
  });

  protected readonly methodTable = computed<ChartTable>(() => {
    const shares = this.methodChart().slices.map((slice) => slice.share);

    return {
      caption: 'Paid orders by payment method',
      testId: 'dashboard-methods',
      headers: ['Method', 'Paid orders', 'Revenue', 'Share of paid orders'],
      rows: (this.dashboard()?.paymentMethods ?? []).map((row, index) => ({ key: row.code, label: row.name, cells: [count(row.paidOrders), this.rupees(row.revenue), shareText(shares[index])] })),
    };
  });

  protected readonly methodSummary = computed(() => largest('Orders paid in these days, by payment method', this.methodChart().slices, 'No orders were paid in these days.'));

  protected readonly categoryChart = computed<BarSpec>(() => {
    const rows = this.dashboard()?.topCategories ?? [];

    return {
      kind: 'bar',
      indexAxis: 'y',
      // The API sends these largest first (`ReportingReader.ReadTopCategoriesAsync`), so the first bar is the leader.
      highlightFirst: true,
      seriesLabel: 'Item sales',
      labels: rows.map((row) => row.name),
      values: rows.map((row) => row.revenue),
      format: (value) => this.rupees(value),
      tick: compactRupees,
    };
  });

  protected readonly categoryTable = computed<ChartTable>(() => ({
    caption: 'Top categories by sales',
    testId: 'dashboard-categories',
    headers: ['Category', 'Units', 'Sales', 'Share'],
    rows: (this.dashboard()?.topCategories ?? []).map((row) => ({ key: row.id, label: row.name, cells: [count(row.units), this.rupees(row.revenue), shareText(row.sharePercent)] })),
  }));

  protected readonly categorySummary = computed(() =>
    largest(
      'Top categories by item sales',
      (this.dashboard()?.topCategories ?? []).map((row) => ({ label: row.name, value: row.revenue, display: this.rupees(row.revenue), share: row.sharePercent })),
      'No sales in these days.',
    ),
  );

  /** The demo's 200 px, grown so ten categories still get a readable bar each. */
  protected readonly categoryHeight = computed(() => Math.max(200, (this.dashboard()?.topCategories.length ?? 0) * 36));

  private readonly mostBuyers = computed(() => Math.max(0, ...(this.dashboard()?.buyersByDistrict.districts ?? []).map((row) => row.buyers)));

  private requests = 0;

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) => {
        const holds = (permission: string) => session.grantsAllPermissions || session.permissions.includes(permission);
        const access: Access = {
          dashboard: holds(REPORTING_PERMISSIONS.dashboard),
          orders: holds(REPORTING_PERMISSIONS.orders),
          reports: holds(REPORTING_PERMISSIONS.reports),
          traffic: holds(TRAFFIC_PERMISSION),
          inventory: holds(INVENTORY_PERMISSIONS.view),
          reviews: holds(REVIEW_PERMISSIONS.view),
        };

        this.access.set(access);

        if (access.dashboard) {
          this.load();
        }
      },
      // The session could not be read: show the widgets and let the API decide, as the other screens do.
      error: () => {
        this.access.set({ dashboard: true, orders: true, reports: true, traffic: true, inventory: true, reviews: true });
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

  protected districtWidth(buyers: number): number {
    const most = this.mostBuyers();
    return most <= 0 || buyers <= 0 ? 0 : Math.max(1, (buyers / most) * 100);
  }

  protected productTone(figure: ComparedFigure): string {
    return tone(figure);
  }

  protected arrow(figure: ComparedFigure): string {
    const way = direction(figure);
    return way === 'up' ? '▲' : way === 'down' ? '▼' : '–';
  }

  protected percent(percentChange: number | null, change: number | null): string {
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

/** One sentence for a chart's accessible name: what it shows, and its largest part with its share. */
function largest(what: string, parts: readonly { label: string; value: number; display: string; share: number }[], none: string): string {
  const top = parts.reduce<(typeof parts)[number] | null>((best, part) => (best === null || part.value > best.value ? part : best), null);

  return top === null || top.value <= 0 ? `${what}: ${none.charAt(0).toLowerCase()}${none.slice(1)}` : `${what}. Largest: ${top.label}, ${top.display} (${shareText(top.share)}).`;
}
