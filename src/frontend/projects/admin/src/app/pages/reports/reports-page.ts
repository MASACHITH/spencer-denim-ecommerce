import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { INVENTORY_FILTERS, InventoryStockFilter, REPORTING_PERMISSIONS } from '../../core/reporting/reporting.model';
import { CustomerReportView } from './customer-report';
import { InventoryReportView } from './inventory-report';
import { SalesReportView } from './sales-report';

type ReportName = 'sales' | 'inventory' | 'customers';

const REPORTS: readonly { readonly name: ReportName; readonly label: string; readonly lead: string }[] = [
  { name: 'sales', label: 'Sales', lead: 'Revenue and orders by day, week or month, with how gross revenue splits into items and delivery.' },
  { name: 'inventory', label: 'Inventory', lead: 'Stock per variant now: on hand, held by unpaid orders, available, and its low-stock threshold.' },
  { name: 'customers', label: 'Customers', lead: 'Everyone who paid for an order in the period, guests included, highest spend first.' },
];

/**
 * Reports (ADM-19; doc 11 P16 step 3): Sales, Inventory and Customers, each with an audited CSV export.
 *
 * **What a role cannot use is not shown** (the API enforces the same): without `Reports.View` nothing is
 * fetched and the page says so; without `Reports.Export` there is no Export button at all — W3-07 shipped
 * an export button that always answered 403, and that was a defect however right the refusal.
 *
 * The report is chosen with `?report=`, so a report has a link of its own and the dashboard's low-stock
 * panel can open the inventory report already filtered (`?report=inventory&status=LowStock`).
 */
@Component({
  selector: 'sda-reports-page',
  imports: [RouterLink, SalesReportView, InventoryReportView, CustomerReportView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="rpt">
      <h1 class="rpt__title">Reports</h1>

      @if (access() === null) {
        <p class="adm-muted">Loading…</p>
      } @else if (!access()!.view) {
        <p class="adm-state" data-testid="reports-forbidden">
          Your role does not include reports. <a class="adm-link" routerLink="/">Back to the dashboard</a>
        </p>
      } @else {
        <nav class="rpt__tabs" aria-label="Reports">
          @for (report of reports; track report.name) {
            <a
              class="rpt__tab adm-link"
              routerLink="/reports"
              [queryParams]="{ report: report.name }"
              [attr.aria-current]="current().name === report.name ? 'page' : null"
              [attr.data-testid]="'report-tab-' + report.name"
            >{{ report.label }}</a>
          }
        </nav>

        <section class="adm-panel" aria-labelledby="report-heading">
          <h2 class="adm-panel__title" id="report-heading">{{ current().label }}</h2>
          <p class="adm-panel__hint">{{ current().lead }}</p>
          @if (!access()!.export) {
            <p class="adm-muted" data-testid="reports-no-export">Your role can see reports but not export them.</p>
          }

          @switch (current().name) {
            @case ('sales') {
              <sda-sales-report [canExport]="access()!.export" />
            }
            @case ('inventory') {
              <sda-inventory-report [canExport]="access()!.export" [initialStatus]="status()" />
            }
            @case ('customers') {
              <sda-customer-report [canExport]="access()!.export" />
            }
          }
        </section>
      }
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .rpt { display: grid; gap: var(--space-3); max-width: 1400px; margin-inline: auto; min-width: 0; }
    .rpt__title { font-size: var(--text-h2); }
    .rpt__tabs { display: flex; flex-wrap: wrap; gap: 0 var(--space-3); border-bottom: 1px solid var(--color-border); }
    .rpt__tab { justify-content: center; min-width: var(--tap); padding-inline: var(--space-1); text-decoration: none; border-bottom: 2px solid transparent; }
    .rpt__tab[aria-current='page'] { font-weight: var(--weight-semibold); border-bottom-color: var(--color-primary); }
  `,
})
export class ReportsPage {
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AdminAuthApi);

  protected readonly reports = REPORTS;
  protected readonly access = signal<{ view: boolean; export: boolean } | null>(null);

  private readonly query = toSignal(this.route.queryParamMap.pipe(map((params) => params)), {
    initialValue: this.route.snapshot.queryParamMap,
  });

  /** An unknown `?report=` opens the sales report rather than a blank page. */
  protected readonly current = computed(
    () => REPORTS.find((report) => report.name === this.query().get('report')) ?? REPORTS[0],
  );

  protected readonly status = computed<InventoryStockFilter>(() => {
    const asked = this.query().get('status') as InventoryStockFilter | null;
    return asked && INVENTORY_FILTERS.includes(asked) ? asked : 'All';
  });

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) => {
        const holds = (permission: string) => session.grantsAllPermissions || session.permissions.includes(permission);
        this.access.set({ view: holds(REPORTING_PERMISSIONS.reports), export: holds(REPORTING_PERMISSIONS.export) });
      },
      // The session could not be read: offer the report and let the API decide, as the other screens do.
      error: () => this.access.set({ view: true, export: true }),
    });
  }
}
