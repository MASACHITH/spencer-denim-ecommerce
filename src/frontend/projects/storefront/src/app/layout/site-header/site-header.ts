import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { BRAND, Icon } from '@spencer-denim/shared';

import { AnnouncementBar } from '../announcement-bar/announcement-bar';
import { DistrictSelector } from '../district-selector/district-selector';
import { HeaderSearch } from '../header-search/header-search';
import { MainNav } from '../main-nav/main-nav';
import { MiniBag } from '../mini-bag/mini-bag';
import { MobileNav } from '../mobile-nav/mobile-nav';
import { AccountStore } from '../../core/account/account.store';
import { CartStore } from '../../core/cart/cart.store';
import { SiteSettingsService } from '../../core/site-settings/site-settings.service';
import { StorefrontCounts } from '../../core/storefront-counts.service';

/**
 * The sticky storefront header (GLB-03, GLB-04, GLB-05, GLB-06).
 *
 * Layout is one wrapping flex row, so nothing has to be duplicated between breakpoints: the
 * hamburger and the district strip appear below 1024 px, the horizontal navigation above it, and
 * the same markup serves both. `position: sticky` keeps the header in place on scroll without the
 * layout shift a fixed header would cause.
 */
@Component({
  selector: 'sd-site-header',
  imports: [AnnouncementBar, DistrictSelector, HeaderSearch, Icon, MainNav, MiniBag, MobileNav, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Escape anywhere in the header closes the search panel; the field itself is not the only
  // place focus can be when a shopper wants out of it.
  host: { '(keydown.escape)': 'closeSearch()' },
  template: `
    <header class="site-header">
      <sd-announcement-bar />

      <div class="site-header__bar">
        <div class="container site-header__inner">
          <button
            class="icon-btn site-header__menu"
            type="button"
            aria-label="Open menu"
            aria-controls="storefront-menu"
            [attr.aria-expanded]="menuOpen()"
            data-testid="menu-toggle"
            (click)="openMenu()"
          >
            <sd-icon name="menu" />
          </button>

          <!-- GLB-02. The logo when the client has uploaded one, the text wordmark otherwise — and the
               wordmark is the normal state, not an error state: nothing is uploaded on a fresh
               deployment, so it is what most visitors see and what every test defaults to.

               One anchor either way, so the link to the home page, its tap target and its focus ring
               are the same object in both states and cannot drift apart. The img carries explicit
               width/height so the box is reserved before the bytes arrive (CLS), and alt is never
               blank: SiteSettingsService falls back to the shop's name, because this image *is* the
               site's name and a decorative-empty alt would lose it (GLB-12, WCAG 1.1.1). -->
          <a
            class="wordmark site-header__wordmark"
            routerLink="/"
            data-testid="wordmark"
            #wordmarkLink
          >
            @if (logo(); as mark) {
              <img
                class="site-header__logo"
                data-testid="logo"
                [src]="mark.url"
                [alt]="mark.alt"
                [attr.width]="mark.width"
                [attr.height]="mark.height"
                decoding="async"
                fetchpriority="high"
              />
            } @else {
              <span data-testid="wordmark-text">{{ brand }}</span>
            }
          </a>

          <sd-main-nav />

          <sd-district-selector class="site-header__district" />

          <div class="site-header__tools">
            <sd-header-search class="site-header__search-inline" />

            <button
              class="icon-btn site-header__search-toggle"
              type="button"
              aria-label="Search"
              aria-controls="header-search-panel"
              [attr.aria-expanded]="searchOpen()"
              data-testid="search-toggle"
              #searchToggle
              (click)="toggleSearch()"
            >
              <sd-icon name="search" />
            </button>

            <!-- GLB-03 plus W3-01. **The anchor and its address are unchanged**: '/account' is now a
                 real route, and the child table redirects it to '/account/orders' for a signed-in
                 shopper and sends a signed-out one to the sign-in form with 'returnTo'. One link, two
                 correct outcomes, and nothing here has to know which.

                 What W3-01 adds is the accessible name. "Account" told a screen-reader user nothing
                 about whether they were signed in; 'accountLabel()' names the customer once the shop
                 knows who they are. It stays 'sr-only' because the icon is the visible control and the
                 greeting beside it carries the name for everyone else. -->
            <a class="icon-btn site-header__account" routerLink="/account" data-testid="account-link">
              <sd-icon name="user" />
              <span class="sr-only">{{ accountLabel() }}</span>
            </a>

            <!-- **Only ever drawn in the browser, and never in the server's markup.** 'AccountStore'
                 does not ask who is signed in during a server render — 'sd_customer_auth' is not
                 forwarded to the API by 'cartCookieInterceptor', deliberately — so 'customer()' is null
                 there for everyone. That is what keeps the rest of the shop cacheable: a first name
                 rendered into this header would make the home page, every listing and every product
                 page 'no-store' with 'Vary: Cookie', in order to greet one shopper a frame sooner.

                 'aria-hidden' on the greeting, because the account link above already carries the name
                 in its accessible name and a screen reader does not need it twice. -->
            @if (customer(); as me) {
              <span class="site-header__greeting" aria-hidden="true" data-testid="account-greeting">{{
                me.firstName
              }}</span>

              <button
                class="link site-header__signout"
                type="button"
                data-testid="sign-out"
                (click)="signOut()"
              >
                Sign out
              </button>
            }

            <a class="icon-btn" routerLink="/wishlist">
              <sd-icon name="heart" />
              @if (wishlistCount() > 0) {
                <span class="count" aria-hidden="true">{{ wishlistCount() }}</span>
              }
              <span class="sr-only">
                Wishlist, {{ wishlistCount() }} {{ wishlistCount() === 1 ? 'item' : 'items' }}
              </span>
            </a>

            <!-- An anchor, not a button, and the drawer is an enhancement on top of it.
                 /bag is a real page (CART-01), so this link works before the application has
                 hydrated, with scripting off, on a middle-click and on ctrl/cmd-click — all of which a
                 button would break. Once hydrated, a plain left click opens the mini bag instead
                 (CART-02) and the modified clicks are left alone, so "open in a new tab" still does.
                 aria-expanded is permitted on role=link and says which of the two it is doing.

                 A plain href rather than routerLink, and that is not a slip: RouterLink's own click
                 handler navigates whether or not the event was default-prevented, so the drawer opened
                 and the router then went to /bag, whose NavigationEnd closed it again. Measured — the
                 drawer flashed and vanished. Without RouterLink, the anchor is inert until this handler
                 runs, and before hydration it is an ordinary link to an ordinary page. -->
            <a
              class="icon-btn"
              href="/bag"
              aria-haspopup="dialog"
              aria-controls="mini-bag"
              [attr.aria-expanded]="bagOpen()"
              data-testid="bag-toggle"
              #bagToggle
              (click)="onBagClick($event)"
            >
              <sd-icon name="bag" />
              @if (bagCount() > 0) {
                <span class="count" aria-hidden="true">{{ bagCount() }}</span>
              }
              <span class="sr-only">
                Bag, {{ bagCount() }} {{ bagCount() === 1 ? 'item' : 'items' }}
              </span>
            </a>
          </div>
        </div>
      </div>

      <div
        class="site-header__search-panel"
        id="header-search-panel"
        [hidden]="!searchOpen()"
      >
        <div class="container">
          <!-- The second instance of the same box: the inline one above is display:none below
               1280 px and this panel is display:none above it, so a shopper only ever sees one. They
               still need distinct landmark names, because "exactly one is visible" is a fact about
               the stylesheet and a landmark list is not. -->
          <sd-header-search
            placeholder="Search for products"
            landmarkLabel="Search products, header panel"
            #panelSearch
          />
        </div>
      </div>

      <sd-mobile-nav
        [open]="menuOpen()"
        (closed)="closeMenu()"
        (signOutRequested)="signOut()"
      />

      <sd-mini-bag
        [open]="bagOpen()"
        (closed)="closeBag()"
        (focusLost)="recoverBagFocus()"
      />
    </header>
  `,
  styles: `
    /* The host is what sticks: a sticky element can only travel inside its own containing
       block, and .site-header's parent is exactly its own height. Sticking the host lets it
       travel the height of the page shell instead, with no layout shift.

       Because it sticks, it covers the top of the viewport, and anything scrolled to — an anchor
       jump, a focused control — has to clear it. That is --header-height in tokens.css, and this
       component's height is what it describes: **if the rows here change, that token moves with
       them.** It is measured, not derived, and nothing enforces the link but the breakpoint tests
       in e2e/product-page.e2e.ts.

       The awkward part is that this height is not constant. The announcement bar wraps, so the
       header is 170 px at 320 px wide against 118 at 1280, and at 320 it changes *as the bar
       rotates* — 170 for the delivery line, 151 for the shorter one. The token covers every
       placeholder message at every width; a longer one an admin writes later would out-grow it
       (a 110-character line measures 206 px at 320). TODO(ADM-17): when the messages become admin
       text, set --header-height from this element's observed height instead of from a token. */
    :host {
      display: block;
      position: sticky;
      top: 0;
      z-index: 80;
    }
    .site-header {
      background: var(--color-surface);
      border-bottom: 1px solid var(--color-border);
    }
    /* Phones and tablets: menu | wordmark | tools on one row, the district strip below.
       A grid, not a wrapping flex row, so the icons can never be pushed onto a second line. */
    .site-header__inner {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) auto;
      align-items: center;
      column-gap: var(--space-1);
      padding-block: var(--space-0-5);
    }
    .site-header__menu {
      grid-area: 1 / 1;
    }
    /* The wordmark anchor carries BOTH minimums, not just min-height — min-width: var(--tap) rather than the
       min-width: 0 that was here before ADM-22.

       **Found by a browser test, not predicted.** With a logo set, the anchor is only as wide as the drawn
       image, and the drawn width comes from the file's *intrinsic* ratio once it loads: the width and height
       attributes only supply a ratio beforehand. A square logo, or a small one the encoder clamped rather than
       upscaled, therefore gave a 32x32 box — the shop's primary link, on every page, below the 44 px touch
       minimum (WCAG 2.5.8 and this project's own rule). Measured at 1024 px as 32x44.

       min-width: 0 was correct while the content was text that had to shrink inside the grid's minmax(0, 1fr)
       column; it is wrong now that the content is an image whose size the client chooses. 44 px is far
       narrower than any real wordmark, so it costs nothing in the ordinary case and is the floor in the one
       nobody would have thought to try.

       The browser suite's square logo state is what holds this: a square logo drawn at the header's height is
       exactly that wide, so it is the only shape that reaches the floor. */
    .site-header__wordmark {
      grid-area: 1 / 2;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: var(--tap);
      min-height: var(--tap);
      font-size: 0.75rem;
      letter-spacing: 0.16em;
      text-align: center;
    }
    /* The uploaded logo is sized by the *header*, not by the file.

       This is the whole reason an admin can upload any image here without breaking the layout. The
       height is fixed to roughly the text wordmark's line box and the width follows the aspect ratio,
       capped so a very wide logo cannot squeeze the navigation: the grid's middle column is
       minmax(0, 1fr), so max-width: 100% keeps the image inside its share and the row cannot wrap.

       It also protects --header-height in tokens.css, which is measured from this component and which
       anchor jumps and focused controls clear. If the logo could set its own height, every upload would
       silently invalidate that token — and the symptom would be a focused control hidden under the
       header, three screens away from the cause. object-fit: contain means a logo whose proportions
       differ is letterboxed inside its box rather than distorted. */
    .site-header__logo {
      display: block;
      width: auto;
      height: 24px;
      max-width: 100%;
      object-fit: contain;
      object-position: center;
    }
    .site-header__tools {
      grid-area: 1 / 3;
      display: flex;
      align-items: center;
      justify-content: flex-end;
    }
    /* The drawer already offers the account rows, so below 1024 px the icon would only crowd this row.
       The greeting and Sign out follow it, by the same rule and for the same reason: 'mobile-nav' draws
       both, so a phone is never without them. */
    .site-header__account,
    .site-header__greeting,
    .site-header__signout {
      display: none;
    }
    .site-header__district {
      grid-area: 2 / 1 / 2 / 4;
      justify-content: center;
      padding-block: var(--space-0-5);
      border-top: 1px solid var(--color-border);
    }
    .site-header__search-inline {
      display: none;
    }
    .site-header__search-panel {
      padding-block: var(--space-1) var(--space-2);
      border-top: 1px solid var(--color-border);
      background: var(--color-bg-alt);
    }

    @media (min-width: 1024px) {
      .site-header__inner {
        display: flex;
        flex-wrap: nowrap;
        column-gap: var(--space-2);
        padding-block: var(--space-1);
        min-height: 72px;
      }
      .site-header__district {
        min-width: 0;
      }
      .site-header__menu {
        display: none;
      }
      .site-header__wordmark {
        order: 1;
        flex: 0 0 auto;
        font-size: 1rem;
        letter-spacing: var(--tracking-wordmark);
        white-space: nowrap;
      }
      /* Taller with the row, and bounded in width because from 1024 px the bar is a nowrap flex row:
         an unbounded logo would push the navigation off the end rather than wrap under it. */
      .site-header__logo {
        height: 32px;
        max-width: 220px;
      }
      sd-main-nav {
        order: 2;
      }
      .site-header__district {
        order: 3;
        padding-block: 0;
        border-top: 0;
      }
      .site-header__tools {
        order: 4;
      }
      .site-header__account {
        display: inline-grid;
      }
      /* Truncated rather than wrapped: from 1024 px the bar is a 'nowrap' flex row, so an unbounded
         name would push the navigation off the end. 10rem fits every ordinary first name, and the
         accessible name on the account link carries the whole of it either way. */
      .site-header__greeting {
        display: inline-block;
        max-width: 10rem;
        overflow: hidden;
        margin-inline-start: var(--space-0-5);
        font-size: var(--text-small);
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      /* 'button.link' in base.css already gives it 44 px of height; this gives it the width, so it is
         not a text-width target sitting inside a tall row. */
      .site-header__signout {
        display: inline-flex;
        flex: none;
        min-width: var(--tap);
        margin-inline-start: var(--space-1-5);
        font-size: var(--text-small);
      }
    }

    @media (min-width: 1440px) {
      .site-header__inner {
        column-gap: var(--space-3);
      }
    }

    @media (min-width: 1280px) {
      .site-header__search-inline {
        display: block;
        width: 200px;
        margin-inline-end: var(--space-1);
      }
      .site-header__search-toggle {
        display: none;
      }
      .site-header__search-panel {
        display: none;
      }
    }
  `,
})
export class SiteHeader {
  /**
   * The shop's name, from the one place it is written down (@spencer-denim/shared).
   *
   * **Still a constant, and still not a setting.** ADM-22 makes the *logo* editable; the name stays a
   * compile-time value in exactly one place per application (owner's decision, W1-15, docs/02 §I2), with
   * a convention test that fails if it is ever written out again. The server renders this header, so a
   * name fetched over HTTP would have to resolve before the first byte of the shell — and the fallback
   * below is the reason it must never depend on a fetch at all.
   */
  protected readonly brand = BRAND.name;

  private readonly account = inject(AccountStore);

  /**
   * The signed-in customer, or null (W3-01, ACC-01).
   *
   * Null on the server for everyone, and null in the browser until `GET /api/account/me` answers. It is
   * **not** read as "signed out": `AccountStore` keeps `unknown` and `signedOut` apart, so this header
   * never draws a greeting it has to take back, and never claims a shopper is signed out because the
   * read is still in flight.
   */
  protected readonly customer = this.account.customer;

  /**
   * The account link's accessible name.
   *
   * "Account" while the shop does not know, the customer's own name once it does. The link's
   * *destination* does not change — see the template.
   */
  protected readonly accountLabel = computed(() => {
    const customer = this.customer();

    return customer === null ? 'Account' : `Your account, ${customer.firstName}`;
  });

  /**
   * The uploaded logo, or null for the text wordmark (GLB-02, ADM-22).
   *
   * Null is the normal state: the client has not uploaded one yet, and `SiteSettingsService` also
   * answers null for a settings read that failed, timed out or came back malformed — so there is one
   * rendering for "no logo" rather than a second, less-looked-at one for "could not find out".
   */
  protected readonly logo = inject(SiteSettingsService).logo;

  private readonly counts = inject(StorefrontCounts);
  private readonly cart = inject(CartStore);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly panelSearch = viewChild<HeaderSearch>('panelSearch');
  private readonly searchToggle = viewChild<ElementRef<HTMLButtonElement>>('searchToggle');
  private readonly bagToggle = viewChild<ElementRef<HTMLAnchorElement>>('bagToggle');
  private readonly wordmark = viewChild<ElementRef<HTMLAnchorElement>>('wordmarkLink');
  private readonly injector = inject(Injector);

  protected readonly wishlistCount = this.counts.wishlist;
  protected readonly bagCount = this.counts.bag;

  private readonly menu = signal(false);
  private readonly search = signal(false);
  private readonly bag = signal(false);

  readonly menuOpen = this.menu.asReadonly();
  readonly searchOpen = this.search.asReadonly();
  readonly bagOpen = this.bag.asReadonly();

  constructor() {
    // **The one read of who is signed in, and it happens after the first render.** `AccountStore.load`
    // is single-flight, so this and the signed-in route guard — which runs *during* the first navigation
    // on a cold load — are one `GET /api/account/me` rather than two racing each other, whichever asks
    // first. `afterNextRender` so it does not compete with the catalogue calls the first paint needs,
    // and so it never runs during a server render: there is no cookie to read there, and a request made
    // then would answer 401 for every visitor.
    afterNextRender(() => this.account.load().subscribe());

    // Navigating away always leaves the shell tidy, whichever link was used.
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        this.menu.set(false);
        this.search.set(false);
        this.bag.set(false);
      });

    // Adding something opens the bag, which is what a slide-out bag is for (CART-02): the shopper gets
    // the confirmation and the way to checkout in one movement, and the Add to Bag button on the
    // product page does not have to know that a drawer exists. `lastAdded` is reset to null and set
    // again on each success, so adding the same variant twice opens it twice. It is never non-null
    // during a server render — nothing can have been added yet — so this does not open on the server.
    effect(() => {
      const added = this.cart.lastAdded();
      untracked(() => {
        if (added !== null) {
          this.bag.set(true);
        }
      });
    });

    // The panel is hidden with the `hidden` attribute, so wait for the render that removes it
    // before putting the caret in the field.
    afterRenderEffect(() => {
      const isOpen = this.search();
      untracked(() => {
        if (isOpen) {
          this.panelSearch()?.focusField();
        }
      });
    });
  }

  openMenu(): void {
    this.menu.set(true);
  }

  closeMenu(): void {
    this.menu.set(false);
  }

  toggleSearch(): void {
    this.search.update((open) => !open);
  }

  closeSearch(): void {
    if (!this.search()) {
      return;
    }

    this.search.set(false);
    this.focusSearchToggle();
  }

  /**
   * Opens the mini bag instead of navigating — but only for the kind of click that means "show me my
   * bag here".
   *
   * A modified click (ctrl/cmd/shift/alt) or a middle click is the shopper asking the browser for a new
   * tab or window, and swallowing those is one of the ways a single-page application quietly becomes
   * worse than a document. `button !== 0` never actually reaches a click handler for the middle button
   * in Chrome, but a stray `auxclick` mapping or a synthetic event would, and the check costs nothing.
   */
  onBagClick(event: MouseEvent): void {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }

    event.preventDefault();
    this.bag.set(true);
  }

  closeBag(): void {
    this.bag.set(false);
  }

  /**
   * The mini bag closed and had nowhere to give focus back to, so it comes here.
   *
   * `sd-drawer` returns focus to whatever opened it, which is this anchor when the shopper opened the
   * drawer themselves, and the Add to Bag button when something else did. Either is better than this
   * fallback. It is reached only when that element has been **removed while the drawer was open** —
   * which is what moving a wishlist row to the bag does to its own Move to bag button, and the
   * measured cause of a keyboard shopper landing on `<body>` (W2-08 round 3).
   *
   * **This used to be a `=== body` test run from `closeBag`, and it could not work.** At that moment
   * focus is still inside the drawer, so the test declined; the drawer then declined to focus a
   * detached node and the browser left focus on `<body>`. Deferring the same test to a later render
   * phase was tried and measured, and still ran too early — the probe read
   * `active=BUTTON`, the drawer's own close button. The drawer is the only thing that knows at the one
   * moment it is knowable, so it says so (`sd-drawer.focusLost`) and this is the answer.
   *
   * **The bag anchor, deliberately.** Returning the shopper to exactly where they were working would
   * be better, but only the page knows where that is and the page has no view of the drawer's
   * lifecycle. This anchor is one answer for every page, it is a real labelled control, and its count
   * has just gone up — it describes what the shopper did.
   */
  recoverBagFocus(): void {
    this.bagToggle()?.nativeElement.focus();
  }

  /**
   * Signs out (ACC-01, doc 11 P11 step B.7).
   *
   * **The bag is re-read, and that is not tidying up.** Step B.7 is explicit that the browser's guest bag
   * is not restored, and the guest cart was deleted by the merge at sign-in — so after this the bag
   * genuinely is empty, and the badge beside this button would otherwise keep showing the count from an
   * account bag that is no longer this visitor's. `AccountStore.signOut` clears its own state whatever
   * the server answered, including on a 401, because a 401 here means the session had already gone.
   *
   * **No `[disabled]` while it is in flight** (#109): disabling the control that currently has focus
   * throws focus to `<body>`, which is exactly what would happen to the keyboard user who just pressed
   * this. The button disappears on success instead — the `@if` above it stops matching — which is a
   * removal and the other half of the same defect, so focus is sent to the wordmark: the shopper is about
   * to be on the home page, and the wordmark is the one control certainly on it.
   */
  signOut(): void {
    this.account.signOut().subscribe(() => {
      this.cart.load();

      void this.router.navigateByUrl('/');

      // After the render that removes this button. `afterNextRender` and never `queueMicrotask`: a
      // signal write schedules change detection as a task, so a microtask would run while the button is
      // still there and focus would land on a node about to be destroyed.
      afterNextRender(() => this.wordmark()?.nativeElement.focus(), { injector: this.injector });
    });
  }

  private focusSearchToggle(): void {
    this.searchToggle()?.nativeElement.focus();
  }
}
