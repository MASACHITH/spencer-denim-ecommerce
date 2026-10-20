import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminOrderApi } from '../../core/orders/admin-order-api';
import { ReportingApi } from '../../core/reporting/reporting-api';
import { count } from '../../core/reporting/reporting-format';
import { DashIcon } from './dash-icon';

interface Alert {
  readonly key: string;
  readonly count: number;
  readonly text: string;
  readonly link: { readonly path: string; readonly query?: Record<string, string> } | null;
}

/**
 * The amber alert strip under the Today banner (#186, the client demo's `#alert-strip`): work waiting now, whatever
 * period the page shows. Hidden when there is nothing to say.
 *
 * - **Paid orders not yet dispatched** — orders in `Paid` or `Processing` over the whole order table, from the order
 *   list's own `statusCounts` (`GET /api/admin/orders?pageSize=1`). Not the dashboard's Order status breakdown,
 *   which counts only orders *placed in the selected period*: a paid order placed 31 days ago is still waiting.
 *   Asked only for a role with `Orders.View`, and it links to the order list.
 * - **Variants low on stock** — `totalCount` from the low-stock endpoint, which is live. It links to the inventory
 *   report filtered to low stock for a role that can open reports; for any other role it is text, and the Low stock
 *   card further down the same page lists the variants.
 *
 * The shell's email-failure banner (#138) stays where it is, above every admin page; this strip does not repeat it.
 * A request that fails leaves its pill out: the card it summarises reports the failure itself.
 */
@Component({
  selector: 'sda-alert-strip',
  imports: [RouterLink, DashIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (alerts().length > 0) {
      <section class="strip" aria-label="Needs attention" data-testid="alert-strip">
        <ul class="strip__list">
          @for (alert of alerts(); track alert.key) {
            <li class="strip__item" [attr.data-testid]="'alert-strip-' + alert.key">
              <sda-dash-icon name="alert" class="strip__icon" />
              @if (alert.link; as link) {
                <a class="adm-link" [routerLink]="link.path" [queryParams]="link.query ?? null">
                  <strong>{{ number(alert.count) }}</strong>&nbsp;{{ alert.text }}
                </a>
              } @else {
                <span><strong>{{ number(alert.count) }}</strong>&nbsp;{{ alert.text }}</span>
              }
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: `
    .strip { padding: var(--space-0-5) var(--space-2); border-radius: 8px; border: 1px solid color-mix(in srgb, var(--status-awaiting) 35%, transparent); background: color-mix(in srgb, var(--status-awaiting) 8%, var(--color-surface)); }
    .strip__list { display: flex; flex-wrap: wrap; gap: 0 var(--space-3); margin: 0; padding: 0; list-style: none; }
    .strip__item { display: flex; align-items: center; gap: var(--space-1); min-width: 0; color: color-mix(in srgb, var(--status-awaiting) 70%, var(--color-text)); }
    .strip__item .adm-link, .strip__item span { color: inherit; overflow-wrap: anywhere; }
    .strip__icon { --icon-colour: var(--status-awaiting); width: 28px; height: 28px; background: none; }
  `,
})
export class AlertStrip {
  readonly canSeeOrders = input(false);
  readonly canSeeReports = input(false);

  private readonly orders = inject(AdminOrderApi);
  private readonly reporting = inject(ReportingApi);

  protected readonly waiting = signal<number | null>(null);
  protected readonly low = signal<number | null>(null);

  protected readonly alerts = computed<readonly Alert[]>(() => {
    const alerts: Alert[] = [];
    const waiting = this.waiting();
    const low = this.low();

    if (waiting !== null && waiting > 0) {
      alerts.push({ key: 'dispatch', count: waiting, text: waiting === 1 ? 'paid order not yet dispatched' : 'paid orders not yet dispatched', link: { path: '/orders' } });
    }

    if (low !== null && low > 0) {
      alerts.push({
        key: 'low-stock',
        count: low,
        text: low === 1 ? 'variant low on stock' : 'variants low on stock',
        link: this.canSeeReports() ? { path: '/reports', query: { report: 'inventory', status: 'LowStock' } } : null,
      });
    }

    return alerts;
  });

  constructor() {
    this.reporting.lowStock().subscribe({ next: (answered) => this.low.set(answered.totalCount), error: () => this.low.set(null) });

    // `canSeeOrders` is an input, so it is not known in the constructor; ask once it is, and only once.
    let asked = false;
    effect(() => {
      if (asked || !this.canSeeOrders()) {
        return;
      }

      asked = true;
      untracked(() =>
        this.orders.list({ page: 1, pageSize: 1 }).subscribe({
          next: (page) => this.waiting.set((page.statusCounts['Paid'] ?? 0) + (page.statusCounts['Processing'] ?? 0)),
          error: () => this.waiting.set(null),
        }),
      );
    });
  }

  protected number(value: number): string {
    return count(value);
  }
}
