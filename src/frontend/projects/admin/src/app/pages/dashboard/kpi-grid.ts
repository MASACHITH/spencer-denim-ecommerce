import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { money } from '../../core/orders/order-formatting';
import { changeText, count, direction } from '../../core/reporting/reporting-format';
import { ComparedFigure, DashboardKpis } from '../../core/reporting/reporting.model';

interface Card {
  readonly key: string;
  readonly label: string;
  readonly hint: string;
  readonly figure: ComparedFigure;
  readonly kind: 'money' | 'count';
}

/**
 * The KPI cards (ADM-04), each against the same number of days immediately before (doc 08 §8.1).
 *
 * **Each card says what it counts**, in a line under the figure, because the four figures that sound alike
 * are counted on different dates: revenue on the day an order was paid, orders on the day it was placed,
 * refunds on the day they completed. **Conversion rate is not a card here**: it is paid orders ÷ visits, so it
 * sits with the visits it divides by, in the Traffic sources widget (`traffic-sources.ts`, W4-03).
 *
 * The direction of a change is in words and a symbol, and **not in colour at all**: green for "up" would
 * call a rise in refunds good news.
 */
@Component({
  selector: 'sda-kpi-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="kpis" data-testid="kpis">
      @for (card of cards(); track card.key) {
        <li class="kpi" [attr.data-testid]="'kpi-' + card.key">
          <p class="kpi__label">{{ card.label }}</p>
          <p class="kpi__value">{{ value(card, card.figure.current) }}</p>
          <p class="kpi__change" [attr.data-direction]="trend(card.figure)">
            <span aria-hidden="true">{{ arrow(card.figure) }}</span> {{ change(card.figure) }}
          </p>
          <p class="kpi__previous">Previous period: {{ value(card, card.figure.previous) }}</p>
          <p class="kpi__hint">{{ card.hint }}</p>
        </li>
      }
    </ul>
  `,
  styles: `
    .kpis {
      display: grid; gap: var(--space-2); margin: 0; padding: 0; list-style: none;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 13rem), 1fr));
    }
    .kpi { display: grid; gap: 2px; align-content: start; padding: var(--space-2); background: var(--color-surface); border: 1px solid var(--color-border); min-width: 0; }
    .kpi p { margin: 0; overflow-wrap: anywhere; }
    .kpi__label { font-size: var(--text-label); letter-spacing: var(--tracking-label); text-transform: uppercase; color: var(--color-text-muted); }
    .kpi__value { font-size: var(--text-h3); font-variant-numeric: tabular-nums; }
    .kpi__change { font-size: var(--text-small); }
    .kpi__previous, .kpi__hint { font-size: var(--text-small); color: var(--color-text-muted); }
  `,
})
export class KpiGrid {
  readonly kpis = input.required<DashboardKpis>();
  readonly currency = input.required<string>();

  protected readonly cards = computed<readonly Card[]>(() => {
    const kpis = this.kpis();

    return [
      { key: 'gross', label: 'Gross revenue', hint: 'Orders by the day they were paid, delivery included, refunded orders included.', figure: kpis.grossRevenue, kind: 'money' },
      { key: 'net', label: 'Net revenue', hint: 'Gross revenue less the refunds completed in these days.', figure: kpis.netRevenue, kind: 'money' },
      { key: 'refunds', label: 'Refunds completed', hint: 'Refunds of paid orders, by the day they completed.', figure: kpis.refundsCompleted, kind: 'money' },
      { key: 'orders', label: 'Orders', hint: 'Orders by the day they were placed, paid or not.', figure: kpis.orders, kind: 'count' },
      { key: 'paid', label: 'Paid orders', hint: 'Orders by the day they were paid.', figure: kpis.paidOrders, kind: 'count' },
      { key: 'aov', label: 'Average order value', hint: 'Gross revenue divided by paid orders.', figure: kpis.averageOrderValue, kind: 'money' },
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

  protected arrow(figure: ComparedFigure): string {
    const way = direction(figure);
    return way === 'up' ? '▲' : way === 'down' ? '▼' : '–';
  }
}
