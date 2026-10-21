import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';

import { ContentPageService } from '../../core/content/content-page.service';
import { CONTENT_PAGE_KEYS } from '../../core/content/content-page.model';
import { DeliveryRates } from '../../core/delivery/delivery-rates';
import { formatWorkingDays } from '../../core/delivery/delivery-returns.model';
import { DeliveryReturnsService } from '../../core/delivery/delivery-returns.service';

/**
 * The product page's delivery and returns panel (PDP-05, issue #33): two disclosures under the product details.
 *
 * **Delivery** holds the Sri Lankan delivery estimate and every method's fee, free-delivery amount and working
 * days per zone — all from `sales.DeliveryMethodZoneRates` through the API, none typed here — then store pickup
 * when, and only when, the API lists a pickup point; then the owner's short delivery text; then a link to the
 * Shipping page. **Returns and exchanges** holds the owner's short returns text and a link to the Returns page.
 *
 * **The same for every visitor**, which is what lets the server-rendered page be cached: no district, no
 * account. The exact fee for a shopper's district — or, overseas, their country — is checkout's to show.
 *
 * **Where overseas delivery plugs in.** The Sri Lankan rates are one region, under their own heading. The
 * international-delivery work (`feature/intl-delivery`) adds a second region — "Delivery to {country}" — by
 * projecting it into the `[sdDeliveryRegion]` slot below, beside the Sri Lankan one, so neither has to be
 * rewritten. Until then the owner's delivery text carries the worldwide line.
 *
 * **Native `<details>`, as the rest of the details list**, so it opens with no script, is keyboard-operable by
 * definition and keeps its contents in the server-rendered HTML whether open or shut. A link appears only when its
 * page is published — a link to a page that answers 404 is worse than none. If the panel cannot be read at all,
 * nothing is drawn: the page under it is a product, and it must render whatever happens to delivery fees.
 */
@Component({
  selector: 'sd-product-delivery-returns',
  imports: [DeliveryRates, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (info(); as panel) {
      <details class="dr__block" data-testid="pdp-delivery">
        <summary class="dr__summary">Delivery</summary>
        <div class="dr__body">
          @if (panel.methods.length > 0 || panel.pickupPoints.length > 0) {
            <section class="dr__region" aria-labelledby="pdp-delivery-lk" data-testid="pdp-delivery-lk">
              <p class="dr__heading" id="pdp-delivery-lk">Within Sri Lanka</p>
              @if (estimate(); as days) {
                <p class="dr__estimate" data-testid="pdp-delivery-estimate">
                  Delivered in {{ days }} after your payment is confirmed, depending on your district and the
                  delivery method.
                </p>
              }
              <sd-delivery-rates [info]="panel" idPrefix="pdp-rates" />
            </section>
          }

          <ng-content select="[sdDeliveryRegion]" />

          @if (panel.deliveryInfoHtml) {
            <!-- Sanitised by the API on save and on read, and bound so Angular's sanitiser runs over it too. -->
            <div class="dr__text rich" data-testid="pdp-delivery-text" [innerHTML]="panel.deliveryInfoHtml"></div>
          }

          @if (shippingPath(); as path) {
            <p class="dr__more"><a class="link" [routerLink]="path" data-testid="pdp-shipping-link">Delivery details</a></p>
          }
        </div>
      </details>

      @if (panel.returnsInfoHtml || returnsPath()) {
        <details class="dr__block" data-testid="pdp-returns">
          <summary class="dr__summary">Returns and exchanges</summary>
          <div class="dr__body">
            @if (panel.returnsInfoHtml) {
              <div class="dr__text rich" data-testid="pdp-returns-text" [innerHTML]="panel.returnsInfoHtml"></div>
            }
            @if (returnsPath(); as path) {
              <p class="dr__more">
                <a class="link" [routerLink]="path" data-testid="pdp-returns-link">Returns and exchanges policy</a>
              </p>
            }
          </div>
        </details>
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .dr__block {
      border-bottom: 1px solid var(--color-border);
    }
    .dr__summary {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      padding-block: var(--space-1);
      font-size: var(--text-small);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
    }
    .dr__body {
      padding-bottom: var(--space-2);
      max-width: 62ch;
      color: var(--color-text-muted);
    }
    .dr__region + .dr__text,
    .dr__region + .dr__more {
      margin-top: var(--space-1-5, 0.75rem);
    }
    .dr__heading {
      margin: 0 0 var(--space-0-5, 0.25rem);
      font-size: var(--text-small);
      font-weight: var(--weight-medium);
      color: var(--color-text);
    }
    .dr__estimate {
      margin: 0 0 var(--space-1);
    }
    .dr__text {
      margin-top: var(--space-1);
      overflow-wrap: anywhere;
    }
    .dr__text :is(ul, ol) {
      padding-left: 1.25rem;
    }
    .dr__more {
      margin: var(--space-1) 0 0;
    }
    .dr__more a {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
    }
  `,
})
export class ProductDeliveryReturns {
  private readonly content = inject(ContentPageService);

  protected readonly info = toSignal(inject(DeliveryReturnsService).load(), { initialValue: null });

  protected readonly estimate = computed(() => {
    const estimate = this.info()?.estimate;
    return estimate ? formatWorkingDays(estimate.daysMin, estimate.daysMax) : null;
  });

  /** The published Shipping page's address, found by system key because the slug is editable. */
  protected readonly shippingPath = computed(() => pathOf(this.content, CONTENT_PAGE_KEYS.shipping));

  protected readonly returnsPath = computed(() => pathOf(this.content, CONTENT_PAGE_KEYS.returns));
}

function pathOf(content: ContentPageService, systemKey: string): string | null {
  const page = content.bySystemKey(systemKey);
  return page ? `/${page.slug}` : null;
}
