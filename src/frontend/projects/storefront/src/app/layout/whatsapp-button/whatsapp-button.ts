import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Icon } from '@spencer-denim/shared';

import { SITE_CONFIG, whatsAppUrl } from '../../core/config/site-config';

/**
 * The floating WhatsApp button, on every storefront page (GLB-08).
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
  template: `
    <a class="wa-float" [href]="url" target="_blank" rel="noopener">
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
  protected readonly url = whatsAppUrl(inject(SITE_CONFIG));
}
