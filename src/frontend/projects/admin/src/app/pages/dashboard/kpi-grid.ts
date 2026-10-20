import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { money } from '../../core/orders/order-formatting';
import { changeText, count, direction } from '../../core/reporting/reporting-format';
import { ComparedFigure, DashboardKpis } from '../../core/reporting/reporting.model';
import { DashIcon, IconName } from './dash-icon';

interface Card {
  readonly key: string;
  readonly label: string;
  readonly hint: string;
  readonly figure: ComparedFigure;
  readonly kind: 'money' | 'count';
  readonly icon: IconName;
  /** A rise is bad news — refunds — so its colours are the other way round (the demo's "bad if up"). */
  readonly badIfUp?: boolean;
}

/** Whether a move is good news, bad news or no move at all; colour follows this, the arrow follows `direction`. */
export function tone(figure: ComparedFigure, badIfUp = false): 'good' | 'bad' | 'flat' {
  const way = direction(figure);

  if (way === 'flat') {
    return 'flat';
  }

  return (way === 'up') !== badIfUp ? 'good' : 'bad';
}

/**
 * The KPI cards (ADM-04), each against the same number of days immediately before (doc 08 §8.1), in the client
 * demo's card: an icon tile, the label, the figure, and ▲/▼ with the change.
 *
 * **Each card says what it counts**, in a line under the figure, because the figures that sound alike are
 * counted on different dates: revenue on the day an order was paid, orders on the day it was placed, refunds
 * on the day they completed. **Conversion rate is not a card here**: it is paid orders ÷ visits, so it sits with
 * the visits it divides by, in the Traffic sources widget (`traffic-sources.ts`, W4-03).
 *
 * **The change is green when it is good news and red when it is bad** (#186), and the arrow and the words say
 * which way it went, so colour is never the only cue. For refunds a rise is bad news, so the colours are
 * reversed, as the demo reverses them for its "bad if up" figures.
 */
@Component({
  selector: 'sda-kpi-grid',
  imports: [DashIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="kpis" data-testid="kpis">
      @for (card of cards(); track card.key) {
        <li class="kpi" [attr.data-testid]="'kpi-' + card.key">
          <sda-dash-icon class="kpi__icon" [name]="card.icon" />
          <div class="kpi__body">
            <p class="kpi__label">{{ card.label }}</p>
            <p class="kpi__value">{{ value(card, card.figure.current) }}</p>
            <p class="kpi__change adm-trend" [attr.data-direction]="trend(card.figure)" [attr.data-tone]="toneOf(card)">
              <span aria-hidden="true">{{ arrow(card.figure) }}</span> {{ change(card.figure) }}
            </p>
            <p class="kpi__previous">Previous period: {{ value(card, card.figure.previous) }}</p>
            <p class="kpi__hint">{{ card.hint }}</p>
          </div>
        </li>
      }
    </ul>
  `,
  styles: `
    .kpis {
      display: grid; gap: var(--space-2); margin: 0; padding: 0; list-style: none;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 15rem), 1fr));
    }
    .kpi {
      display: flex; align-items: flex-start; gap: var(--space-1-5); min-width: 0; padding: var(--space-2);
      background: var(--color-surface); border-radius: var(--card-radius); box-shadow: var(--card-shadow);
    }
    .kpi__body { display: grid; gap: 2px; min-width: 0; }
    .kpi p { margin: 0; overflow-wrap: anywhere; }
    .kpi__label { font-size: var(--text-label); color: var(--color-text-muted); font-weight: var(--weight-medium); }
    .kpi__value { font-size: var(--text-h3); font-weight: var(--weight-semibold); font-variant-numeric: tabular-nums; }
    .kpi__change { font-size: var(--text-label); white-space: normal; }
    .kpi__previous, .kpi__hint { font-size: var(--text-label); color: var(--color-text-muted); }
    @media (min-width: 1280px) { .kpis { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
  `,
})
export class KpiGrid {
  readonly kpis = input.required<DashboardKpis>();
  readonly currency = input.required<string>();

  protected readonly cards = computed<readonly Card[]>(() => {
    const kpis = this.kpis();

    return [
      { key: 'gross', label: 'Gross revenue', hint: 'Orders by the day they were paid, delivery included, refunded orders included.', figure: kpis.grossRevenue, kind: 'money', icon: 'money' },
      { key: 'net', label: 'Net revenue', hint: 'Gross revenue less the refunds completed in these days.', figure: kpis.netRevenue, kind: 'money', icon: 'wallet' },
      { key: 'refunds', label: 'Refunds completed', hint: 'Refunds of paid orders, by the day they completed.', figure: kpis.refundsCompleted, kind: 'money', icon: 'refund', badIfUp: true },
      { key: 'orders', label: 'Orders', hint: 'Orders by the day they were placed, paid or not.', figure: kpis.orders, kind: 'count', icon: 'bag' },
      { key: 'paid', label: 'Paid orders', hint: 'Orders by the day they were paid.', figure: kpis.paidOrders, kind: 'count', icon: 'check' },
      { key: 'aov', label: 'Average order value', hint: 'Gross revenue divided by paid orders.', figure: kpis.averageOrderValue, kind: 'money', icon: 'average' },
    ];
  });

  protected value(card: Card, amount: number | null): string {
    if (amount === null) {
      return '—';
    }

    return card.kind === 'money' ? money(amount, this.currency()) : count(amount);
  }

  protected change(figure: ComparedFigure): string {
    return changeText(figure);
  }

  protected trend(figure: ComparedFigure): string {
    return direction(figure);
  }

  protected toneOf(card: Card): string {
    return tone(card.figure, card.badIfUp);
  }

  protected arrow(figure: ComparedFigure): string {
    const way = direction(figure);
    return way === 'up' ? '▲' : way === 'down' ? '▼' : '–';
  }
}
