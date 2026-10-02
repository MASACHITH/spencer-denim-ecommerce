import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
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
          <a class="wordmark site-header__wordmark" routerLink="/" data-testid="wordmark">
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

            <a class="icon-btn site-header__account" routerLink="/account">
              <sd-icon name="user" />
              <span class="sr-only">Account</span>
            </a>

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

      <sd-mobile-nav [open]="menuOpen()" (closed)="closeMenu()" />

      <sd-mini-bag [open]="bagOpen()" (closed)="closeBag()" />
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
    /* The wordmark anchor above carries BOTH minimums, not just min-height — and min-width: var(--tap)
       rather than the min-width: 0 that was there before ADM-22.

       **Found by a browser test, not predicted.** With a logo set, the anchor is only as wide as the drawn
       image, and the drawn width comes from the file's *intrinsic* ratio once it loads: the width and height
       attributes only supply a ratio beforehand. A square logo, or a small one the encoder clamped rather
       than upscaled, therefore gave a 32x32 box — the shop's primary link, on every page, below the 44 px
       touch minimum (WCAG 2.5.8 and this project's own rule). Measured at 1024 px as 32x44.

       min-width: 0 was correct while the content was text that had to be allowed to shrink inside the
       grid's minmax(0, 1fr) column; it is wrong now that the content is an image whose size the client
       chooses. 44 px is far narrower than any real wordmark, so it costs nothing in the ordinary case and is
       the floor in the one nobody would have thought to try. */
    .site-header__tools {
      grid-area: 1 / 3;
      display: flex;
      align-items: center;
      justify-content: flex-end;
    }
    /* The drawer already offers Sign in / Register, so the icon would only crowd the row. */
    .site-header__account {
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

  protected readonly wishlistCount = this.counts.wishlist;
  protected readonly bagCount = this.counts.bag;

  private readonly menu = signal(false);
  private readonly search = signal(false);
  private readonly bag = signal(false);

  readonly menuOpen = this.menu.asReadonly();
  readonly searchOpen = this.search.asReadonly();
  readonly bagOpen = this.bag.asReadonly();

  constructor() {
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

    // sd-drawer returns focus to whatever opened it, which is this anchor when the shopper opened the
    // drawer themselves. When it was opened by an Add to Bag on another part of the page, the element
    // that had focus was that button — so focus is only steered here if the drawer did not manage it.
    const toggle = this.bagToggle()?.nativeElement;
    if (toggle && this.document.activeElement === this.document.body) {
      toggle.focus();
    }
  }

  private focusSearchToggle(): void {
    this.searchToggle()?.nativeElement.focus();
  }
}
