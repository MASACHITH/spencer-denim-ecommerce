import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { Drawer } from '@spencer-denim/shared';

import { CartLineDto, cartLineNotice } from '../../core/cart/cart.model';
import { CartStore } from '../../core/cart/cart.store';
import { formatLkr } from '../../core/catalog/money';

/**
 * The slide-out mini bag (CART-02).
 *
 * The drawer itself — focus moves in, Tab cycles inside it, Escape closes it, focus returns to
 * whatever opened it — is the shared `sd-drawer`, which has its own 70 tests. This component only
 * supplies what is inside, exactly as `sd-mobile-nav` does. Building a second drawer for the bag would
 * mean two implementations of a focus trap.
 *
 * **What is deliberately not in here: the quantity control.** CART-01 puts quantity, "move to
 * wishlist" and the promo field on the bag page, and CART-02 asks for a slide-out bag. At 320 px the
 * drawer is 282 px wide, and a thumbnail, a name, a three-part quantity stepper, a remove button and a
 * price in one row cannot all hold 44 px without wrapping into something unreadable. So the mini bag
 * shows what is in the bag and takes things out of it, and "View bag" is one tap away for everything
 * else. See the task report.
 *
 * **The list renders only once the drawer has been open.** `sd-drawer` keeps its panel in the DOM when
 * closed, so anything in its body would be in the HTML of every page — and the bag is per-visitor, so
 * that would make every page's markup personal and uncacheable (and would ship the lines twice on the
 * bag page). The drawer can only be opened in a browser, so on the server this is empty.
 */
