import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { INVENTORY_PERMISSIONS } from '../inventory/inventory.model';
import { REPORTING_PERMISSIONS } from '../reporting/reporting.model';
import { REVIEW_PERMISSIONS } from '../reviews/reviews.model';
import { TRAFFIC_PERMISSION } from '../traffic/traffic.model';

/** What a link needs the signed-in staff member to hold; absent = every signed-in staff member sees it. */
type Gate = 'reports' | 'traffic' | 'inventory' | 'reviews';

interface NavLink {
  readonly label: string;
  readonly path: string;
  readonly icon: string;
  readonly gate?: Gate;
  /** `/` must match exactly, or it would light up on every page. */
  readonly exact?: boolean;
}

interface NavGroup {
  readonly label: string;
  readonly links: readonly NavLink[];
}

/** 24×24 stroke icons (Feather-style paths), drawn with currentColor. */
const ICON = {
  home: 'M3 10.5 12 3l9 7.5V21a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1z',
  chart: 'M4 20V10M10 20V4M16 20v-7M22 20H2',
  pulse: 'M22 12h-4l-3 9L9 3l-3 9H2',
  bag: 'M6 2 3 6v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6l-3-4zM3 6h18M16 10a4 4 0 0 1-8 0',
  bank: 'M3 10h18M5 10v8M9.5 10v8M14.5 10v8M19 10v8M2 21h20M12 3l9 5H3z',
  truck: 'M1 4h14v12H1zM15 9h4l3 3v4h-7M5.5 19a1.5 1.5 0 1 0 0-.01M18.5 19a1.5 1.5 0 1 0 0-.01',
  users: 'M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75',
  tag: 'M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L2 12V2h10l8.6 8.6a2 2 0 0 1 0 2.8zM7 7h.01',
  box: 'M21 16V8a2 2 0 0 0-1-1.7l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.7l7 4a2 2 0 0 0 2 0l7-4a2 2 0 0 0 1-1.7zM3.3 7 12 12l8.7-5M12 22V12',
  layers: 'M12 2 2 7l10 5 10-5zM2 17l10 5 10-5M2 12l10 5 10-5',
  star: 'M12 2l3.1 6.3 6.9 1-5 4.9 1.2 6.8L12 17.8 5.8 21l1.2-6.8-5-4.9 6.9-1z',
  percent: 'M19 5 5 19M6.5 9a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5M17.5 20a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5',
  sale: 'M3 3h7l11 11-7 7L3 10zM7.5 7.5h.01',
  mail: 'M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM22 6l-10 7L2 6',
  layout: 'M3 3h18v18H3zM3 9h18M9 21V9',
  megaphone: 'M3 11v2a1 1 0 0 0 1 1h3l5 4V6L7 10H4a1 1 0 0 0-1 1zM16 8a5 5 0 0 1 0 8',
  menu: 'M3 6h18M3 12h18M3 18h18',
  ruler: 'M2 16 16 2l6 6L8 22zM7 11l2 2M10 8l2 2M13 5l2 2',
  file: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M8 13h8M8 17h8',
  gear: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z',
  list: 'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01',
  user: 'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8',
  shield: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z',
  logout: 'M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9',
} as const;

