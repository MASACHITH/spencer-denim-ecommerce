import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { PeriodQuery } from '../../core/reporting/reporting-api';
import {
  PERIOD_PRESETS,
  PeriodPreset,
  addDays,
  changeText,
  colomboToday,
  count,
  formatDay,
  formatPeriod,
  presetPeriod,
} from '../../core/reporting/reporting-format';
import { TrafficApi } from '../../core/traffic/traffic-api';
import { CHANNEL_MEANINGS, TRAFFIC_PERMISSION, TrafficReport } from '../../core/traffic/traffic.model';
import { BarRow, BarTable } from '../dashboard/bar-table';

/** The page families of `PageType`, as staff read them. */
const PAGE_TYPE_LABELS: Readonly<Record<string, string>> = {
  Home: 'Home page',
  Department: 'Department',
  Category: 'Category listing',
  Product: 'Product',
  Search: 'Search results',
  NewArrivals: 'New arrivals',
  Sale: 'Sale',
  Bag: 'Bag',
  Wishlist: 'Wishlist',
  Checkout: 'Checkout',
  Order: 'Order confirmation',
  Account: 'My account',
  Content: 'Information page',
  Other: 'Other',
};

/** Up to this many days, the day-by-day table is open; a longer period starts it folded. */
const OPEN_DAYS = 31;

/**
 * The Traffic page (ADM-20; doc 11 P16 step 4: "visits, sources, top pages, devices").
 *
 * **What a visit is, said on the page:** one person's pages on one Colombo day, credited to where their first page
 * came from; crawlers are counted apart and in nothing else. Today and the three days before it are counted live,
 * older days from the nightly totals — the page says which days are live.
 *
 * **Every figure is in a table** (the bars are decoration on it), so there is no chart without its text, and an
 * empty period says it is empty instead of drawing zeros. The API needs `Traffic.View`; a role without it is told
 * so rather than shown an error.
 */
