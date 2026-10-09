import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { BRAND } from '@spencer-denim/shared';

import { AlertBanner } from './core/alerts/alert-banner';

/**
 * The admin shell: the header, the alert banner and the router outlet.
 *
 * **The banner is here rather than on the dashboard, and the reason is in `AlertBanner`'s own
 * docblock.** Doc 08 §3.8 calls `ops.AdminAlerts` "the dashboard banner" and ADM-04's dashboard does not
 * exist — the route at `/` is W0-09's scaffold placeholder. Doc 11 §6.1 step 1's morning routine starts
 * from these banners, so a dead email, a payment exception or a sensitive settings change has to reach a
 * Super Admin wherever in the panel they happen to be, not only if they go back to the home screen. The
 * component takes no inputs and fetches for itself, so ADM-04 can render the same `<sda-alert-banner />`
 * in a dashboard widget without a second implementation.
 */
@Component({
  selector: 'sda-root',
  imports: [RouterOutlet, RouterLink, AlertBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="admin-header">
      <a class="admin-header__brand" routerLink="/">{{ brand }}</a>
    </header>
    <sda-alert-banner />
    <main id="main">
      <router-outlet />
    </main>
  `,
  styles: `
    :host { display: block; min-height: 100vh; background: var(--color-bg-alt); }
    .admin-header {
      display: flex;
      align-items: center;
      min-height: calc(var(--tap) + var(--space-2));
      padding-inline: var(--gutter);
      background: var(--color-primary);
      color: var(--color-on-primary);
    }
    .admin-header__brand {
      font-size: var(--text-small);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-decoration: none;
    }
    .admin-header__brand:focus-visible { outline-color: var(--color-on-primary); }
  `,
})
export class App {
  /** The admin application's name, from the one place it is written down (@spencer-denim/shared). */
  protected readonly brand = BRAND.adminName;
}