/** The same sections, in the same order, as the dashboard's "Admin sections" list, grouped the way staff work. */
const GROUPS: readonly NavGroup[] = [
  {
    label: 'Overview',
    links: [
      { label: 'Dashboard', path: '/', icon: ICON.home, exact: true },
      { label: 'Reports', path: '/reports', icon: ICON.chart, gate: 'reports' },
      { label: 'Website traffic', path: '/traffic', icon: ICON.pulse, gate: 'traffic' },
    ],
  },
  {
    label: 'Sales',
    links: [
      { label: 'Orders', path: '/orders', icon: ICON.bag, exact: true },
      { label: 'Bank transfers', path: '/orders/bank-transfers', icon: ICON.bank },
      { label: 'Couriers', path: '/orders/couriers', icon: ICON.truck },
      { label: 'Customers', path: '/customers', icon: ICON.users },
    ],
  },
  {
    label: 'Catalogue',
    links: [
      { label: 'Products', path: '/products', icon: ICON.tag },
      { label: 'Inventory', path: '/inventory', icon: ICON.box, gate: 'inventory' },
      { label: 'Departments and categories', path: '/catalog', icon: ICON.layers },
      { label: 'Reviews', path: '/reviews', icon: ICON.star, gate: 'reviews' },
    ],
  },
  {
    label: 'Marketing',
    links: [
      { label: 'Promo codes', path: '/marketing/promo-codes', icon: ICON.percent },
      { label: 'Sale campaigns', path: '/marketing/sale-campaigns', icon: ICON.sale },
      { label: 'Newsletter subscribers', path: '/marketing/newsletter', icon: ICON.mail },
    ],
  },
  {
    label: 'Content',
    links: [
      { label: 'Homepage', path: '/content/home', icon: ICON.layout },
      { label: 'Announcement bar', path: '/content/announcements', icon: ICON.megaphone },
      { label: 'Mega menu cards', path: '/content/mega-menu', icon: ICON.menu },
      { label: 'Size guides', path: '/content/size-guides', icon: ICON.ruler },
      { label: 'Content pages', path: '/content/pages', icon: ICON.file },
    ],
  },
  {
    label: 'Administration',
    links: [
      { label: 'Settings', path: '/settings', icon: ICON.gear },
      { label: 'Audit log', path: '/audit-log', icon: ICON.list },
      { label: 'Staff users', path: '/users', icon: ICON.user },
      { label: 'Roles', path: '/roles', icon: ICON.shield },
    ],
  },
];

/**
 * The admin panel's left-hand navigation: every section, grouped, with the current page highlighted.
 *
 * Links behind a permission (reports, traffic, inventory, reviews) follow the same rule as the dashboard's
 * section list: shown when the session holds the permission, all shown if the session cannot be read (the API
 * still refuses what the role may not do). On a wide screen it is a fixed column; below 1024 px it is a drawer
 * the header's menu button opens, closed by the backdrop, Escape or following a link.
 */
