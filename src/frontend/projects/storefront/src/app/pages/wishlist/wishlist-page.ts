import { isPlatformBrowser } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  PLATFORM_ID,
  RESPONSE_INIT,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { BRAND, Icon, brandPageTitle } from '@spencer-denim/shared';

import { RobotsMeta } from '../../core/platform/robots-meta';
import { WISHLIST_MAX_SLUGS } from '../../core/wishlist/wishlist-api';
import { WishlistRow, WishlistStore } from '../../core/wishlist/wishlist.store';
import { FULL_WIDTH_GRID_CARD_SIZES, ProductCard } from '../../ui/product-card/product-card';
import { QuickView } from '../../ui/quick-view/quick-view';

/**
 * The wishlist page (WISH-01).
 *
 * ## It does not render on the server, and that is not a shortcut
 *
 * The wishlist lives in this browser — doc 02's WISH-01 line ("guests keep it in the browser"), doc 08
 * §1.7 ("Client-side only (no tables)"), doc 11 §553 — so the server has nothing to render it from. It
 * must not guess, either: markup the browser then replaced would be a hydration mismatch, and a page
 * that grew rows after the first paint would be adding nodes Angular never sent. So the server renders
 * the heading, the shell and a "Loading your wishlist" line, and the list is read in `afterNextRender`,
 * exactly as `RecentlyViewed` (PDP-07) and `RecentSearches` (SRCH-01) are read.
 *
 * **This is the one page where that costs nothing.** It is personal, so it answers `no-store` and
 * `noindex` and is never in a shared cache or a search result — the two things server rendering buys.
 * The bag went the other way because doc 08 §3.5 gives the bag four reasons to be server-side; §1.7
 * gives the wishlist the opposite decision in the same document.
 *
 * ## What the shopper's own browser knows, and what the shop is asked
 *
 * Membership and order come from `localStorage`; the price, the photograph and whether the shop still
 * sells it come from `/api/wishlist`, live, every time the page is opened. A saved product is something
 * a shopper comes back to in a week, so a stored price would be a price that had since changed. An item
 * the shop has withdrawn comes back with no card at all and is drawn from the stored name with a way to
 * remove it (doc 11 §575).
 *
 * ## Why "Move to bag" opens Quick View
 *
 * A wishlist entry names a **product**; a bag line needs a **variant**. Doc 11 §539 says the size has to
 * be chosen first, and Quick View is the only thing in the storefront that has the variants
 * (`ProductCardDto` carries colour dots and no sizes at all). The entry leaves the wishlist on the
 * dialog's `added` output and on nothing else — see `WishlistStore.completeMove` for why that matters.
 *
 * ## The two controls that remove an item
 *
 * The card keeps its heart, which on a saved product means "remove from wishlist", and the row below has
 * a Remove of its own. That duplication is deliberate: the heart is the control a shopper has used on
 * every listing and product page to get here, and a wishlist page whose heart did nothing would be the
 * stranger of the two. Both are fully labelled with the product's name, so a screen reader reads two
 * distinct controls rather than two identical ones.
 */
