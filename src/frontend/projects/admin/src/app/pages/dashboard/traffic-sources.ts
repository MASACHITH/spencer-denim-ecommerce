import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { PeriodQuery } from '../../core/reporting/reporting-api';
import { count } from '../../core/reporting/reporting-format';
import { TrafficApi } from '../../core/traffic/traffic-api';
import { CHANNEL_MEANINGS, TrafficSources } from '../../core/traffic/traffic.model';
import { BarRow, BarTable } from './bar-table';

/**
 * The dashboard's Traffic Sources widget (ADM-06) and its conversion rate (doc 11 P16: "Paid orders ÷ human
 * sessions").
 *
 * **Shown only to a role with `Traffic.View`** — the dashboard decides, as it does for Recent Orders — and the
 * API refuses the same request without it. Visits are people, not crawlers. With no visits in the period the
 * widget says so and shows no rate: a conversion rate of "0 %" over no visits would be invented.
 *
 * Like every dashboard breakdown it is a table with figures; the bars are decoration on it.
 */
@Component({
  selector: 'sda-traffic-sources',
  imports: [BarTable, RouterLink],
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
            <dt>Conversion rate</dt>
            <dd data-testid="traffic-conversion">{{ percent(answered.conversionPercent) }}</dd>
          </div>
        </dl>
        <p class="adm-panel__hint">
          Visits are people (crawlers are left out); a visit is one person's pages on one day, credited to where their
          first page came from. Conversion is paid orders divided by visits.
        </p>
        <sda-bar-table caption="Visits by traffic source" testId="traffic-sources" [headers]="['Source', 'Visits', 'Page views', 'Share']" [rows]="rows()" />
      }
      <p><a class="adm-link" routerLink="/traffic" data-testid="traffic-sources-more">Open the Traffic page</a></p>
    } @else {
      <p class="adm-muted">Loading traffic…</p>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-1); min-width: 0; }
    .traffic__figures { display: grid; gap: var(--space-1); grid-template-columns: repeat(auto-fit, minmax(min(100%, 8rem), 1fr)); margin: 0; }
    .traffic__figures div { min-width: 0; }
    .traffic__figures dt { font-size: var(--text-label); letter-spacing: var(--tracking-label); text-transform: uppercase; color: var(--color-text-muted); }
    .traffic__figures dd { margin: 0; font-size: var(--text-h3); font-variant-numeric: tabular-nums; }
    p { margin: 0; }
  `,
})
export class TrafficSourcesPanel {
  readonly period = input.required<PeriodQuery>();

  private readonly api = inject(TrafficApi);

  protected readonly sources = signal<TrafficSources | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);

  protected readonly rows = computed<readonly BarRow[]>(() =>
    (this.sources()?.channels ?? []).map((row) => ({
      key: row.channel,
      label: `${row.label} — ${CHANNEL_MEANINGS[row.channel] ?? ''}`,
      cells: [count(row.visits), count(row.pageViews), `${row.sharePercent.toLocaleString('en-LK', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`],
      value: row.visits,
    })),
  );

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
