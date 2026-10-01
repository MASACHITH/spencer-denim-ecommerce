import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { BRAND, Icon } from '@spencer-denim/shared';

import { AnnouncementBar } from '../announcement-bar/announcement-bar';
import { DistrictSelector } from '../district-selector/district-selector';
import { HeaderSearch } from '../header-search/header-search';
import { MainNav } from '../main-nav/main-nav';
import { MobileNav } from '../mobile-nav/mobile-nav';
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
  imports: [AnnouncementBar, DistrictSelector, HeaderSearch, Icon, MainNav, MobileNav, RouterLink],
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

          <a class="wordmark site-header__wordmark" routerLink="/">{{ brand }}</a>

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

            <a class="icon-btn" routerLink="/bag">
              <sd-icon name="bag" />
              @if (bagCount() > 0) {
                <span class="count" aria-hidden="true">{{ bagCount() }}</span>
              }
              <span class="sr-only">Bag, {{ bagCount() }} items</span>
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
      min-width: 0;
      min-height: var(--tap);
      font-size: 0.75rem;
      letter-spacing: 0.16em;
      text-align: center;
    }
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
  /** The shop's name, from the one place it is written down (@spencer-denim/shared). */
  protected readonly brand = BRAND.name;

  private readonly counts = inject(StorefrontCounts);
  private readonly router = inject(Router);
  private readonly panelSearch = viewChild<HeaderSearch>('panelSearch');
  private readonly searchToggle = viewChild<ElementRef<HTMLButtonElement>>('searchToggle');

  protected readonly wishlistCount = this.counts.wishlist;
  protected readonly bagCount = this.counts.bag;

  private readonly menu = signal(false);
  private readonly search = signal(false);

  readonly menuOpen = this.menu.asReadonly();
  readonly searchOpen = this.search.asReadonly();

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

  private focusSearchToggle(): void {
    this.searchToggle()?.nativeElement.focus();
  }
}