@Component({
  selector: 'sd-wishlist-page',
  imports: [Icon, ProductCard, QuickView, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container wish">
      <div class="wish__head">
        <h1 class="wish__title" id="wishlist-title">Wishlist</h1>
        <p class="wish__count">{{ countLabel() }}</p>
      </div>

      <!-- role="status" and not role="alert": these are confirmations of something the shopper just
           did, so they are announced politely rather than interrupting. It is rendered on the server
           and always present, because a live region added to the page at the same moment as its text
           is a region a screen reader has not been watching. -->
      <p class="sr-only" role="status">{{ announcement() }}</p>

      @if (error(); as message) {
        <p class="wish__error" role="alert">
          {{ message }}
          @if (status() === 'error') {
            <button class="link" type="button" (click)="retry()">Try again</button>
          }
        </p>
      }

      @if (!taken()) {
        <!-- The server's answer, and the browser's for one frame. Not an empty state: the server does
             not know whether this shopper has anything saved, and "Your wishlist is empty" would be a
             claim rather than a wait. -->
        <p class="wish__muted" role="status">Loading your wishlist…</p>
      } @else if (isEmpty()) {
        <div class="wish__empty">
          <p class="wish__muted">Your wishlist is empty.</p>
          <p class="wish__hint">
            Tap the heart on anything you like and it will be saved here, in this browser.
          </p>
          <a class="btn btn--primary" routerLink="/">Start shopping</a>
        </div>
      } @else {
        <div class="wish__bar">
          @if (confirmingClear()) {
            <!-- A two-step confirmation rather than window.confirm, which cannot be styled, cannot be
                 announced properly and does not exist during a server render. -->
            <p class="wish__confirm" role="status">
              {{ clearQuestion() }}
              <button class="link" type="button" (click)="clear()">Yes, clear it</button>
              <button class="link" type="button" (click)="cancelClear()">Cancel</button>
            </p>
          } @else {
            <button class="link wish__clear" type="button" (click)="askToClear()">
              Clear wishlist
            </button>
          }
        </div>

        @if (truncated()) {
          <p class="wish__muted wish__note">
            {{ truncationNote() }}
          </p>
        }

        <ul class="product-grid wish__list">
          @for (row of rows(); track row.entry.slug) {
            <li class="wish__item">
              @if (row.product; as product) {
                <sd-product-card [product]="product" [sizes]="CARD_SIZES" />

                <div class="wish__actions">
                  @if (product.isInStock) {
                    <!-- aria-haspopup/aria-expanded, as the card's own two triggers carry: this opens
                         the same dialog and a shopper is entitled to know before pressing. -->
                    <button
                      class="btn btn--secondary wish__move"
                      type="button"
                      data-testid="wishlist-move-to-bag"
                      aria-haspopup="dialog"
                      [attr.aria-expanded]="movingSlug() === row.entry.slug"
                      [attr.aria-label]="'Move ' + name(row) + ' to your bag'"
                      (click)="beginMove(row)"
                    >
                      Move to bag
                    </button>
                  } @else {
                    <p class="wish__oos">
                      Out of stock. We will keep it saved for you.
                    </p>
                  }

                  <button
                    class="link wish__remove"
                    type="button"
                    data-testid="wishlist-remove"
                    [attr.aria-label]="'Remove ' + name(row) + ' from your wishlist'"
                    (click)="remove(row)"
                  >
                    Remove
                  </button>
                </div>

                @if (movingSlug() === row.entry.slug) {
                  <sd-quick-view
                    [product]="product"
                    [open]="true"
                    (added)="completeMove(row)"
                    (closed)="cancelMove()"
                  />
                }
              } @else if (row.pending) {
                <!-- Saved, and we have not heard back about it. The stored name is all this browser
                     knows, and printing it is better than printing a grey box: the shopper can see
                     which of their items is still loading. -->
                <div class="wish__placeholder">
                  <span class="wish__placeholder-media" aria-hidden="true"></span>
                  <p class="wish__placeholder-name">{{ name(row) }}</p>
                  <p class="wish__muted">Loading…</p>
                </div>
              } @else {
                <!-- Doc 11 §575: an item the shop no longer sells is shown as unavailable and can be
                     removed. There is no card to draw — the API has nothing to send for a product that
                     is not for sale — so the name comes from what was stored when it was saved. -->
                <div class="wish__gone">
                  <span class="wish__placeholder-media" aria-hidden="true">
                    <sd-icon name="heart" />
                  </span>
                  <p class="wish__placeholder-name">{{ name(row) }}</p>
                  <p class="wish__muted">
                    This is no longer available.
                  </p>
                  <button
                    class="link wish__remove"
                    type="button"
                    data-testid="wishlist-remove"
                    [attr.aria-label]="'Remove ' + name(row) + ' from your wishlist'"
                    (click)="remove(row)"
                  >
                    Remove
                  </button>
                </div>
              }
            </li>
          }
        </ul>

        <a class="link wish__continue" routerLink="/">Continue shopping</a>
      }
    </div>
  `,
  styles: `
    .wish {
      padding-block: var(--space-4) var(--space-7);
    }
    .wish__head {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--space-1) var(--space-2);
      margin-bottom: var(--space-3);
    }
    .wish__title {
      margin: 0;
      font-size: var(--text-h2);
    }
    .wish__count,
    .wish__muted {
      margin: 0;
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }
    .wish__hint {
      margin: 0;
      max-width: 42ch;
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }
    .wish__note {
      margin-bottom: var(--space-2);
    }
    .wish__error {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1) var(--space-2);
      margin: 0 0 var(--space-3);
      padding: var(--space-2);
      border: 1px solid var(--color-warning);
      color: var(--color-warning);
      font-size: var(--text-small);
    }
    .wish__empty {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      padding-block: var(--space-5);
    }

    /* The bar above the grid. Right-aligned from 768 px, where there is room beside the heading;
       stacked below that, because a 320 px row with a heading and a destructive action on it puts the
       action under a thumb reaching for the heading. */
    .wish__bar {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1) var(--space-2);
      margin-bottom: var(--space-3);
    }
    .wish__confirm {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1) var(--space-2);
      margin: 0;
      font-size: var(--text-small);
    }
    @media (min-width: 768px) {
      .wish__bar {
        justify-content: flex-end;
      }
    }

    .wish__list {
      margin: 0;
      padding: 0;
    }
    .wish__item {
      display: flex;
      flex-direction: column;
      gap: var(--space-1-5);
    }

    /* Every control in here is at least 44 px tall, which axe does not check and W1-03 shipped
       36-41 px controls past. The link-styled ones get the height from a min-height plus centred
       content rather than from padding, so a long label wrapping cannot make one taller than the
       other and a short one is never shorter than a thumb. */
    .wish__actions {
      display: flex;
      flex-direction: column;
      align-items: stretch;
      gap: var(--space-0-5);
    }
    .wish__move {
      min-height: var(--tap);
      width: 100%;
    }
    .wish__remove,
    .wish__clear {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: var(--tap);
      /* A link in a column of its own would otherwise have a 44 px *row* and a text-width target. */
      min-width: var(--tap);
    }
    .wish__oos {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      margin: 0;
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }

    .wish__placeholder,
    .wish__gone {
      display: flex;
      flex-direction: column;
      gap: var(--space-0-5);
    }
    .wish__placeholder-media {
      display: grid;
      place-items: center;
      aspect-ratio: 3 / 4;
      background: var(--color-bg-alt);
      color: var(--color-text-muted);
    }
    .wish__placeholder-name {
      margin: 0;
      font-size: var(--text-small);
    }

    .wish__continue {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      margin-top: var(--space-4);
    }
  `,
})
export class WishlistPage {
  /** The same `sizes` the homepage and product-page rows use: a four-column grid with no sidebar. */
  protected readonly CARD_SIZES = FULL_WIDTH_GRID_CARD_SIZES;

  private readonly store = inject(WishlistStore);

  /**
   * Whether the browser's own list has been taken up yet.
   *
   * **The page gates on this and not on `rows()` being empty**, which is the distinction two red `main`
   * branches have been built on: "I have not looked yet" and "there is nothing there" render as the same
   * empty array, and telling a shopper their wishlist is empty when the answer is "one frame from now"
   * is a confident wrong value rather than a slow one. It flips in `afterNextRender`, after the local
   * list has been read, so it is false on the server for everyone and false in the browser for exactly
   * one frame.
   */
  private readonly localTaken = signal(false);

  protected readonly taken = this.localTaken.asReadonly();

  protected readonly rows = this.store.rows;
  protected readonly isEmpty = this.store.isEmpty;
  protected readonly status = this.store.status;
  protected readonly error = this.store.error;
  protected readonly announcement = this.store.lastChange;
  protected readonly movingSlug = this.store.movingSlug;
  protected readonly truncated = this.store.truncated;

  private readonly clearAsked = signal(false);

  protected readonly confirmingClear = this.clearAsked.asReadonly();

  protected readonly countLabel = computed(() => {
    const count = this.store.count();
    const unavailable = this.store.unavailableCount();
    const items = count === 1 ? '1 item' : `${count} items`;

    // The withdrawn ones are counted in, and said separately. Leaving them out of the total would make
    // the header badge (GLB-04) and this line disagree; not mentioning them would leave a shopper
    // wondering why two of their cards have no price.
    return unavailable === 0
      ? `${items} saved in this browser.`
      : unavailable === 1
        ? `${items} saved in this browser. 1 is no longer available.`
        : `${items} saved in this browser. ${unavailable} are no longer available.`;
  });

  protected readonly clearQuestion = computed(() => {
    const count = this.store.count();
    return count === 1
      ? 'Remove the 1 item in your wishlist?'
      : `Remove all ${count} items in your wishlist?`;
  });

  protected readonly truncationNote = computed(
    () =>
      `You have ${this.store.count()} items saved. Showing the ${WISHLIST_MAX_SLUGS} most recent — ` +
      'nothing has been removed.',
  );

  constructor() {
    inject(Title).setTitle(brandPageTitle('Wishlist'));
    inject(Meta).updateTag({
      name: 'description',
      content: `The products you have saved at ${BRAND.name}.`,
    });

    // A wishlist is personal and has nothing in it a crawler should index, and there is no status code
    // saying so, so the tag is the only thing that does. Cleared on the way out, as the bag page and the
    // not-found page do: a client-side navigation would otherwise carry `noindex` onto the next page.
    const robots = inject(RobotsMeta);
    robots.noindex();
    inject(DestroyRef).onDestroy(() => robots.clear());

    // **`no-store` even though the markup carries no list.** The HTML is the same for everyone today —
    // the rows arrive after hydration — so this is belt and braces rather than a fix. It is here because
    // the alternative is a page whose caching depends on a rendering decision made in this file: if a
    // later task ever renders a row on the server, the header has to already be right. No `Vary: Cookie`,
    // unlike the bag: nothing here reads a cookie.
    const response = inject(RESPONSE_INIT, { optional: true });
    if (response) {
      const headers = new Headers(response.headers ?? {});
      headers.set('Cache-Control', 'no-store, no-cache, private');
      response.headers = headers;
    }

    // The list is in `localStorage`, so this is the earliest honest moment: the server has rendered, the
    // browser has hydrated the markup the server sent, and anything added now is an ordinary update
    // rather than a node Angular never sent.
    //
    // Guarded by `isPlatformBrowser` as well, the shape `RecentlyViewed` uses. Angular skips
    // `afterNextRender` during a real server render on its own, so this is not a fix — it makes the
    // rule *explicit and testable*: "the server never takes the list up" is then a property of this
    // file rather than of the framework's render scheduler, and a unit test can render on the server
    // platform and see the loading state the SSR process really emits.
    if (isPlatformBrowser(inject(PLATFORM_ID))) {
      afterNextRender(() => {
        this.localTaken.set(true);
        this.store.load();
      });
    }
  }

  /** What to call a row: the live name, the stored one, or something true but general. */
  protected name(row: WishlistRow): string {
    return row.product?.name ?? row.entry.name ?? 'This item';
  }

  protected retry(): void {
    this.store.retry();
  }

  protected remove(row: WishlistRow): void {
    this.store.remove(row.entry.slug, this.name(row));
  }

  protected askToClear(): void {
    this.clearAsked.set(true);
  }

  protected cancelClear(): void {
    this.clearAsked.set(false);
  }

  protected clear(): void {
    this.clearAsked.set(false);
    this.store.clear();
  }

  protected beginMove(row: WishlistRow): void {
    this.store.beginMove(row.entry.slug);
  }

  protected cancelMove(): void {
    this.store.cancelMove();
  }

  /**
   * The bag took it, so it leaves the wishlist.
   *
   * Raised by `QuickView.added`, which fires only in the branch where the bag answered with no error.
   * A sold-out or offline add raises nothing, the dialog stays open with the reason on it, and the item
   * is still saved — the mirror of the W2-09 defect where a bag line was destroyed over a wishlist save
   * that had silently failed.
   */
  protected completeMove(row: WishlistRow): void {
    this.store.completeMove(row.entry.slug, this.name(row));
  }
}
