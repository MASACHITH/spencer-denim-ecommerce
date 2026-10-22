import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { BRAND } from '@spencer-denim/shared';

import { AlertBanner } from './core/alerts/alert-banner';
import { AdminSidebar } from './core/shell/admin-sidebar';

/** Screens shown before a staff member is fully signed in: no sidebar there, only the header. */
const SIGNED_OUT_PATHS = ['/login', '/login/two-factor', '/change-password', '/two-factor/setup'];

/**
 * The admin shell: the header, the left-hand navigation, the alert banner and the router outlet.
 *
 * **The banner is here rather than on the dashboard, and the reason is in `AlertBanner`'s own
 * docblock.** Doc 08 §3.8 calls `ops.AdminAlerts` "the dashboard banner" and ADM-04's dashboard does not
 * exist — the route at `/` is W0-09's scaffold placeholder. Doc 11 §6.1 step 1's morning routine starts
 * from these banners, so a dead email, a payment exception or a sensitive settings change has to reach a
 * Super Admin wherever in the panel they happen to be, not only if they go back to the home screen. The
 * component takes no inputs and fetches for itself, so ADM-04 can render the same `<sda-alert-banner />`
 * in a dashboard widget without a second implementation.
 *
 * **The sidebar is rendered only once signed in**, and is created afresh after each sign-in, so it reads
 * the new session's permissions rather than the previous user's.
 */
@Component({
  selector: 'sda-root',
  imports: [RouterOutlet, RouterLink, AlertBanner, AdminSidebar],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell" [class.shell--nav]="showNav()">
      @if (showNav()) {
        <sda-admin-sidebar [open]="navOpen()" (closed)="navOpen.set(false)" />
      }
      <div class="shell__main">
        <header class="admin-header">
          @if (showNav()) {
            <button
              class="admin-header__menu"
              type="button"
              aria-controls="admin-sidebar"
              [attr.aria-expanded]="navOpen()"
              (click)="navOpen.set(!navOpen())"
            >
              <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M3 6h18M3 12h18M3 18h18" /></svg>
              <span class="sr-only">{{ navOpen() ? 'Close menu' : 'Open menu' }}</span>
            </button>
          }
          <a class="admin-header__brand" routerLink="/">{{ brand }}</a>
        </header>
        <sda-alert-banner />
        <main id="main">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
  styles: `
    :host { display: block; min-height: 100vh; background: var(--color-bg-alt); }
    .shell { min-height: 100vh; }
    .shell--nav { display: grid; grid-template-columns: auto minmax(0, 1fr); }
    .shell__main { min-width: 0; }
    .admin-header {
      display: flex;
      align-items: center;
      gap: var(--space-1-5);
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
    .admin-header__menu {
      display: none;
      align-items: center;
      justify-content: center;
      width: var(--tap);
      height: var(--tap);
      margin-inline-start: calc(var(--space-1) * -1);
      border: 0;
      background: transparent;
      color: inherit;
      cursor: pointer;
    }
    .admin-header__menu svg { width: 22px; height: 22px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; }
    .admin-header__menu:focus-visible { outline: 2px solid var(--color-on-primary); outline-offset: -4px; }
    @media (max-width: 1023.98px) {
      .shell--nav { display: block; }
      .admin-header__menu { display: inline-flex; }
    }
  `,
})
export class App {
  private readonly router = inject(Router);

  /** The admin application's name, from the one place it is written down (@spencer-denim/shared). */
  protected readonly brand = BRAND.adminName;

  protected readonly navOpen = signal(false);

  protected readonly showNav = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => !SIGNED_OUT_PATHS.includes(event.urlAfterRedirects.split(/[?#]/)[0])),
    ),
    { initialValue: false },
  );
}
