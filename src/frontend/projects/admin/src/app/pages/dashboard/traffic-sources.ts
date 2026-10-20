import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { PeriodQuery } from '../../core/reporting/reporting-api';
import { count } from '../../core/reporting/reporting-format';
import { TrafficApi } from '../../core/traffic/traffic-api';
import { TrafficSources } from '../../core/traffic/traffic.model';
import { ChartTable, ChartView } from '../../core/charts/chart';
import { DoughnutSpec } from '../../core/charts/chart-config';
import { channelDoughnut, channelSummary, channelTable } from '../traffic/traffic-charts';

/**
 * The dashboard's Traffic Sources widget (ADM-06) and its conversion rate (doc 11 P16: "Paid orders ÷ human
 * sessions").
 *
 * **Shown only to a role with `Traffic.View`** — the dashboard decides, as it does for Recent Orders — and the
 * API refuses the same request without it. Visits are people, not crawlers. With no visits in the period the
 * widget says so and shows no rate: a conversion rate of "0 %" over no visits would be invented.
 *
 * Drawn as the client demo's small Traffic Sources doughnut with its text legend (#186), and the same figures as
 * a table under it.
 */
@Component({
  selector: 'sda-traffic-sources',
  imports: [ChartView, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" data-testid="traffic-sources-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind !== 'forbidden') {
          <div class="adm-actions"><button class="adm-btn" type="button" (click)="load()">Try again</button></div>
        }
      </div>
    } @else if (sources(); as answered) {
      @if (answered.visits === 0) {
        <p class="adm-muted" data-testid="traffic-sources-empty">
          No visits were recorded in these days, so there is no breakdown and no conversion rate.
        </p>
      } @else {
        <dl class="traffic__figures" data-testid="traffic-sources-figures">
          <div><dt>Visits</dt><dd data-testid="traffic-visits">{{ number(answered.visits) }}</dd></div>
          <div><dt>Paid orders</dt><dd>{{ number(answered.paidOrders) }}</dd></div>
          <div>
            <dt>Conversion</dt>
            <dd data-testid="traffic-conversion">{{ percent(answered.conversionPercent) }}</dd>
          </div>
        </dl>
        <sda-chart testId="traffic-sources-chart" [spec]="spec()" [summary]="summary()" [height]="160" [table]="table()" emptyText="No visits in this period." />
        <p class="adm-panel__hint">
          Visits are people (crawlers are left out); a visit is one person's pages on one day, credited to where their
          first page came from. Conversion is paid orders divided by visits.
        </p>
      }
      <p><a class="adm-link" routerLink="/traffic" data-testid="traffic-sources-more">Open the Traffic page</a></p>
    } @else {
      <p class="adm-muted">Loading traffic…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-1-5); min-width: 0; }
    .traffic__figures { display: grid; gap: var(--space-1); grid-template-columns: repeat(3, minmax(0, 1fr)); margin: 0; }
    .traffic__figures div { min-width: 0; padding: var(--space-1); text-align: center; background: var(--color-bg-alt); border-radius: 8px; }
    .traffic__figures dt { font-size: var(--text-label); color: var(--color-text-muted); }
    .traffic__figures dd { margin: 0; font-size: var(--text-h4); font-weight: var(--weight-semibold); font-variant-numeric: tabular-nums; }
    p { margin: 0; }
  `,
})
export class TrafficSourcesPanel {
  readonly period = input.required<PeriodQuery>();

  private readonly api = inject(TrafficApi);

  protected readonly sources = signal<TrafficSources | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  protected readonly spec = computed<DoughnutSpec>(() => channelDoughnut(this.sources()?.channels ?? []));

  protected readonly table = computed<ChartTable>(() => channelTable(this.sources()?.channels ?? [], 'traffic-sources'));

  protected readonly summary = computed(() => channelSummary(this.sources()?.channels ?? []));

  private requests = 0;

  constructor() {
    effect(() => {
      this.period();
      untracked(() => this.load());
    });
  }

  load(): void {
    const request = ++this.requests;
    this.problem.set(null);
    this.sources.set(null);

    this.api.sources(this.period()).subscribe({
      next: (answered) => {
        if (request === this.requests) {
          this.sources.set(answered);
        }
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          this.problem.set(toCatalogFailure(error));
        }
      },
    });
  }

  protected number(value: number): string {
    return count(value);
  }

  protected percent(value: number | null): string {
    return value === null ? '—' : `${value.toLocaleString('en-LK', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
  }
}
