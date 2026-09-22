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
import { Icon } from '@spencer-denim/shared';

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

          <a class="wordmark site-header__wordmark" routerLink="/">Spencer Denim</a>

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

            <a class="icon-btn" routerLink="/account">
              <sd-icon name="user" />
              <span class="sr-only">Account</span>
            </a>

            <a class="icon-btn" routerLink="/wishlist">
              <sd-icon name="heart" />
              @if (wishlistCount() > 0) {
                <span class="count" aria-hidden="true">{{ wishlistCount() }}</span>
              }
              <span class="sr-only">Wishlist, {{ wishlistCount() }} items</span>
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
          <sd-header-search placeholder="Search for products" #panelSearch />
        </div>
      </div>

      <sd-mobile-nav [open]="menuOpen()" (closed)="closeMenu()" />
    </header>
  `,
  styles: `
    :host {
      display: block;
    }
    .site-header {
      position: sticky;
      top: 0;
      z-index: 80;
      background: var(--color-surface);
      border-bottom: 1px solid var(--color-border);
    }
    .site-header__inner {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0 var(--space-1);
      padding-block: var(--space-0-5);
    }
    .site-header__wordmark {
      display: inline-flex;
      align-items: center;
      order: 2;
      flex: 1 1 auto;
      justify-content: center;
      min-height: var(--tap);
      font-size: 0.8125rem;
      letter-spacing: 0.18em;
      text-align: center;
    }
    .site-header__menu {
      order: 1;
    }
    .site-header__tools {
      order: 3;
      display: flex;
      align-items: center;
      justify-content: flex-end;
    }
    .site-header__district {
      order: 4;
      flex-basis: 100%;
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
        gap: 0 var(--space-3);
        padding-block: var(--space-1);
        min-height: 72px;
      }
      .site-header__menu {
        display: none;
      }
      sd-main-nav {
        order: 2;
      }
      .site-header__wordmark {
        order: 1;
        flex: 0 0 auto;
        font-size: 1rem;
        letter-spacing: var(--tracking-wordmark);
        white-space: nowrap;
      }
      .site-header__district {
        order: 3;
        flex-basis: auto;
        padding-block: 0;
        border-top: 0;
      }
      .site-header__tools {
        order: 4;
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
