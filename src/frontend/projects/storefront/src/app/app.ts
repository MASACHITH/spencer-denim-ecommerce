import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { SiteFooter } from './layout/site-footer/site-footer';
import { SiteHeader } from './layout/site-header/site-header';
import { WhatsappButton } from './layout/whatsapp-button/whatsapp-button';

/**
 * The storefront shell every customer page sits inside (GLB-03..GLB-09).
 *
 * The landmarks are here and only here: one `banner`, one `main`, one `contentinfo`. Pages supply
 * the single `h1` for their own content.
 *
 * **The skip link is handled here, not left to its `href`.** `index.html` has `<base href="/">`, so a plain
 * `href="#main"` resolves to `/#main`, and activating it from any page but the home page did a full load of the
 * home page: a keyboard user asking to skip to the content was sent somewhere else. The click is taken over
 * instead: focus moves into `main` (it has `tabindex="-1"` for this), and `scrollIntoView` brings it up under the
 * sticky header, because it honours the `scroll-padding-top` that `base.css` keeps equal to the header's height.
 * (Angular's own `scrollToAnchor` ignores that padding and left the first 126 to 159 px of the content behind the
 * header.) The listener is on the document, from `host`, rather than on the link in the template, so the
 * server-rendered markup does not change: a listener on the link would add a `jsaction` attribute to it. A click
 * in the moments before the page hydrates, or with scripts off, still follows the `href`.
 */
@Component({
  selector: 'sd-root',
  imports: [RouterOutlet, SiteFooter, SiteHeader, WhatsappButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:click)': 'onDocumentClick($event)' },
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
export class App {
  private readonly document = inject(DOCUMENT);

  protected onDocumentClick(event: MouseEvent): void {
    const target = event.target;

    if (!(target instanceof Element) || !target.closest('a.skip-link')) {
      return;
    }

    event.preventDefault();

    // Looked up by id and not through a template reference: a reference takes a slot in the component's view, which
    // moves the router outlet's index in the hydration data the server writes, and the server-rendered state
    // should stay as it was.
    const main = this.document.getElementById('main');
    main?.focus({ preventScroll: true });
    main?.scrollIntoView({ block: 'start' });
  }
}
