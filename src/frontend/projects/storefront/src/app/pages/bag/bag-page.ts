import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  RESPONSE_INIT,
  computed,
  inject,
} from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { BRAND, Icon, brandPageTitle } from '@spencer-denim/shared';

import { CartLineDto, cartLineNotice, isBuyable } from '../../core/cart/cart.model';
import { CartStore } from '../../core/cart/cart.store';
import { formatLkr } from '../../core/catalog/money';
import { RobotsMeta } from '../../core/platform/robots-meta';

/**
 * The bag page (CART-01).
 *
 * **It renders on the server**, which is the point of reading the cart from the constructor: the request
 * is in flight before the first change detection, so the HTML carries the lines rather than a spinner,
 * and `cartCookieInterceptor` is what lets the server see whose bag it is at all.
 *
 * **And because it does, the response says so.** This is the one page in the storefront whose markup is
 * personal, so it answers `Cache-Control: no-store, private` — a shared cache holding this page would
 * hand one shopper another shopper's bag — and `noindex`, because a bag is not a page anyone should find
 * in a search result. The cart *token* is in neither the markup nor the transfer state; it never leaves
 * the `HttpOnly` cookie.
 *
 * ## What this page does not have, and why
 *
 * **No "Proceed to checkout".** Checkout is W3-02 and is not built. This task is its unblocker, not its
 * delivery. A button that looked like it worked and did nothing would be worse than no button — the same
 * judgement the product page made about Add to Bag until this task, and the product card still makes
 * about Quick View.
 *
 * **No promo code field.** CART-01 asks for one and there is nothing behind it: promo codes are ADM-13
 * (week 3) and `marketing.PromoCodes` does not exist, so every code a shopper typed would be refused and
 * the field would teach them the shop is broken. Doc 08 §3.5 wants the code revalidated on every cart
 * read, which is impossible with no table to read.
 *
 * **No delivery fee, and so no grand total.** Doc 08 §6 step 10 takes the fee from a
 * `sales.DeliveryMethodZoneRates` row that "must exist and be available". That table is #53's and
 * `ref.Districts` has never been created, so any figure here would be invented — and issue #33 is open
 * precisely because delivery estimates have no source. The summary says what the items cost and that
 * delivery is worked out at checkout, which is true. All three are in the task report.
 */
