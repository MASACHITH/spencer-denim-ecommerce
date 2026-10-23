import { ChangeDetectionStrategy, Component, afterNextRender, computed, inject, input, signal } from '@angular/core';

import { formatLkr } from '../catalog/money';
import { DeliveryDistrictService } from './delivery-district.service';
import { DeliveryRateDto, DeliveryReturnsDto, DeliveryZoneName, formatWorkingDays } from './delivery-returns.model';

/** One line of the rate list: a zone and what delivery there costs and takes. */
interface RateLine {
  readonly zone: DeliveryZoneName;
  readonly label: string;
  readonly text: string;
}

/**
 * What one rate says, in checkout's own terms: a fee of nought, or a free-delivery threshold of nought, is "Free";
 * otherwise the fee, then "free on orders of Rs. X or more after discounts" — checkout compares the items subtotal
 * **after** the promo discount with `>=` (`OrderPricing`, doc 08 §6 step 10) — then the working days.
 */
export function describeRate(rate: DeliveryRateDto): string {
  const free = rate.fee === 0 || rate.freeAboveAmount === 0;

  return [
    free ? 'Free' : formatLkr(rate.fee),
    !free && rate.freeAboveAmount !== null
      ? `free on orders of ${formatLkr(rate.freeAboveAmount)} or more after discounts`
      : null,
    formatWorkingDays(rate.daysMin, rate.daysMax),
  ]
    .filter((part): part is string => part !== null)
    .join(', ');
}

interface MethodBlock {
  readonly code: string;
  readonly name: string;
  readonly lines: readonly RateLine[];
}

/**
 * The Sri Lankan delivery fees, method by method and zone by zone (issue #33), drawn from the zone rates the API
 * sends. Used under the product page's Delivery disclosure and on the Shipping page.
 *
 * **A definition list per method, not a table**, so it reflows to one column at 320 px with nothing to scroll
 * sideways, and a screen reader hears "Colombo: Rs. 400, free on orders of Rs. 10,000 or more after discounts, 1–2 working days" as one pair (commas rather than middle dots, which some readers announce).
 * Every number here came from `sales.DeliveryMethodZoneRates`; none is typed into the template.
 */
@Component({
  selector: 'sd-delivery-rates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (method of methods(); track method.code) {
      <div class="rates__method" [attr.data-testid]="'delivery-method-' + method.code">
        <p class="rates__name" [id]="idPrefix() + '-' + method.code">{{ method.name }}</p>
        <dl class="rates__list" [attr.aria-labelledby]="idPrefix() + '-' + method.code">
          @for (line of method.lines; track line.zone) {
            <div class="rates__row" [class.rates__row--yours]="line.zone === yourZone()">
              <dt>
                {{ line.label }}
                @if (line.zone === yourZone()) {
                  <span class="rates__yours" data-testid="delivery-your-district">your district, {{ yourDistrict() }}</span>
                }
              </dt>
              <dd>{{ line.text }}</dd>
            </div>
          }
        </dl>
      </div>
    }

    @if (info().pickupPoints.length > 0) {
      <div class="rates__method" data-testid="delivery-pickup">
        <p class="rates__name" [id]="idPrefix() + '-pickup'">Store pickup</p>
        <ul class="rates__points" [attr.aria-labelledby]="idPrefix() + '-pickup'">
          @for (point of info().pickupPoints; track point.name) {
            <li>
              <span class="rates__point-name">{{ point.name }}, {{ point.city }}</span>
              <span>{{ price(point.fee) }}, ready in {{ days(point.daysMin, point.daysMax) }}</span>
              @if (point.openingHours) {
                <span class="rates__hours">{{ point.openingHours }}</span>
              }
            </li>
          }
        </ul>
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .rates__method + .rates__method {
      margin-top: var(--space-1-5, 0.75rem);
    }
    .rates__name {
      margin: 0 0 var(--space-0-5, 0.25rem);
      color: var(--color-text);
      font-weight: var(--weight-medium);
    }
    .rates__list,
    .rates__points {
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .rates__row {
      display: grid;
      grid-template-columns: minmax(7rem, 11rem) minmax(0, 1fr);
      gap: var(--space-1);
      padding-block: 0.125rem;
    }
    .rates__row dt {
      color: var(--color-text);
    }
    .rates__row dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .rates__row > * {
      min-width: 0;
    }
    .rates__row--yours {
      box-shadow: inset 3px 0 0 var(--color-text);
      padding-left: var(--space-1);
    }
    .rates__yours {
      display: block;
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .rates__points li {
      display: flex;
      flex-direction: column;
      padding-block: 0.125rem;
      overflow-wrap: anywhere;
    }
    .rates__point-name {
      color: var(--color-text);
    }
    @media (max-width: 479px) {
      .rates__row {
        grid-template-columns: minmax(0, 1fr);
        gap: 0;
        padding-block: 0.25rem;
      }
    }
  `,
})
export class DeliveryRates {
  readonly info = input.required<DeliveryReturnsDto>();

  private readonly district = inject(DeliveryDistrictService);

  /**
   * False on the server and during hydration, so the server-rendered list is the same for everybody and the
   * browser's first render matches it; the shopper's district is marked only once the page is live.
   */
  private readonly hydrated = signal(false);

  /** The district the shopper chose in the header ("Deliver to"), once hydrated; null until they choose one. */
  protected readonly yourDistrict = computed(() => (this.hydrated() ? (this.district.chosen()?.name ?? null) : null));

  /** The zone that district is in, by the API's own district lists (the zone assignment is admin-editable). */
  protected readonly yourZone = computed<DeliveryZoneName | null>(() => {
    const name = this.yourDistrict();
    if (!name) {
      return null;
    }

    const wanted = name.toLowerCase();
    return this.info().zones.find((zone) => zone.districts.some((candidate) => candidate.toLowerCase() === wanted))?.zone ?? null;
  });

  constructor() {
    afterNextRender(() => this.hydrated.set(true));
  }

  /** Distinguishes the ids when the list appears twice on one page. */
  readonly idPrefix = input('delivery-rates');

  protected readonly methods = computed<readonly MethodBlock[]>(() => {
    const info = this.info();
    const labels = new Map(info.zones.map((zone) => [zone.zone, zone.label]));

    return info.methods.map((method) => ({
      code: method.code,
      name: method.name,
      lines: method.rates
        .filter((rate) => labels.has(rate.zone))
        .map((rate) => ({ zone: rate.zone, label: labels.get(rate.zone)!, text: describeRate(rate) })),
    }));
  });

  protected price(fee: number): string {
    return fee === 0 ? 'Free' : formatLkr(fee);
  }

  protected days(min: number, max: number): string {
    return formatWorkingDays(min, max);
  }
}
