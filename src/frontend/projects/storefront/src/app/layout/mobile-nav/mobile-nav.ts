import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Drawer, Icon } from '@spencer-denim/shared';

import { AccountStore } from '../../core/account/account.store';
import { NavigationService } from '../../core/navigation/navigation.service';
import { NavItem, hasMenu } from '../../core/navigation/navigation.model';
import { SITE_CONFIG, whatsAppUrl } from '../../core/config/site-config';
import { SiteSettingsService } from '../../core/site-settings/site-settings.service';
import { StorefrontCounts } from '../../core/storefront-counts.service';

/**
 * The phone and tablet navigation (GLB-05): a slide-out drawer whose departments expand in place.
 *
 * The drawer itself (focus trap, Escape, focus return) is the shared `sd-drawer`. This component
 * only supplies what is inside it. One department is open at a time, which keeps the list short
 * enough to scan at 320 px.
 */
@Component({
  selector: 'sd-mobile-nav',
  imports: [Drawer, Icon, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-drawer
      [open]="open()"
      panelId="storefront-menu"
      heading="Menu"
      closeLabel="Close menu"
      (closed)="closed.emit()"
    >
      <nav aria-label="Main menu">
        <ul class="m-nav">
          @for (item of items(); track item.id) {
            <li>
              @if (menuOf(item)) {
                <button
                  class="m-nav__toggle"
                  type="button"
                  [attr.aria-expanded]="expandedId() === item.id"
                  [attr.aria-controls]="panelId(item)"
                  (click)="toggle(item.id)"
                >
                  <span>{{ item.label }}</span>
                  <sd-icon
                    class="m-nav__chevron"
                    [class.is-open]="expandedId() === item.id"
                    name="chevron-down"
                    size="sm"
                  />
                </button>
                <ul class="m-nav__panel" [id]="panelId(item)" [hidden]="expandedId() !== item.id">
                  @for (link of linksOf(item); track link.path) {
                    <li>
                      <a
                        class="m-nav__sub"
                        [class.m-nav__sub--child]="link.depth > 0"
                        [routerLink]="link.path"
                        (click)="closed.emit()"
                        >{{ link.label }}</a
                      >
                    </li>
                  }
                </ul>
              } @else {
                <a
                  class="m-nav__link"
                  [class.text-sale]="item.tone === 'sale'"
                  [routerLink]="item.path"
                  (click)="closed.emit()"
                  >{{ item.label }}</a
                >
              }
            </li>
          }
        </ul>
      </nav>

      <ul class="m-nav m-nav--secondary">
        <!-- W3-01. **The address is unchanged**: '/account' is a real route now, and its child table
             sends a signed-in shopper to their orders and a signed-out one to the sign-in form with
             'returnTo'. Only the words change, and only once the shop knows who is here — 'customer()'
             is null on the server for every visitor, because 'AccountStore' does not ask during a render
             ('sd_customer_auth' is not forwarded to the API), so this row reads "Sign in / Register" in
             the server's markup and keeps reading it for everyone who is not signed in. -->
        <li>
          <a class="m-nav__link" routerLink="/account" (click)="closed.emit()">
            <sd-icon name="user" size="sm" />
            @if (customer(); as me) {
              <span data-testid="m-nav-account">Your account ({{ me.firstName }})</span>
            } @else {
              <span data-testid="m-nav-account">Sign in / Register</span>
            }
          </a>
        </li>
        @if (customer()) {
          <!-- The drawer is the only place a phone can sign out: the header's own control is
               'display: none' below 1024 px (see 'site-header.ts'). It is a row in this list rather than
               a button beside the account row, so it is the same 56/44 px target as every other row and
               cannot be mistaken for part of the link above it.

               It asks the header rather than signing out itself. The header already owns the bag the
               sign-out has to re-read and the focus that has to be re-homed, and two components calling
               'AccountStore.signOut' would be two answers to what happens afterwards. -->
          <li>
            <button class="m-nav__link" type="button" data-testid="m-nav-sign-out" (click)="requestSignOut()">
              <sd-icon name="user" size="sm" /> <span>Sign out</span>
            </button>
          </li>
        }
        <li>
          <a class="m-nav__link" routerLink="/wishlist" (click)="closed.emit()">
            <sd-icon name="heart" size="sm" /> <span>Wishlist ({{ wishlistCount() }})</span>
          </a>
        </li>
        <!-- Only when the client has set 'Contact.WhatsApp' (W2-11). A row in the menu that opens a
             chat with nobody is worse than one row fewer, and the @if wraps the <li> rather than the
             <a> so the list does not keep an empty item's spacing. -->
        @if (whatsApp(); as href) {
          <li>
            <a class="m-nav__link" [href]="href" rel="noopener" target="_blank">
              <sd-icon name="whatsapp" size="sm" /> <span>Chat on WhatsApp</span>
            </a>
          </li>
        }
      </ul>
    </sd-drawer>
  `,
  styles: `
    .m-nav {
      list-style: none;
    }
    .m-nav > li {
      border-bottom: 1px solid var(--color-border);
    }
    .m-nav__toggle,
    .m-nav__link {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1);
      width: 100%;
      min-height: 56px;
      padding: 0;
      border: 0;
      background: none;
      font-size: var(--text-button);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-align: left;
      text-decoration: none;
      cursor: pointer;
    }
    .m-nav__chevron {
      transition: transform var(--duration) var(--ease);
    }
    .m-nav__chevron.is-open {
      transform: rotate(180deg);
    }
    .m-nav__panel {
      list-style: none;
      padding-bottom: var(--space-1);
    }
    .m-nav__sub {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      padding-inline-start: var(--space-2);
      font-size: var(--text-small);
      text-decoration: none;
    }
    .m-nav__sub--child {
      padding-inline-start: var(--space-4);
      color: var(--color-text-muted);
    }
    .m-nav__sub:hover {
      text-decoration: underline;
      text-underline-offset: 4px;
    }
    .m-nav--secondary {
      margin-top: var(--space-3);
    }
    .m-nav--secondary > li {
      border-bottom: 0;
    }
    .m-nav--secondary .m-nav__link {
      justify-content: flex-start;
      min-height: var(--tap);
      text-transform: none;
      letter-spacing: 0;
      font-size: var(--text-small);
      font-weight: var(--weight-regular);
    }
  `,
})
export class MobileNav {
  readonly open = input.required<boolean>();
  readonly closed = output<void>();

  /**
   * The shopper asked to sign out (W3-01, ACC-01).
   *
   * An output rather than the work, because `SiteHeader` owns what has to happen afterwards: the bag is
   * re-read (doc 11 P11 step B.7 — the guest bag is not restored, so the badge really is zero) and focus
   * is re-homed onto the wordmark, because this button is destroyed by its own success. Doing it here as
   * well would be two answers to one question.
   */
  readonly signOutRequested = output<void>();

  /**
   * The signed-in customer, or null (W3-01).
   *
   * Null on the server for everyone and null until `GET /api/account/me` answers, which is why the
   * `@else` branch in the template — "Sign in / Register" — is both the server's markup and the
   * signed-out state. `AccountStore` keeps "nobody has asked" and "signed out" apart, so this drawer
   * never greets somebody and then takes it back.
   */
  protected readonly customer = inject(AccountStore).customer;

  private readonly navigation = inject(NavigationService);
  private readonly counts = inject(StorefrontCounts);

  readonly items = this.navigation.items;
  protected readonly wishlistCount = this.counts.wishlist;
  private readonly siteConfig = inject(SITE_CONFIG);
  private readonly whatsAppNumber = inject(SiteSettingsService).whatsAppNumber;

  /** <inheritdoc cref="WhatsappButton.url" /> — the same setting, the same fallback (ADM-22). */
  protected readonly whatsApp = computed(() => whatsAppUrl(this.siteConfig, this.whatsAppNumber()));

  /** Closes the drawer and asks the header to sign out. In that order, so the focus trap is gone. */
  protected requestSignOut(): void {
    this.closed.emit();
    this.signOutRequested.emit();
  }

  private readonly expanded = signal<string | null>(null);

  /** The department section that is expanded, or `null`. */
  readonly expandedId = this.expanded.asReadonly();

  /** Every department's menu flattened to a single indented list, "Shop all" first. */
  private readonly flattened = computed(() =>
    new Map(
      this.items().map((item) => [
        item.id,
        [
          ...(item.shopAll
            ? [{ label: item.shopAll.label, path: item.shopAll.path, depth: 0 }]
            : []),
          ...item.columns.flatMap((column) =>
            column.groups.flatMap((group) => [
              { label: group.link.label, path: group.link.path, depth: 0 },
              ...group.children.map((child) => ({
                label: child.label,
                path: child.path,
                depth: 1,
              })),
            ]),
          ),
        ],
      ]),
    ),
  );

  protected menuOf(item: NavItem): boolean {
    return hasMenu(item);
  }

  protected panelId(item: NavItem): string {
    return `m-nav-${item.id}`;
  }

  protected linksOf(item: NavItem): readonly { label: string; path: string; depth: number }[] {
    return this.flattened().get(item.id) ?? [];
  }

  toggle(id: string): void {
    this.expanded.update((current) => (current === id ? null : id));
  }
}