@Component({
  selector: 'sd-bag-page',
  imports: [Icon, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container bag">
      <h1 class="bag__title" id="bag-title">Your bag</h1>

      @if (error(); as message) {
        <p class="bag__error" role="alert">
          {{ message }}
          @if (isEmpty()) {
            <button class="link" type="button" (click)="retry()">Try again</button>
          }
        </p>
      }

      @if (isLoading() && isEmpty()) {
        <p class="bag__muted" role="status">Loading your bag…</p>
      } @else if (isEmpty()) {
        <div class="bag__empty">
          <p class="bag__muted">Your bag is empty.</p>
          <a class="btn btn--primary" routerLink="/">Start shopping</a>
        </div>
      } @else {
        <div class="bag__layout">
          <section class="bag__items" aria-labelledby="bag-items-title">
            <h2 class="sr-only" id="bag-items-title">Items in your bag</h2>

            <ul class="bag__list">
              @for (line of lines(); track line.variantId) {
                <li class="bag__line" [class.is-unavailable]="line.status === 'Unavailable'">
                  <a
                    class="bag__thumb"
                    [routerLink]="['/product', line.slug]"
                    tabindex="-1"
                    aria-hidden="true"
                  >
                    @if (line.imageUrl) {
                      <img
                        [src]="line.imageUrl"
                        [alt]="line.imageAltText ?? ''"
                        width="96"
                        height="128"
                        loading="lazy"
                      />
                    }
                  </a>

                  <div class="bag__detail">
                    <a class="bag__name" [routerLink]="['/product', line.slug]">
                      {{ line.productName }}
                    </a>
                    <dl class="bag__attrs">
                      <div><dt>Colour</dt><dd>{{ line.colourName }}</dd></div>
                      <div><dt>Size</dt><dd>{{ line.sizeLabel }}</dd></div>
                    </dl>

                    @if (line.price; as price) {
                      <p class="bag__unit">
                        {{ money(price.current) }} each
                        @if (price.hasDiscount) {
                          <s class="bag__was">{{ money(price.original) }}</s>
                        }
                      </p>
                    }

                    @if (notice(line); as text) {
                      <p class="bag__notice" [class.bag__notice--warn]="line.status !== 'Buyable'">
                        {{ text }}
                      </p>
                    }
                  </div>

                  <div class="bag__qty">
                    @if (line.status === 'Unavailable') {
                      <!-- Nothing to choose: the shop is not offering this any more, so a quantity
                           control would be asking how many of something unavailable they would like. -->
                      <p class="bag__qty-fixed">Qty {{ line.quantity }}</p>
                    } @else {
                      <label class="sr-only" [attr.for]="'bag-qty-' + line.variantId">
                        Quantity for {{ label(line) }}
                      </label>
                      <div class="qty">
                        <button
                          class="qty__step"
                          type="button"
                          [attr.aria-label]="'Decrease quantity of ' + label(line)"
                          [disabled]="line.quantity <= 1 || busy(line)"
                          (click)="step(line, -1)"
                        >
                          &minus;
                        </button>
                        <input
                          class="qty__input"
                          type="number"
                          inputmode="numeric"
                          min="1"
                          step="1"
                          [id]="'bag-qty-' + line.variantId"
                          [max]="line.maxQuantity"
                          [value]="line.quantity"
                          [disabled]="busy(line)"
                          (change)="onQuantityInput(line, $event)"
                        />
                        <button
                          class="qty__step"
                          type="button"
                          [attr.aria-label]="'Increase quantity of ' + label(line)"
                          [disabled]="line.quantity >= line.maxQuantity || busy(line)"
                          (click)="step(line, 1)"
                        >
                          +
                        </button>
                      </div>
                    }
                  </div>

                  <p class="bag__total">
                    @if (line.lineSubtotal !== null) {
                      {{ money(line.lineSubtotal) }}
                    } @else {
                      <span class="bag__muted">—</span>
                    }
                  </p>

                  <div class="bag__actions">
                    <button
                      class="link"
                      type="button"
                      [disabled]="busy(line)"
                      [attr.aria-label]="'Remove ' + label(line) + ' from your bag'"
                      (click)="remove(line)"
                    >
                      Remove
                    </button>
                    <button
                      class="link"
                      type="button"
                      [disabled]="busy(line)"
                      [attr.aria-label]="'Move ' + label(line) + ' to your wishlist'"
                      (click)="moveToWishlist(line)"
                    >
                      <sd-icon name="heart" size="sm" />
                      Move to wishlist
                    </button>
                  </div>
                </li>
              }
            </ul>

            <a class="link bag__continue" routerLink="/">Continue shopping</a>
          </section>

          <section class="bag__summary" aria-labelledby="bag-summary-title">
            <h2 class="bag__summary-title" id="bag-summary-title">Order summary</h2>

            <dl class="bag__sums">
              <div>
                <dt>Subtotal ({{ buyableCount() }} {{ buyableCount() === 1 ? 'item' : 'items' }})</dt>
                <dd>{{ money(totals().itemsSubtotal) }}</dd>
              </div>
              @if (totals().savedTotal > 0) {
                <div>
                  <dt>Discount</dt>
                  <dd class="text-sale">&minus;{{ money(totals().savedTotal) }}</dd>
                </div>
              }
              <div>
                <dt>Delivery</dt>
                <dd class="bag__muted">Calculated at checkout</dd>
              </div>
              <div class="bag__sums-total">
                <!-- Labelled "items only" rather than "Total", because it is one: the delivery fee has
                     no source (#33, #53) and there is no promo code to apply (ADM-13). A row called
                     "Total" that silently excluded delivery would be the misleading version of this. -->
                <dt>Total (items only)</dt>
                <dd>{{ money(totals().itemsSubtotal) }}</dd>
              </div>
            </dl>

            @if (unavailableCount() > 0) {
              <p class="bag__notice bag__notice--warn">
                @if (unavailableCount() === 1) {
                  1 item in your bag cannot be bought at the moment and is not in this total.
                } @else {
                  {{ unavailableCount() }} items in your bag cannot be bought at the moment and are not
                  in this total.
                }
              </p>
            }

            <p class="bag__muted bag__fineprint">
              Delivery is worked out at checkout, so this covers the items only.
            </p>
          </section>
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .bag {
      display: grid;
      gap: var(--space-3);
      padding-block: var(--space-4) var(--space-12);
    }
    .bag__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .bag__error {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1);
      padding: var(--space-1-5);
      border: 1px solid var(--color-sale);
      color: var(--color-sale);
    }
    .bag__muted {
      color: var(--color-text-muted);
    }
    .bag__empty {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      padding-block: var(--space-4);
    }
    .bag__layout {
      display: grid;
      gap: var(--space-4);
      align-items: start;
    }
    .bag__list {
      display: grid;
      gap: var(--space-3);
      list-style: none;
    }
    /* Phones: thumbnail in the first column, everything else stacked beside and below it. Named grid
       areas rather than source order, so the same markup can become a table-like row at 768 px without
       anything being duplicated for a breakpoint. */
    .bag__line {
      display: grid;
      grid-template-columns: 96px minmax(0, 1fr);
      grid-template-areas:
        'thumb detail'
        'thumb total'
        'qty qty'
        'actions actions';
      gap: var(--space-1) var(--space-2);
      padding-bottom: var(--space-3);
      border-bottom: 1px solid var(--color-border);
    }
    .bag__line.is-unavailable .bag__name,
    .bag__line.is-unavailable .bag__unit {
      color: var(--color-text-muted);
    }
    .bag__thumb {
      grid-area: thumb;
      display: block;
      background: var(--color-bg-alt);
      aspect-ratio: 3 / 4;
    }
    .bag__thumb img {
      display: block;
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .bag__detail {
      grid-area: detail;
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .bag__name {
      font-weight: var(--weight-medium);
      color: inherit;
      text-decoration: none;
    }
    .bag__name:hover {
      text-decoration: underline;
    }
    .bag__attrs {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
      font-size: var(--text-label);
      color: var(--color-text-muted);
    }
    .bag__attrs div {
      display: flex;
      gap: 4px;
    }
    .bag__attrs dt::after {
      content: ':';
    }
    .bag__unit {
      font-size: var(--text-label);
      color: var(--color-text-muted);
      font-variant-numeric: tabular-nums;
    }
    .bag__was {
      margin-inline-start: 4px;
    }
    .bag__notice {
      font-size: var(--text-label);
      color: var(--color-text-muted);
    }
    .bag__notice--warn {
      color: var(--color-sale);
    }
    .bag__qty {
      grid-area: qty;
    }
    .bag__qty-fixed {
      min-height: var(--tap);
      display: flex;
      align-items: center;
      color: var(--color-text-muted);
    }
    .qty {
      display: flex;
      align-items: stretch;
      width: max-content;
      border: 1px solid var(--color-border-strong);
    }
    /* The three controls sit edge to edge, so a focus ring would be painted under its neighbour.
       Lifting whichever has focus puts the whole ring on top (lesson L4). */
    .qty > :focus-visible {
      position: relative;
      z-index: 1;
    }
    .qty__step {
      width: var(--tap);
      min-height: var(--tap);
      border: 0;
      background: var(--color-surface);
      font-size: var(--text-h4);
      cursor: pointer;
    }
    .qty__step:disabled {
      color: var(--color-text-muted);
      cursor: not-allowed;
    }
    .qty__input {
      width: 3.5rem;
      min-height: var(--tap);
      border: 0;
      border-inline: 1px solid var(--color-border-strong);
      background: var(--color-surface);
      text-align: center;
      font-variant-numeric: tabular-nums;
    }
    .bag__total {
      grid-area: total;
      font-weight: var(--weight-medium);
      font-variant-numeric: tabular-nums;
    }
    .bag__actions {
      grid-area: actions;
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
    .bag__actions .link {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      min-height: var(--tap);
    }
    .bag__continue {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      justify-self: start;
    }
    .bag__summary {
      display: grid;
      gap: var(--space-1-5);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-bg-alt);
    }
    .bag__summary-title {
      font-size: var(--text-h4);
    }
    .bag__sums {
      display: grid;
      gap: var(--space-1);
      font-variant-numeric: tabular-nums;
    }
    .bag__sums > div {
      display: flex;
      justify-content: space-between;
      gap: var(--space-2);
    }
    .bag__sums-total {
      padding-top: var(--space-1);
      border-top: 1px solid var(--color-border);
      font-size: var(--text-h4);
    }
    .bag__fineprint {
      font-size: var(--text-label);
    }

    @media (min-width: 768px) {
      .bag__line {
        grid-template-columns: 96px minmax(0, 1fr) auto auto;
        grid-template-areas:
          'thumb detail qty total'
          'thumb actions actions actions';
        align-items: start;
        column-gap: var(--space-2);
      }
      .bag__total {
        text-align: end;
        min-width: 7rem;
      }
    }

    @media (min-width: 1024px) {
      .bag__layout {
        grid-template-columns: minmax(0, 1fr) 340px;
        column-gap: var(--space-4);
      }
      .bag__summary {
        position: sticky;
        /* Clears the sticky header, whose height is --header-height (see site-header). */
        top: calc(var(--header-height) + var(--space-2));
      }
    }
  `,
})
export class BagPage {
  private readonly cart = inject(CartStore);

  protected readonly lines = this.cart.lines;
  protected readonly totals = this.cart.totals;
  protected readonly isEmpty = this.cart.isEmpty;
  protected readonly isLoading = this.cart.isLoading;
  protected readonly error = this.cart.error;

  protected readonly buyableCount = computed(() => this.totals().buyableItemCount);

  protected readonly unavailableCount = computed(
    () => this.lines().filter((line) => !isBuyable(line)).length,
  );

  constructor() {
    inject(Title).setTitle(brandPageTitle('Your bag'));
    inject(Meta).updateTag({
      name: 'description',
      content: `The items you have added to your ${BRAND.name} bag.`,
    });

    // A bag is personal and has nothing in it a crawler should index — and, unlike a 404, there is no
    // status code saying so, so the tag is the only thing that does. Cleared on the way out, as the
    // not-found page does: a client-side navigation would otherwise carry `noindex` onto the next page.
    const robots = inject(RobotsMeta);
    robots.noindex();
    inject(DestroyRef).onDestroy(() => robots.clear());

    // The response that carries this markup must not be stored by anything. `RESPONSE_INIT` is absent in
    // the browser, where a client-side navigation has no response to set headers on.
    const response = inject(RESPONSE_INIT, { optional: true });
    if (response) {
      const headers = new Headers(response.headers ?? {});
      headers.set('Cache-Control', 'no-store, no-cache, private');
      headers.set('Vary', 'Cookie');
      response.headers = headers;
    }

    // From the constructor, so the request is in flight before the first change detection and the server
    // renders the lines rather than a spinner.
    this.cart.load();
  }

  protected busy(line: CartLineDto): boolean {
    return this.cart.isBusy(line.variantId);
  }

  protected notice(line: CartLineDto): string | null {
    return cartLineNotice(line);
  }

  protected label(line: CartLineDto): string {
    return `${line.productName}, ${line.colourName}, size ${line.sizeLabel}`;
  }

  protected money(amount: number): string {
    return formatLkr(amount);
  }

  protected retry(): void {
    this.cart.retry();
  }

  protected step(line: CartLineDto, by: number): void {
    const next = Math.min(Math.max(line.quantity + by, 1), line.maxQuantity);
    if (next !== line.quantity) {
      this.cart.setQuantity(line.variantId, next, this.label(line));
    }
  }

  protected remove(line: CartLineDto): void {
    this.cart.remove(line.variantId, this.label(line));
  }

  protected moveToWishlist(line: CartLineDto): void {
    this.cart.moveToWishlist(line.variantId, line.slug, this.label(line));
  }

  /**
   * A typed quantity.
   *
   * `change` rather than `input`, deliberately: a request per keystroke would send 1, then 12, then 123
   * on the way to typing "123", and the first two are changes the shopper never asked for.
   *
   * **Too many is clamped; anything else is ignored.** Doc 04 §4 says to clamp to the maximum rather
   * than accept a number and fail, and "99" against a ceiling of 4 is an unambiguous instruction — give
   * me as many as you have. Everything else is not an instruction at all, and is answered by putting
   * the box back to the quantity the bag already holds, which is the one value guaranteed to be valid.
   * Nothing is sent.
   *
   * The test is a digits-only pattern rather than `parseInt`, and the difference is not pedantic:
   * `parseInt('1e9', 10)` is **1**, so typing `1e9` would have quietly set the quantity to one. `0` and
   * `-3` are rejected on the same principle — a shopper who meant to remove the line has a Remove
   * button, and guessing "they must have meant 1" changes a quantity they never asked to change.
   */
  protected onQuantityInput(line: CartLineDto, event: Event): void {
    const input = event.target as HTMLInputElement;
    const raw = input.value.trim();
    const typed = /^\d+$/.test(raw) ? Number(raw) : Number.NaN;
    const usable = Number.isSafeInteger(typed) && typed >= 1;
    const next = usable ? Math.min(typed, line.maxQuantity) : line.quantity;

    input.value = String(next);

    if (next !== line.quantity) {
      this.cart.setQuantity(line.variantId, next, this.label(line));
    }
  }
}
