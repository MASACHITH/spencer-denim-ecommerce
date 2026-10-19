import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { ReportingApi } from '../../core/reporting/reporting-api';
import { count } from '../../core/reporting/reporting-format';
import { LowStock } from '../../core/reporting/reporting.model';

/**
 * Low stock (ADM-08; doc 11 P16 "Live: 0 &lt; available ≤ threshold"), lowest first. Live: it has no period.
 *
 * The threshold column says whose threshold applied, because a variant with its own threshold and one on
 * the shop default can sit side by side and only one of them is the default the panel names.
 *
 * **Laid out as the client demo's Low Stock Alerts** (#186): a count badge in the title and a bar of how much is
 * left against the threshold. The demo's Department and Category columns and its Critical / Low / Reorder badge
 * are left out: the low-stock rows carry neither department nor category, and every row here is low by
 * definition (out-of-stock variants are not in this list), so a badge would say the same word on every row.
 */
@Component({
  selector: 'sda-low-stock',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="adm-card__hd">
      <h2 class="adm-card__title" id="dash-low">
        Low stock
        @if (stock(); as answered) {
          @if (answered.totalCount > 0) {
            <span class="adm-badge adm-badge--alert low__count" data-testid="low-stock-count">{{ number(answered.totalCount) }} {{ answered.totalCount === 1 ? 'variant' : 'variants' }}</span>
          }
        }
      </h2>
      @if (canSeeReports() && (stock()?.totalCount ?? 0) > 0) {
        <a class="adm-link" routerLink="/reports" [queryParams]="{ report: 'inventory', status: 'LowStock' }" data-testid="low-stock-report">
          Every low-stock variant in the inventory report
        </a>
      }
    </div>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="low-stock-problem">
        <p>{{ failure.message }}</p>
        <div class="adm-actions"><button class="adm-btn" type="button" (click)="load()">Try again</button></div>
      </div>
    } @else if (stock(); as answered) {
      <p class="adm-panel__hint" data-testid="low-stock-summary">
        @if (answered.totalCount === 0) {
          No variant is low on stock. A variant is low when 1 to {{ answered.defaultThreshold }} are available
          (the shop default), or up to its own threshold when it has one.
        } @else {
          {{ number(answered.totalCount) }} {{ answered.totalCount === 1 ? 'variant is' : 'variants are' }} low on stock{{ answered.totalCount > answered.rows.length ? '; the ' + answered.rows.length + ' with fewest available are shown' : '' }}.
          The shop default threshold is {{ answered.defaultThreshold }}. Out-of-stock variants are not listed here.
        }
      </p>

      @if (answered.rows.length > 0) {
        <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Low-stock variants">
          <table class="adm-table" data-testid="low-stock">
            <caption class="sr-only">Low-stock variants, fewest available first</caption>
            <thead>
              <tr>
                <th scope="col">Product</th>
                <th scope="col">SKU</th>
                <th scope="col">Available</th>
                <th scope="col" class="adm-num">Threshold</th>
              </tr>
            </thead>
            <tbody>
              @for (row of answered.rows; track row.variantId) {
                <tr>
                  <th scope="row" class="low__wrap"><span>{{ row.productName }}</span><span class="low__sub">{{ row.colour }} / {{ row.size }}</span></th>
                  <td class="low__wrap">{{ row.sku }}</td>
                  <td>
                    <span class="low__bar" aria-hidden="true"><span [style.width.%]="fill(row.available, row.threshold)"></span></span>
                    <span class="low__sub">{{ row.available }} of {{ row.threshold }}</span>
                  </td>
                  <td class="adm-num">{{ row.threshold }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    } @else {
      <p class="adm-muted">Loading stock…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-1-5); min-width: 0; }
    .low__count { margin-inline-start: var(--space-1); vertical-align: middle; }
    .low__wrap { overflow-wrap: anywhere; }
    .low__sub { display: block; font-size: var(--text-label); color: var(--color-text-muted); }
    .low__bar { display: block; width: 6rem; height: 6px; border-radius: 999px; overflow: hidden; background: var(--chart-grid); }
    .low__bar span { display: block; height: 100%; background: var(--status-awaiting); }
  `,
})
export class LowStockPanel {
  /** Whether to offer the inventory report — only to a role that can open it. */
  readonly canSeeReports = input(false);

  private readonly api = inject(ReportingApi);

  protected readonly stock = signal<LowStock | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  constructor() {
    this.load();
  }

  /** How full the bar is: available as a share of the threshold it is measured against, never past full. */
  protected fill(available: number, threshold: number): number {
    return threshold <= 0 ? 0 : Math.min(100, Math.max(0, (available / threshold) * 100));
  }

  protected number(value: number): string {
    return count(value);
  }

  load(): void {
    this.problem.set(null);
    this.stock.set(null);

    this.api.lowStock().subscribe({
      next: (answered) => this.stock.set(answered),
      error: (error: unknown) => this.problem.set(toCatalogFailure(error)),
    });
  }
}
