import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';

import { money } from '../../core/orders/order-formatting';
import { ReportingApi } from '../../core/reporting/reporting-api';
import { colomboToday, count, formatDay } from '../../core/reporting/reporting-format';
import { AdminDashboard } from '../../core/reporting/reporting.model';
import { TrafficApi } from '../../core/traffic/traffic-api';
import { DashIcon, IconName } from './dash-icon';

interface TodayCard {
  readonly key: string;
  readonly label: string;
  readonly value: string;
  readonly icon: IconName;
  readonly token: string;
}

/**
 * The Today banner at the top of the dashboard (#186, the client demo's `#today-banner`): today's figures, whatever
 * period the rest of the page shows.
 *
 * **Every figure is one the API already counts for a period, asked for the one Colombo day that is today**:
 * gross revenue and orders from `GET /api/admin/dashboard?from=today&to=today` (revenue on the day an order was
 * paid, orders on the day it was placed — the same rules as the Key figures), and visits from the traffic-sources
 * endpoint, only for a role with `Traffic.View`.
 *
 * **What the demo has and this does not:** "New customers today" — no endpoint counts new customers (#186's API
 * check), so the card is left out rather than invented. And the demo's "TODAY LIVE" with a pulsing dot: these
 * figures are read once when the page opens and do not update themselves, so the banner says the time they were
 * read instead of claiming to be live.
 *
 * If either request fails the banner shows what it has; the Key figures below report the failure.
 */
@Component({
  selector: 'sda-today-banner',
  imports: [DashIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="today" aria-labelledby="dash-today" data-testid="today-banner">
      <h2 class="today__title" id="dash-today">
        Today <span class="today__when">{{ day }}{{ readAt() ? ', as of ' + readAt() : '' }}</span>
      </h2>
      <ul class="today__cards">
        @for (card of cards(); track card.key) {
          <li class="today__card" [attr.data-testid]="'today-' + card.key" [style.--icon-colour]="'var(' + card.token + ')'">
            <sda-dash-icon [name]="card.icon" />
            <div>
              <p class="today__value">{{ card.value }}</p>
              <p class="today__label">{{ card.label }}</p>
            </div>
          </li>
        }
      </ul>
    </section>
  `,
  styles: `
    .today {
      display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); padding: var(--space-2);
      color: var(--dash-banner-text); background: var(--dash-banner-bg); border-radius: var(--card-radius);
    }
    .today__title { margin: 0; font-size: var(--text-label); font-weight: var(--weight-semibold); letter-spacing: var(--tracking-label); text-transform: uppercase; }
    .today__when { display: block; font-weight: var(--weight-regular); letter-spacing: 0; text-transform: none; color: var(--dash-banner-muted); }
    .today__cards { display: grid; flex: 1 1 32rem; gap: var(--space-1-5); grid-template-columns: repeat(auto-fit, minmax(min(100%, 11rem), 1fr)); margin: 0; padding: 0; list-style: none; }
    .today__card { display: flex; align-items: center; gap: var(--space-1-5); min-width: 0; padding: var(--space-1) var(--space-1-5); border-radius: 8px; background: rgb(255 255 255 / 0.06); }
    .today__card p { margin: 0; overflow-wrap: anywhere; }
    .today__value { font-size: var(--text-h4); font-weight: var(--weight-semibold); font-variant-numeric: tabular-nums; color: var(--icon-colour); }
    .today__label { font-size: var(--text-label); color: var(--dash-banner-muted); }
  `,
})
export class TodayBanner {
  /** Whether to ask for today's visits: only a role with `Traffic.View`, as the API requires. */
  readonly canSeeTraffic = input(false);

  private readonly reporting = inject(ReportingApi);
  private readonly traffic = inject(TrafficApi);

  private readonly today = colomboToday();
  protected readonly day = formatDay(this.today);

  /** `undefined` while asking, `null` when the request failed. */
  protected readonly figures = signal<AdminDashboard | null | undefined>(undefined);
  protected readonly visits = signal<number | null | undefined>(undefined);
  protected readonly readAt = signal('');

  protected readonly cards = computed<readonly TodayCard[]>(() => {
    const answered = this.figures();
    const currency = answered?.currency ?? 'LKR';
    const cards: TodayCard[] = [
      { key: 'revenue', label: "Today's revenue (paid today)", value: shown(answered, (a) => money(a.kpis.grossRevenue.current ?? 0, currency)), icon: 'money', token: '--chart-on-dark-1' },
      { key: 'orders', label: 'Orders placed today', value: shown(answered, (a) => count(a.kpis.orders.current)), icon: 'bag', token: '--chart-on-dark-2' },
    ];

    if (this.canSeeTraffic()) {
      cards.push({ key: 'visits', label: 'Visits today', value: shown(this.visits(), count), icon: 'visitors', token: '--chart-on-dark-3' });
    }

    return cards;
  });

  constructor() {
    const period = { from: this.today, to: this.today };

    this.reporting.dashboard(period).subscribe({
      next: (answered) => {
        this.figures.set(answered);
        this.readAt.set(new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'Asia/Colombo' }));
      },
      error: () => this.figures.set(null),
    });

    // `canSeeTraffic` is an input, so it is not known in the constructor; ask once it is, and only once.
    let asked = false;
    effect(() => {
      if (asked || !this.canSeeTraffic()) {
        return;
      }

      asked = true;
      untracked(() =>
        this.traffic.sources(period).subscribe({
          next: (answered) => this.visits.set(answered.visits),
          error: () => this.visits.set(null),
        }),
      );
    });
  }
}

/** `…` while asking, `—` when it could not be read, otherwise the figure. */
function shown<T>(value: T | null | undefined, format: (value: T) => string): string {
  return value === undefined ? '…' : value === null ? '—' : format(value);
}