@Component({
  selector: 'sda-traffic-page',
  imports: [BarTable, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="traffic">
      <h1 class="traffic__title">Traffic</h1>
      <p class="sr-only" role="status" tabindex="-1" data-testid="traffic-status">{{ said() }}</p>

      @if (allowed() === null) {
        <p class="adm-muted">Loading…</p>
      } @else if (!allowed()) {
        <p class="adm-state" data-testid="traffic-forbidden">
          Your role does not include traffic figures. Ask a Super Admin for the Traffic permission if you need them.
        </p>
        <p><a class="adm-link" routerLink="/">Back to the dashboard</a></p>
      } @else {
        <form class="traffic__period adm-panel" aria-label="Period" (submit)="applyCustom($event)">
          <label class="adm-field">
            <span class="adm-label">Period</span>
            <select class="adm-input" name="preset" data-testid="traffic-preset" (change)="choosePreset($event)">
              @for (option of presets; track option.value) {
                <option [value]="option.value" [selected]="preset() === option.value">{{ option.label }}</option>
              }
            </select>
          </label>
          @if (preset() === 'custom') {
            <label class="adm-field">
              <span class="adm-label">From</span>
              <input class="adm-input" type="date" name="from" data-testid="traffic-from" [value]="customFrom()" (change)="edit('from', $event)" />
            </label>
            <label class="adm-field">
              <span class="adm-label">To</span>
              <input class="adm-input" type="date" name="to" data-testid="traffic-to" [value]="customTo()" (change)="edit('to', $event)" />
            </label>
            <div class="adm-actions">
              <button class="adm-btn--primary" type="submit" data-testid="traffic-apply">Show</button>
            </div>
          }
          @if (formError()) {
            <p class="adm-field-error traffic__wide" data-testid="traffic-period-error">{{ formError() }}</p>
          }
          <p class="adm-panel__hint traffic__wide" data-testid="traffic-period">{{ periodText() }}</p>
        </form>

        @if (problem(); as failure) {
          <div class="adm-state adm-state--error" data-testid="traffic-problem">
            <p>{{ failure.message }}</p>
            @if (failure.kind !== 'forbidden') {
              <div class="adm-actions"><button class="adm-btn" type="button" (click)="load()">Try again</button></div>
            }
          </div>
        } @else if (report(); as answered) {
          <section class="adm-panel" aria-labelledby="traffic-summary">
            <h2 class="adm-panel__title" id="traffic-summary">Visits and page views</h2>
            <ul class="traffic__figures" data-testid="traffic-figures">
              <li data-testid="traffic-visits">
                <p class="traffic__label">Visits</p>
                <p class="traffic__value">{{ number(answered.visits.current) }}</p>
                <p class="traffic__hint">{{ change(answered.visits) }} (previous period: {{ number(answered.visits.previous) }})</p>
              </li>
              <li data-testid="traffic-page-views">
                <p class="traffic__label">Page views</p>
                <p class="traffic__value">{{ number(answered.pageViews.current) }}</p>
                <p class="traffic__hint">{{ change(answered.pageViews) }} (previous period: {{ number(answered.pageViews.previous) }})</p>
              </li>
              <li data-testid="traffic-bots">
                <p class="traffic__label">Crawler page views</p>
                <p class="traffic__value">{{ number(answered.botPageViews) }}</p>
                <p class="traffic__hint">Search engines, link previews and automated browsers. Not in any other figure.</p>
              </li>
            </ul>
            <p class="adm-panel__hint" data-testid="traffic-definitions">
              A visit is one person's pages on one Colombo day, credited to where their first page that day came from.
              Days from {{ day(answered.liveFrom) }} are counted live; earlier days come from the nightly totals. No IP
              address and no visitor ID are kept.
            </p>
          </section>

          @if (empty()) {
            <p class="adm-state" data-testid="traffic-empty">No visits were recorded in these days.</p>
          } @else {
            <div class="traffic__grid">
              <section class="adm-panel traffic__wide" aria-labelledby="traffic-days">
                <h2 class="adm-panel__title" id="traffic-days">Visits by day</h2>
                <details class="traffic__days" [open]="answered.days.length <= openDays">
                  <summary class="adm-link" data-testid="traffic-days-toggle">Day by day ({{ answered.days.length }} {{ answered.days.length === 1 ? 'day' : 'days' }})</summary>
                  <sda-bar-table caption="Visits and page views by day" testId="traffic-days" [headers]="['Day', 'Visits', 'Page views', 'Crawler views']" [rows]="days()" />
                </details>
              </section>

              <section class="adm-panel" aria-labelledby="traffic-channels">
                <h2 class="adm-panel__title" id="traffic-channels">Where visitors came from</h2>
                <sda-bar-table caption="Visits by traffic source" testId="traffic-channels" [headers]="['Source', 'Visits', 'Page views', 'Share']" [rows]="channels()" />
              </section>

              <section class="adm-panel" aria-labelledby="traffic-devices">
                <h2 class="adm-panel__title" id="traffic-devices">Devices</h2>
                <sda-bar-table caption="Visits by device" testId="traffic-devices" [headers]="['Device', 'Visits', 'Page views', 'Share']" [rows]="devices()" />
              </section>

              <section class="adm-panel" aria-labelledby="traffic-sources">
                <h2 class="adm-panel__title" id="traffic-sources">Top referring sites and campaigns</h2>
                <p class="adm-panel__hint">
                  The campaign source when a link was tagged, otherwise the site the visitor came from. "(direct)" is no
                  source at all; "(other)" gathers the smaller sources of a busy day.
                </p>
                <sda-bar-table caption="Top sources by visits" testId="traffic-top-sources" [headers]="['Source', 'Visits', 'Page views']" [rows]="sources()" />
              </section>

              <section class="adm-panel" aria-labelledby="traffic-pages">
                <h2 class="adm-panel__title" id="traffic-pages">Top pages</h2>
                <p class="adm-panel__hint">
                  By page views. Addresses keep only the listing's filters; anything private in an address, such as an order
                  link, is never stored.
                </p>
                <sda-bar-table caption="Top pages by page views" testId="traffic-top-pages" [headers]="['Page', 'Kind', 'Page views', 'Visits']" [rows]="pages()" />
              </section>
            </div>
          }
        } @else {
          <p class="adm-muted">Loading traffic…</p>
        }
      }
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .traffic { display: grid; gap: var(--space-3); max-width: 1400px; margin-inline: auto; min-width: 0; }
    .traffic__title { font-size: var(--text-h2); }
    .traffic__period { grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr)); align-items: end; }
    .traffic__wide { grid-column: 1 / -1; margin: 0; }
    .traffic__grid { display: grid; gap: var(--space-3); grid-template-columns: minmax(0, 1fr); }
    .traffic__grid > * { min-width: 0; align-content: start; }
    .traffic__figures {
      display: grid; gap: var(--space-2); margin: 0; padding: 0; list-style: none;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 13rem), 1fr));
    }
    .traffic__figures li { display: grid; gap: 2px; align-content: start; padding: var(--space-2); background: var(--color-surface); border: 1px solid var(--color-border); min-width: 0; }
    .traffic__figures p { margin: 0; overflow-wrap: anywhere; }
    .traffic__label { font-size: var(--text-label); letter-spacing: var(--tracking-label); text-transform: uppercase; color: var(--color-text-muted); }
    .traffic__value { font-size: var(--text-h3); font-variant-numeric: tabular-nums; }
    .traffic__hint { font-size: var(--text-small); color: var(--color-text-muted); }
    .traffic__days { min-width: 0; max-width: 100%; }
    .traffic__days summary { display: inline-flex; align-items: center; min-height: 44px; }
    @media (min-width: 1024px) {
      .traffic__grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    }
  `,
})
export class TrafficPage {
  private readonly api = inject(TrafficApi);
  private readonly auth = inject(AdminAuthApi);
  private readonly injector = inject(Injector);

  protected readonly presets = PERIOD_PRESETS;
  protected readonly openDays = OPEN_DAYS;
  protected readonly preset = signal<PeriodPreset>('last30');
  protected readonly customFrom = signal('');
  protected readonly customTo = signal('');
  protected readonly formError = signal<string | null>(null);
  protected readonly period = signal<PeriodQuery>(presetPeriod('last30', colomboToday()));

  protected readonly allowed = signal<boolean | null>(null);
  protected readonly report = signal<TrafficReport | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  protected readonly empty = computed(() => {
    const answered = this.report();
    return answered !== null && answered.visits.current === 0 && answered.botPageViews === 0;
  });

  protected readonly periodText = computed(() => {
    const answered = this.report();
    return answered
      ? `${formatPeriod(answered.period)} (${answered.period.days} ${answered.period.days === 1 ? 'day' : 'days'}), compared with ${formatPeriod(answered.previousPeriod)}.`
      : 'Days are Colombo days, both ends included.';
  });

  protected readonly days = computed<readonly BarRow[]>(() =>
    (this.report()?.days ?? []).map((row) => ({
      key: row.date,
      label: formatDay(row.date),
      cells: [count(row.visits), count(row.pageViews), count(row.botPageViews)],
      value: row.visits,
    })),
  );

  protected readonly channels = computed<readonly BarRow[]>(() =>
    (this.report()?.channels ?? []).map((row) => ({
      key: row.channel,
      label: `${row.label} — ${CHANNEL_MEANINGS[row.channel] ?? ''}`,
      cells: [count(row.visits), count(row.pageViews), this.share(row.sharePercent)],
      value: row.visits,
    })),
  );

  protected readonly devices = computed<readonly BarRow[]>(() =>
    (this.report()?.devices ?? []).map((row) => ({
      key: row.device,
      label: row.label,
      cells: [count(row.visits), count(row.pageViews), this.share(row.sharePercent)],
      value: row.visits,
    })),
  );

  protected readonly sources = computed<readonly BarRow[]>(() =>
    (this.report()?.sources ?? []).map((row) => ({
      key: row.source,
      label: row.source,
      cells: [count(row.visits), count(row.pageViews)],
      value: row.visits,
    })),
  );

  protected readonly pages = computed<readonly BarRow[]>(() =>
    (this.report()?.topPages ?? []).map((row) => ({
      key: row.path,
      label: row.productName ? `${row.productName} (${row.path})` : row.path,
      cells: [PAGE_TYPE_LABELS[row.pageType] ?? row.pageType, count(row.pageViews), count(row.visits)],
      value: row.pageViews,
    })),
  );

  private requests = 0;

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) => {
        const allowed = session.grantsAllPermissions || session.permissions.includes(TRAFFIC_PERMISSION);
        this.allowed.set(allowed);
        if (allowed) {
          this.load();
        }
      },
      // The session could not be read: let the API decide, as the other screens do.
      error: () => {
        this.allowed.set(true);
        this.load();
      },
    });
  }

  load(): void {
    const request = ++this.requests;

    this.problem.set(null);
    this.report.set(null);
    this.announce('Loading traffic…');

    this.api.report(this.period()).subscribe({
      next: (answered) => {
        if (request !== this.requests) {
          return;
        }

        this.report.set(answered);
        this.announce(this.empty() ? `No visits were recorded in ${formatPeriod(answered.period)}.` : `Showing ${this.periodText()}`);
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

  protected number(value: number | null): string {
    return count(value);
  }

  protected change(figure: TrafficReport['visits']): string {
    return changeText(figure);
  }

  protected day(value: string): string {
    return formatDay(value);
  }

  private share(value: number): string {
    return `${value.toLocaleString('en-LK', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
  }

  /** The one live region; never inside a block an update removes. */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