@Component({
  selector: 'sda-admin-sidebar',
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.is-open]': 'open()',
    '(document:keydown.escape)': 'open() && closed.emit()',
  },
  template: `
    <div class="sidebar__backdrop" (click)="closed.emit()" aria-hidden="true"></div>
    <nav class="sidebar" id="admin-sidebar" aria-label="Admin sections" data-testid="admin-sidebar">
      <div class="sidebar__scroll">
        @for (group of groups(); track group.label) {
          <p class="sidebar__group">{{ group.label }}</p>
          <ul class="sidebar__list">
            @for (link of group.links; track link.path) {
              <li>
                <a
                  class="sidebar__link"
                  [routerLink]="link.path"
                  routerLinkActive="is-active"
                  ariaCurrentWhenActive="page"
                  [routerLinkActiveOptions]="{ exact: !!link.exact }"
                  (click)="closed.emit()"
                >
                  <svg class="sidebar__icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path [attr.d]="link.icon" /></svg>
                  <span>{{ link.label }}</span>
                </a>
              </li>
            }
          </ul>
        }
      </div>
      <div class="sidebar__foot">
        <button class="sidebar__link sidebar__signout" type="button" data-testid="sidebar-logout" [attr.aria-disabled]="loggingOut() ? 'true' : null" (click)="logout()">
          <svg class="sidebar__icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path [attr.d]="logoutIcon" /></svg>
          <span>{{ loggingOut() ? 'Signing out…' : 'Sign out' }}</span>
        </button>
      </div>
    </nav>
  `,
  styles: `
    :host { --sidebar-width: 256px; display: contents; }
    .sidebar {
      position: sticky;
      top: 0;
      display: flex;
      flex-direction: column;
      width: var(--sidebar-width);
      height: 100vh;
      height: 100dvh;
      background: var(--color-primary);
      color: var(--color-on-primary);
      border-inline-end: 1px solid rgb(255 255 255 / 0.08);
    }
    .sidebar__scroll { flex: 1; overflow-y: auto; padding: var(--space-2) var(--space-2) var(--space-3); }
    .sidebar__group {
      margin: var(--space-3) var(--space-1-5) var(--space-1);
      font-size: 11px;
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      opacity: 0.55;
    }
    .sidebar__group:first-child { margin-top: var(--space-1); }
    .sidebar__list { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
    .sidebar__link {
      display: flex;
      align-items: center;
      gap: var(--space-1-5);
      width: 100%;
      min-height: 40px;
      padding: var(--space-1) var(--space-1-5);
      border: 0;
      border-radius: var(--radius);
      background: transparent;
      color: inherit;
      font: inherit;
      font-size: var(--text-small);
      text-align: start;
      text-decoration: none;
      opacity: 0.82;
      cursor: pointer;
      transition: background-color 120ms ease, opacity 120ms ease;
    }
    .sidebar__link:hover { background: rgb(255 255 255 / 0.08); opacity: 1; }
    .sidebar__link:focus-visible { outline: 2px solid var(--color-on-primary); outline-offset: -2px; opacity: 1; }
    .sidebar__link.is-active {
      background: rgb(255 255 255 / 0.14);
      font-weight: var(--weight-semibold);
      opacity: 1;
      box-shadow: inset 3px 0 0 var(--color-on-primary);
    }
    .sidebar__icon {
      flex: none;
      width: 18px;
      height: 18px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.8;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .sidebar__foot { padding: var(--space-2); border-top: 1px solid rgb(255 255 255 / 0.1); }
    .sidebar__signout[aria-disabled='true'] { cursor: progress; }
    .sidebar__backdrop { display: none; }

    @media (max-width: 1023.98px) {
      .sidebar {
        position: fixed;
        inset-block: 0;
        inset-inline-start: 0;
        z-index: 40;
        max-width: 85vw;
        transform: translateX(-100%);
        visibility: hidden;
        pointer-events: none;
        transition: transform 200ms ease, visibility 0s linear 200ms;
      }
      :host(.is-open) .sidebar { transform: none; visibility: visible; pointer-events: auto; transition: transform 200ms ease; }
      :host(.is-open) .sidebar__backdrop { display: block; position: fixed; inset: 0; z-index: 39; background: var(--color-scrim); }
    }
    @media (prefers-reduced-motion: reduce) {
      .sidebar, :host(.is-open) .sidebar { transition: none; }
    }
  `,
})
export class AdminSidebar {
  private readonly auth = inject(AdminAuthApi);
  private readonly router = inject(Router);

  /** The drawer state below 1024 px; ignored on a wide screen, where the column is always shown. */
  readonly open = input(false);
  readonly closed = output<void>();

  protected readonly logoutIcon = ICON.logout;
  protected readonly loggingOut = signal(false);
  private readonly holds = signal<Record<Gate, boolean> | null>(null);

  protected readonly groups = computed(() => {
    const holds = this.holds();
    return GROUPS.map((group) => ({ ...group, links: group.links.filter((link) => !link.gate || !holds || holds[link.gate]) })).filter(
      (group) => group.links.length > 0,
    );
  });

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) => {
        const has = (permission: string) => session.grantsAllPermissions || session.permissions.includes(permission);
        this.holds.set({
          reports: has(REPORTING_PERMISSIONS.reports),
          traffic: has(TRAFFIC_PERMISSION),
          inventory: has(INVENTORY_PERMISSIONS.view),
          reviews: has(REVIEW_PERMISSIONS.view),
        });
      },
      // The session could not be read: show every link and let the API decide, as the dashboard does.
      error: () => this.holds.set(null),
    });
  }

  protected logout(): void {
    // aria-disabled plus this refusal, never [disabled]: a disabled button throws focus to <body> (#109).
    if (this.loggingOut()) {
      return;
    }

    this.loggingOut.set(true);
    const leave = () => {
      this.loggingOut.set(false);
      this.closed.emit();
      void this.router.navigateByUrl('/login');
    };

    this.auth.logout().subscribe({ next: leave, error: leave });
  }
}