@Component({
  selector: 'sd-mini-bag',
  imports: [Drawer, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-drawer
      [open]="open()"
      panelId="mini-bag"
      side="end"
      heading="Your bag"
      closeLabel="Close bag"
      (closed)="closed.emit()"
    >
      <div class="mini-bag">
        <!-- Inside the dialog, because a dialog with aria-modal="true" can hide everything outside it
             from a screen reader, and doc 04 §4 says to announce inside an open dialog for that reason.
             The sibling region at the bottom covers the bag page and the product page, where the drawer
             is shut and its contents are not in the accessibility tree at all.

             Which of the two receives a sentence is decided **when the sentence is produced**, in the
             effect below — not computed from open() here. Computing it from open() meant the text moved
             between the regions every time the drawer was toggled, and a text change in a live region is
             an announcement: add (announced inside), Escape (the identical sentence announced again from
             outside). Both are plain state now, so toggling moves nothing. -->
        <p class="sr-only" role="status" aria-live="polite">{{ insideText() }}</p>

        @if (open()) {
          @if (error(); as message) {
            <p class="mini-bag__error" role="alert">{{ message }}</p>
          }

          @if (lines().length === 0) {
            <p class="mini-bag__empty">Your bag is empty.</p>
            <a class="btn btn--primary btn--block" routerLink="/" (click)="closed.emit()">
              Start shopping
            </a>
          } @else {
            <ul class="mini-bag__list">
              @for (line of lines(); track line.variantId) {
                <li class="mini-bag__line" [class.is-unavailable]="line.status === 'Unavailable'">
                  <a
                    class="mini-bag__thumb"
                    [routerLink]="['/product', line.slug]"
                    tabindex="-1"
                    aria-hidden="true"
                    (click)="closed.emit()"
                  >
                    @if (line.imageUrl) {
                      <img [src]="line.imageUrl" alt="" width="64" height="85" loading="lazy" />
                    }
                  </a>

                  <div class="mini-bag__body">
                    <a
                      class="mini-bag__name"
                      [routerLink]="['/product', line.slug]"
                      (click)="closed.emit()"
                      >{{ line.productName }}</a
                    >
                    <p class="mini-bag__meta">{{ line.colourName }} · {{ line.sizeLabel }}</p>
                    <p class="mini-bag__meta">
                      Qty {{ line.quantity }}
                      @if (line.price) {
                        <span class="mini-bag__price">{{ money(line.lineSubtotal) }}</span>
                      }
                    </p>

                    @if (notice(line); as text) {
                      <p class="mini-bag__notice">{{ text }}</p>
                    }

                    <button
                      class="link mini-bag__remove"
                      type="button"
                      [disabled]="cart.isBusy(line.variantId)"
                      [attr.aria-label]="'Remove ' + label(line) + ' from your bag'"
                      (click)="remove(line)"
                    >
                      Remove
                    </button>
                  </div>
                </li>
              }
            </ul>

            <div class="mini-bag__foot">
              <p class="mini-bag__total">
                <span>Subtotal</span>
                <strong>{{ money(totals().itemsSubtotal) }}</strong>
              </p>
              <!-- Not "total": the delivery fee has no source yet (#33, and
                   sales.DeliveryMethodZoneRates is #53's table), so the bag says what the items cost
                   and where the rest is worked out rather than printing a figure nobody has agreed. -->
              <p class="mini-bag__note">Delivery is calculated at checkout.</p>
              <a class="btn btn--primary btn--block" routerLink="/bag" (click)="closed.emit()">
                View bag
              </a>
            </div>
          }
        }
      </div>
    </sd-drawer>

    <!-- The other half of the pair above: it receives the sentence when the drawer was shut at the
         moment of the change. -->
    <p class="sr-only" role="status" aria-live="polite">{{ outsideText() }}</p>
  `,
  styles: `
    :host {
      display: contents;
    }
    .mini-bag {
      display: grid;
      gap: var(--space-2);
    }
    .mini-bag__error {
      padding: var(--space-1);
      border: 1px solid var(--color-sale);
      color: var(--color-sale);
      font-size: var(--text-label);
    }
    .mini-bag__empty {
      padding-block: var(--space-2);
      color: var(--color-text-muted);
    }
    .mini-bag__list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
    }
    .mini-bag__line {
      display: grid;
      grid-template-columns: 64px minmax(0, 1fr);
      gap: var(--space-1-5);
      padding-bottom: var(--space-2);
      border-bottom: 1px solid var(--color-border);
    }
    .mini-bag__line.is-unavailable .mini-bag__name {
      color: var(--color-text-muted);
    }
    .mini-bag__thumb {
      display: block;
      background: var(--color-bg-alt);
      aspect-ratio: 3 / 4;
    }
    .mini-bag__thumb img {
      display: block;
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .mini-bag__body {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .mini-bag__name {
      font-weight: var(--weight-medium);
      text-decoration: none;
      color: inherit;
    }
    .mini-bag__name:hover {
      text-decoration: underline;
    }
    .mini-bag__meta {
      font-size: var(--text-label);
      color: var(--color-text-muted);
    }
    .mini-bag__price {
      margin-inline-start: var(--space-1);
      color: var(--color-text);
      font-variant-numeric: tabular-nums;
    }
    .mini-bag__notice {
      font-size: var(--text-label);
      color: var(--color-sale);
    }
    /* A text button, so it is its own 44 px target without an icon to guess at. justify-self:start
       keeps the hit area to the word rather than the whole column. */
    .mini-bag__remove {
      justify-self: start;
      min-height: var(--tap);
      padding-inline: 0;
    }
    .mini-bag__foot {
      display: grid;
      gap: var(--space-1);
    }
    .mini-bag__total {
      display: flex;
      justify-content: space-between;
      gap: var(--space-2);
      font-size: var(--text-h4);
      font-variant-numeric: tabular-nums;
    }
    .mini-bag__note {
      font-size: var(--text-label);
      color: var(--color-text-muted);
    }
  `,
})
export class MiniBag {
  /** Whether the drawer is showing. The header owns this state, as it does for the mobile menu. */
  readonly open = input.required<boolean>();

  readonly closed = output<void>();

  protected readonly cart = inject(CartStore);

  protected readonly lines = this.cart.lines;
  protected readonly totals = this.cart.totals;
  protected readonly error = this.cart.error;

  /**
   * The two live regions' text, as **state** rather than as a computation over `open()`.
   *
   * `CartStore.lastChange` is app-lifetime: it holds the last sentence for as long as the application
   * runs. So a region whose contents are `open() ? lastChange() : ''` changes whenever the drawer is
   * toggled, and a live region that changes speaks — which meant Escape re-announced whatever the add
   * had just announced. Writing the sentence once, to whichever region is live at that moment, makes
   * toggling a no-op for both.
   */
  private readonly inside = signal('');
  private readonly outside = signal('');

  protected readonly insideText = this.inside.asReadonly();
  protected readonly outsideText = this.outside.asReadonly();

  constructor() {
    effect(() => {
      const sentence = this.cart.lastChange();

      // `untracked`, so the effect depends on the sentence alone. Reading `open()` tracked would make
      // this re-run on every toggle, which is the behaviour it exists to remove.
      untracked(() => {
        if (sentence === '') {
          return;
        }

        // The other region is cleared so that exactly one ever holds text, which keeps the invariant
        // assertable. Clearing is safe: `aria-relevant` defaults to "additions text", so a region
        // losing its contents announces nothing.
        if (this.open()) {
          this.inside.set(sentence);
          this.outside.set('');
        } else {
          this.outside.set(sentence);
          this.inside.set('');
        }
      });
    });
  }

  /** How many lines need attention, for the heading's own sake rather than only colour. */
  protected readonly warnings = computed(
    () => this.lines().filter((line) => cartLineNotice(line) !== null).length,
  );

  protected notice(line: CartLineDto): string | null {
    return cartLineNotice(line);
  }

  protected label(line: CartLineDto): string {
    return `${line.productName}, ${line.colourName}, size ${line.sizeLabel}`;
  }

  protected money(amount: number | null): string {
    return amount === null ? '' : formatLkr(amount);
  }

  protected remove(line: CartLineDto): void {
    this.cart.remove(line.variantId, this.label(line));
  }
}
