import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Icon } from '@spencer-denim/shared';

import { SITE_CONFIG, whatsAppUrl } from '../../core/config/site-config';
import { SiteSettingsService } from '../../core/site-settings/site-settings.service';

/**
 * The floating WhatsApp button, on every storefront page (GLB-08).
 *
 * It is its own `complementary` landmark; see the `host` block for why that is not decoration.
 *
 * The colour is WhatsApp's darker brand green (`--color-whatsapp`), the one exception to the
 * neutral palette that docs/04 §4 decision 6 allows. The bright `#25d366` was not usable: against
 * white it reaches only 1.9:1, so neither the button's edge nor the white glyph would meet WCAG
 * 2.2 AA non-text contrast. The token gives 4.1:1 for both.
 */
@Component({
  selector: 'sd-whatsapp-button',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // A landmark of its own, which is the one moderate axe has reported on every storefront page
  // since this button existed (#65: "region, on an sr-only node outside a landmark" — the sr-only
  // node is this component's).
  //
  // `app.ts` renders it after the footer, so it sits outside banner, main and contentinfo alike,
  // and content outside every landmark is content a screen-reader user navigating by region never
  // arrives at. It cannot simply move: it is `position: fixed` and site-wide, so main is the wrong
  // place (that is the page) and it cannot be nested inside the footer component's own `<footer>`
  // from here. A named complementary landmark is the honest description — a site-wide way to get in
  // touch, beside the page rather than part of it — and it costs one entry in a landmark list.
  host: { role: 'complementary', 'aria-label': 'Chat with us' },
  template: `
    <a class="wa-float" [href]="url()" target="_blank" rel="noopener">
      <sd-icon name="whatsapp" />
      <span class="sr-only">Chat with us on WhatsApp</span>
    </a>
  `,
  styles: `
    :host {
      position: fixed;
      right: var(--space-2);
      bottom: var(--space-2);
      z-index: 70;
    }
    .wa-float {
      display: grid;
      place-items: center;
      width: 56px;
      height: 56px;
      background: var(--color-whatsapp);
      color: var(--color-on-whatsapp);
      text-decoration: none;
      transition: background-color var(--duration) var(--ease);
    }
    .wa-float:hover {
      background: var(--color-whatsapp-hover);
    }
    .wa-float:focus-visible {
      outline-color: var(--color-whatsapp);
      outline-offset: 3px;
    }
    @media (min-width: 768px) {
      :host {
        right: var(--space-3);
        bottom: var(--space-3);
      }
    }
  `,
})
export class WhatsappButton {
  private readonly config = inject(SITE_CONFIG);
  private readonly number = inject(SiteSettingsService).whatsAppNumber;

  /**
   * The deep link, from the admin setting `Contact.WhatsApp` when the client has set one and from
   * `SITE_CONFIG`'s documented placeholder otherwise (ADM-22).
   *
   * A computed rather than a value read once in the constructor: the settings arrive from an HTTP call,
   * so reading them at construction time would capture the empty initial value and never update — which
   * is exactly how a server-rendered page ends up with a different href from the hydrated one.
   */
  protected readonly url = computed(() => whatsAppUrl(this.config, this.number()));
}
