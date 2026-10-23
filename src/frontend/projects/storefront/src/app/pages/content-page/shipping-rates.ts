import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';

import { DeliveryRates } from '../../core/delivery/delivery-rates';
import { DeliveryReturnsService } from '../../core/delivery/delivery-returns.service';

/**
 * The live Sri Lankan delivery fees under the Shipping page's copy (issue #33).
 *
 * The page's words are the owner's and deliberately quote no fee — a typed figure would drift from checkout the
 * first time a rate changed — so the figures are drawn here from the same zone rates the product page and
 * checkout read. Nothing is drawn when the rates cannot be read or nothing is on offer; the copy above stands on
 * its own.
 */
@Component({
  selector: 'sd-shipping-rates',
  imports: [DeliveryRates],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (info(); as panel) {
      @if (panel.methods.length > 0 || panel.pickupPoints.length > 0) {
        <section class="rates" aria-labelledby="shipping-rates-title" data-testid="shipping-rates">
          @if (inline()) {
            <h3 class="rates__title" id="shipping-rates-title">Current delivery fees in Sri Lanka</h3>
          } @else {
            <h2 class="rates__title" id="shipping-rates-title">Current delivery fees in Sri Lanka</h2>
          }
          <sd-delivery-rates [info]="panel" idPrefix="shipping-rates" />
          <p class="rates__note t-small muted">
            Checkout shows the exact fee for your district, or for your country if you are ordering from
            overseas, before you pay.
          </p>
        </section>
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .rates {
      margin-top: var(--space-3, 1.5rem);
      max-width: 62ch;
    }
    .rates__note {
      margin-top: var(--space-1);
    }
  `,
})
export class ShippingRates {
  /** True when drawn inside the copy's "Delivery within Sri Lanka" section, so its heading sits one level below. */
  readonly inline = input(false);

  protected readonly info = toSignal(inject(DeliveryReturnsService).load(), { initialValue: null });
}
