import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  RESPONSE_INIT,
  afterRenderEffect,
  computed,
  inject,
  untracked,
  viewChildren,
} from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { BRAND, Icon, brandPageTitle } from '@spencer-denim/shared';

import { CartLineDto, cartLineNotice, isBuyable } from '../../core/cart/cart.model';
import { CartStore } from '../../core/cart/cart.store';
import { formatLkr } from '../../core/catalog/money';
import { RobotsMeta } from '../../core/platform/robots-meta';
import {
  DeliveryCountryService,
  OverseasEstimate,
  workingDays,
} from '../../core/delivery/delivery-country.service';

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
 * **There is now a "Proceed to checkout" button (W3-02).** It was absent while checkout did not exist,
 * because a button that looked like it worked and did nothing would have been worse than no button. It is
 * **disabled while any line cannot be bought** — doc 11 §7 G-08: warn and block until the shopper adjusts
 * it, never reduce a quantity or drop a line on their behalf — and the reason is said in words next to it
 * rather than left to a greyed-out control.
 *
 * **No promo code field.** CART-01 asks for one and there is nothing behind it: promo codes are ADM-13
 * (week 3) and `marketing.PromoCodes` does not exist, so every code a shopper typed would be refused and
 * the field would teach them the shop is broken. Doc 08 §3.5 wants the code revalidated on every cart
 * read, which is impossible with no table to read.
 *
 * **Still no delivery fee here, and so no grand total here** — and that is now a narrower statement than
 * it was. W3-02 created `ref.Districts` and seeded `sales.DeliveryMethodZoneRates`, so a fee *can* be
 * priced; what the bag lacks is a **district to price it for**. Doc 08 §3.5 R7 would take one from
 * `Users.DefaultDistrictId` (no customer sign-in, ACC-01) or the `Delivery.DefaultZone` setting (no
 * `ops.Settings`, ADM-22), and `Carts.DistrictId` is not in the schema — W2-09 left the column out and
 * GLB-06's header district selector, which would write it, is unbuilt (#33). So the bag keeps saying
 * delivery is worked out at checkout, and **checkout now genuinely works it out**, which is what makes
 * that sentence true rather than merely unfalsifiable. The three figures the bag does print are the same
 * three, with the same labels, that checkout prints.
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
                          #qtyBox
                          [id]="'bag-qty-' + line.variantId"
                          [attr.data-variant-id]="line.variantId"
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

            <!-- The ladder has to add up. It did not: Subtotal printed itemsSubtotal, which is already
                 net of the markdown, then a deduction row of savedTotal, which *is* the markdown, then
                 itemsSubtotal again as the Total — so a shopper read 8,784 − 5,196 and a total of
                 8,784. Subtotal is now the "was" figure (itemsOriginalTotal) and Total is what they pay
                 (itemsSubtotal), which makes the subtraction true and shows what they saved, which is
                 worth showing on a shop that marks things down. bag-page.spec.ts checks the three
                 figures as a sum, not as three string matches. -->
            <dl class="bag__sums">
              <div>
                <dt>Subtotal ({{ buyableCount() }} {{ buyableCount() === 1 ? 'item' : 'items' }})</dt>
                <dd>{{ money(totals().itemsOriginalTotal) }}</dd>
              </div>
              @if (totals().savedTotal > 0) {
                <!-- Savings, not Discount, and that is a distinction rather than a synonym: nothing
                     here was applied by a code. This is the markdown between a product's compare-at
                     price and what it sells for today (doc 08 §6 steps 2 and 6). ADM-13's promo code
                     adds a Discount row of its own *below* this one, and two rows both called Discount
                     would be unreadable. Hidden entirely when there is nothing to show, so a bag of
                     full-price items has Subtotal equal to Total and no deduction at all. -->
                <div>
                  <dt>Savings</dt>
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

            <!-- G-08: block, do not silently fix. A short-stock line keeps the quantity the shopper
                 chose and this button waits for them to change it. "<a>" when it works and "<button
                 aria-disabled>" when it does not, rather than one element pretending: a disabled
                 anchor is still followable by keyboard and by a crawler, and "pointer-events: none"
                 is not a disabled control to anyone using a keyboard.
                 "aria-disabled", never "disabled" (#109): "disabled" takes the button out of the tab
                 order, so a keyboard or screen-reader user could not reach it to hear WHY checkout is
                 blocked, which is the sentence aria-describedby points at. The button has no click
                 handler and no routerLink, so pressing it already does nothing. -->
            @if (canCheckout()) {
              <a class="btn btn--primary bag__checkout" routerLink="/checkout">Proceed to checkout</a>
            } @else {
              <button class="btn btn--primary bag__checkout" type="button" aria-disabled="true"
                [attr.aria-describedby]="'bag-checkout-blocked'">
                Proceed to checkout
              </button>
              <p class="bag__notice bag__notice--warn" id="bag-checkout-blocked">
                @if (blockedCount() === 1) {
                  Adjust or remove the item above before you check out.
                } @else {
                  Adjust or remove the {{ blockedCount() }} items above before you check out.
                }
              </p>
            }

            <p class="bag__muted bag__fineprint">
              Delivery is worked out at checkout, so this covers the items only.
            </p>

            <!-- Owner decision 2026-10-21: an overseas destination chosen in the header gets its fee and
                 estimate here. Sri Lanka keeps the sentence above and nothing else. -->
            @if (overseas(); as estimate) {
              <p class="bag__muted bag__fineprint" data-testid="bag-overseas-estimate">
                @if (estimate.isAvailable) {
                  Delivery to {{ estimate.country.name }}: {{ money(estimate.fee ?? 0) }}{{
                    days(estimate) ? ', ' + days(estimate) : ''
                  }}.
                } @else {
                  We cannot deliver to {{ estimate.country.name }} at the moment.
                }
              </p>
            }
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
    /* .qty, .qty > :focus-visible, .qty__step and .qty__input are **not** here. They were, as a
       byte-for-byte copy of the declarations QV-01 hoisted into the storefront stylesheet, which
       left two sources for one design-system piece and this one shadowing the global by the
       specificity a scoped attribute adds. Removed in round 2: the quantity stepper is the same
       control on this page, the product page and in Quick View, and the class names are the
       contract between them. See projects/storefront/src/styles.css. */
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
    .bag__checkout {
      width: 100%;

      /* 44 px is doc 02 GLB-09 / doc 04's tap-target floor, and the padding alone does not guarantee it
         at the smallest root font size. A disabled <button> and an <a> are different elements with
         different default box models, so the height is stated once here and applies to both — otherwise
         the enabled and blocked states of the same control differ in height and the summary jumps. */
      min-height: 44px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      text-align: center;
    }
    .bag__checkout:disabled,
    .bag__checkout[aria-disabled='true'] {
      cursor: not-allowed;
      opacity: 0.55;
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

  /** The header's overseas destination, or null for Sri Lanka (owner decision 2026-10-21). */
  protected readonly overseas = inject(DeliveryCountryService).overseasEstimate;

  protected days(estimate: OverseasEstimate): string | null {
    return workingDays(estimate.estimatedDaysMin, estimate.estimatedDaysMax);
  }

  protected readonly lines = this.cart.lines;
  protected readonly totals = this.cart.totals;
  protected readonly isEmpty = this.cart.isEmpty;
  protected readonly isLoading = this.cart.isLoading;
  protected readonly error = this.cart.error;

  protected readonly buyableCount = computed(() => this.totals().buyableItemCount);

  protected readonly unavailableCount = computed(
    () => this.lines().filter((line) => !isBuyable(line)).length,
  );

  /**
   * How many lines stand between this bag and checkout.
   *
   * **Not `unavailableCount()`, and the difference is the whole of G-08.** `isBuyable` counts a
   * short-stock line as buyable — it is in the subtotal, because the price is real and the items may be
   * restocked — but it cannot be *ordered*, because the API reserves the full quantity or refuses the
   * order. So the row that says "not in this total" and the row that says "fix this before you check
   * out" are counting two different things, and conflating them would either disable checkout for a line
   * that is fine or offer it for one the server will refuse.
   */
  protected readonly blockedCount = computed(
    () => this.lines().filter((line) => line.status !== 'Buyable').length,
  );

  /**
   * Whether Proceed to checkout is a link or a disabled button.
   *
   * The same rule the API applies (`CheckoutQuote.IsPlaceable`): every line must be `Buyable`. It is
   * asserted on both sides — here so the shopper is not sent to a page that will refuse them, and in the
   * use case because this check is a courtesy and the server's is the one that counts.
   */
  protected readonly canCheckout = computed(
    () => !this.isEmpty() && this.blockedCount() === 0,
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

    // **Puts every quantity box back in step with the bag.**
    //
    // `onQuantityInput` writes `input.value` by hand, which is right for immediate feedback and wrong on
    // its own: it bypasses the `[value]` binding, so when a change is refused `line.quantity` never
    // moves, Angular sees no change to the bound expression and never rewrites the element. The box then
    // showed a number the bag did not hold, and the next step computed from the real quantity and jumped.
    //
    // It depends on `busyLines()` as well as on `lines()`, and that is the part that matters: a refused
    // write changes **nothing** about the bag, so a re-sync keyed on the lines alone would never fire.
    // Every mutation flips a line busy and back, so this runs at the end of every attempt either way.
    afterRenderEffect(() => {
      const lines = this.lines();
      this.cart.busyLines();

      untracked(() => {
        for (const box of this.quantityBoxes()) {
          const element = box.nativeElement;
          const variantId = Number(element.dataset['variantId']);

          // A line with a change in flight keeps the number that was asked for. Without this the
          // effect fires on busy-*on* and immediately overwrites the shopper's own clamped value with
          // the quantity the bag still holds — so the box flicked back the instant they typed.
          if (this.cart.isBusy(variantId)) {
            continue;
          }

          const line = lines.find((candidate) => candidate.variantId === variantId);
          if (line && element.value !== String(line.quantity)) {
            element.value = String(line.quantity);
          }
        }
      });
    });
  }

  /** The quantity inputs, so the effect above can put each one back in step with its line. */
  private readonly quantityBoxes = viewChildren<ElementRef<HTMLInputElement>>('qtyBox');

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
    this.cart.moveToWishlist(line, this.label(line));
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
   *
   * **Any digits-only value clamps, however long.** There used to be a `Number.isSafeInteger` guard, so
   * a twenty-digit number was *reset* to the current quantity while `99` *clamped* to the ceiling — two
   * different answers to the same keystroke intent, decided by how long the number was. `Math.min`
   * handles the long ones correctly on its own, `Infinity` included.
   *
   * The box is written here for immediate feedback — a clamp the shopper cannot see is a clamp they will
   * fight — and `syncQuantityBoxes` puts it right again if the server disagrees.
   */
  protected onQuantityInput(line: CartLineDto, event: Event): void {
    const input = event.target as HTMLInputElement;
    const raw = input.value.trim();
    const typed = /^\d+$/.test(raw) ? Number(raw) : Number.NaN;
    const next = typed >= 1 ? Math.min(typed, line.maxQuantity) : line.quantity;

    input.value = String(next);

    if (next !== line.quantity) {
      this.cart.setQuantity(line.variantId, next, this.label(line));
    }
  }
}
