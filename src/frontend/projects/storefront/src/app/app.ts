import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { SiteFooter } from './layout/site-footer/site-footer';
import { SiteHeader } from './layout/site-header/site-header';
import { WhatsappButton } from './layout/whatsapp-button/whatsapp-button';

/**
 * The storefront shell every customer page sits inside (GLB-03..GLB-09).
 *
 * The landmarks are here and only here: one `banner`, one `main`, one `contentinfo`. Pages supply
 * the single `h1` for their own content.
 */
@Component({
  selector: 'sd-root',
  imports: [RouterOutlet, SiteFooter, SiteHeader, WhatsappButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <sd-site-header />
    <main id="main" tabindex="-1">
      <router-outlet />
    </main>
    <sd-site-footer />
    <sd-whatsapp-button />
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 100vh;
    }
    main {
      flex: 1 0 auto;
    }
    main:focus {
      outline: none;
    }
  `,
})
export class App {}
