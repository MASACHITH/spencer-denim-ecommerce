import { DestroyRef, Injectable, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable } from 'rxjs';

import { CartApi, cartErrorMessage, cartFailure } from './cart-api';
import { CartDto, EMPTY_CART, cartLineNotice } from './cart.model';
import { ServerHandover } from '../platform/server-handover';
import { LocalWishlist } from '../wishlist/local-wishlist.service';

/** Where the server leaves the bag it rendered, for the browser that hydrates the page. */
const HANDOVER_KEY = 'cart';

/** What the bag is doing. `loading` is only ever the very first read. */
export type CartPhase = 'idle' | 'loading' | 'ready' | 'error';

/**
 * The visitor's bag (CART-01, CART-02, GLB-04).
 *
 * Provided in root, because the bag page, the mini bag and the header count are three views of one
 * thing: a quantity changed on the bag page has to move the header badge in the same frame, and two
 * stores would mean two answers to "how many things are in my bag".
 *
 * **Server-side rendering.** The bag page calls {@link load} from its constructor, so the request is
 * already in flight while the server renders and the HTML carries the lines. What the server fetched
 * is handed to the browser through {@link ServerHandover}, and this store takes it in its
 * **constructor** — not in an effect, and the timing is the whole point. The header is created before
 * the routed page is, so if the handover were taken later the header would render a count of 0 against
 * a server-rendered badge of N and hydration would find a mismatched DOM. Taking it at construction
 * means the browser's first render is identical to the server's.
 *
 * **The bag is per-visitor, so it is nowhere it should not be.** The only page that renders bag
 * contents on the server is `/bag`, which answers `Cache-Control: no-store` and `noindex`; the mini
 * bag's list is rendered only once it has been opened, which can only happen in a browser. The cart
 * token is never in the markup or in the transfer state at all — it lives only in the `HttpOnly`
 * cookie.
 */
@Injectable({ providedIn: 'root' })
export class CartStore {
  private readonly api = inject(CartApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly handover = inject(ServerHandover);
  private readonly wishlist = inject(LocalWishlist);

  private readonly current = signal<CartDto>(EMPTY_CART);
  private readonly phase = signal<CartPhase>('idle');
  private readonly failure = signal<string | null>(null);
  private readonly pending = signal<ReadonlySet<number>>(new Set());
  private readonly adding = signal(false);
  private readonly announcement = signal('');
  private readonly openedBy = signal<number | null>(null);

  /** Guards against two reads in flight, and against the browser re-reading what it was handed. */
  private ticket = 0;
  private loaded = false;

  constructor() {
    // See the type remarks: this has to happen before anything renders.
    const handedOver = this.handover.take<CartDto>(HANDOVER_KEY);
    if (handedOver) {
      this.current.set(handedOver);
      this.phase.set('ready');
    }
  }

  readonly cart: Signal<CartDto> = this.current.asReadonly();

  readonly lines = computed(() => this.current().lines);

  readonly totals = computed(() => this.current().totals);

  /** What the header badge shows (GLB-04): everything in the bag, buyable or not. */
  readonly itemCount = computed(() => this.current().totals.itemCount);

  readonly isEmpty = computed(() => this.current().lines.length === 0);

  readonly status: Signal<CartPhase> = this.phase.asReadonly();

  /** True only while the very first read is outstanding, so the page can show a skeleton once. */
  readonly isLoading = computed(() => this.phase() === 'loading');

  /** The last thing that went wrong, as a sentence. Cleared by the next successful change. */
  readonly error: Signal<string | null> = this.failure.asReadonly();

  /** True while an Add to Bag is in flight, so the button can say so and refuse a second press. */
  readonly isAdding: Signal<boolean> = this.adding.asReadonly();

  /** Variant ids with a change in flight: that line's controls are disabled, not the whole page. */
  readonly busyLines: Signal<ReadonlySet<number>> = this.pending.asReadonly();

  /**
   * One sentence for the live region, so a screen reader hears what changed (CART-02).
   *
   * A signal rather than a direct DOM write: the region is rendered by the component that owns it, and
   * a value that only ever moves forward is one a test can read without racing the announcement.
   */
  readonly lastChange: Signal<string> = this.announcement.asReadonly();

  /**
   * The variant whose Add to Bag last succeeded, or null. The mini bag watches it so that adding
   * something opens the drawer — and it is a *new* value each time, so adding the same variant twice
   * opens it twice.
   */
  readonly lastAdded: Signal<number | null> = this.openedBy.asReadonly();

  isBusy(variantId: number): boolean {
    return this.pending().has(variantId);
  }

  /**
   * Reads the bag.
   *
   * Called synchronously from the bag page's constructor so the request is in flight during the
   * server render. In the browser, a page that was handed a bag by the server still reads again: the
   * handover is a snapshot of whatever the stock and prices were when the server rendered, and a bag
   * is the one page where "nearly right" is a commercial problem.
   */
  load(): void {
    this.read(false);
  }

  /**
   * Reads the bag.
   *
   * `keepMessage` exists for one case and it was found by a browser test: a sold-out add sets the
   * message *and* asks for a re-read, and a successful read used to clear the message on the way in —
   * so the shopper was told "that size has just sold out" for a few milliseconds and then told
   * nothing at all. The read's own success says nothing about whether the add succeeded, so it must
   * not answer for it.
   */
  private read(keepMessage: boolean): void {
    const ticket = ++this.ticket;
    const alreadyShowing = this.phase() === 'ready';

    if (!alreadyShowing) {
      this.phase.set('loading');
    }

    this.api
      .get()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (cart) => {
          this.handover.give<CartDto>(HANDOVER_KEY, cart);
          this.accept(ticket, cart, keepMessage);
        },
        error: (error: unknown) => this.rejectRead(ticket, error),
      });

    this.loaded = true;
  }

  /**
   * Reads the bag once per application instance, for whoever needs the count rather than the contents
   * — the header on every page.
   *
   * It is separate from {@link load} because the header must not fetch during a server render: the
   * count would then be in the HTML of every page, which makes every page per-visitor and uncacheable
   * for the sake of one number. The shell calls this after the first render instead.
   */
  ensureLoaded(): void {
    if (!this.loaded) {
      this.load();
    }
  }

  /** Add to Bag (PDP-03). `quantity` is 1–10; the server caps the line's total at 10. */
  add(variantId: number, quantity: number, label: string): void {
    if (this.adding()) {
      return;
    }

    this.adding.set(true);
    this.failure.set(null);

    this.api
      .add(variantId, quantity)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (cart) => {
          this.adding.set(false);
          this.replace(cart);
          this.announce(`${label} added to your bag. ${this.bagSummary(cart)}`);

          // A new value every time, so the mini bag opens again when the same variant is added twice.
          this.openedBy.set(null);
          this.openedBy.set(variantId);
        },
        error: (error: unknown) => {
          this.adding.set(false);
          this.fail(error);

          // A sold-out add is the one failure where the bag itself may have moved on: the line's stock
          // is now 0, and the shopper is about to be told so. Re-read rather than leave the page saying
          // something the server has just contradicted.
          if (cartFailure(error) === 'soldOut') {
            this.read(true);
          }
        },
      });
  }

  /** Change one line's quantity (CART-01). */
  setQuantity(variantId: number, quantity: number, label: string): void {
    this.mutate(variantId, this.api.setQuantity(variantId, quantity), (cart) =>
      `${label} quantity changed to ${quantity}. ${this.bagSummary(cart)}`,
    );
  }

  /** Remove one line (CART-01). */
  remove(variantId: number, label: string): void {
    this.mutate(variantId, this.api.remove(variantId), (cart) =>
      `${label} removed from your bag. ${this.bagSummary(cart)}`,
    );
  }

  /**
   * Move to wishlist (CART-01).
   *
   * **It saves to this browser, not to an account, and that is a gap rather than a shortcut.** There is
   * no wishlist API: W2-08 is not built, and doc 08 §1.7 already specifies that a guest wishlist is
   * `localStorage` merged into `sales.WishlistItems` on login. So this writes to the same
   * {@link LocalWishlist} the product card's heart has used since W2-04, and removes the line **only if
   * that write succeeded**. The ordering alone was not the protection it was described as: storage here
   * cannot throw, it returns `false`, so the check is what protects the item and the order is merely
   * what makes the check possible.
   *
   * **What it cannot keep: the colour and the size.** `LocalWishlist` holds product slugs, as the heart
   * does, while `sales.WishlistItems` has a `ProductColourId`. Moving "Beige / M" saves the product and
   * not the colourway. That is the honest limit of what exists today and is reported with the task; the
   * alternative — inventing a table — is worse.
   */
  moveToWishlist(variantId: number, slug: string, label: string): void {
    // **Nothing is removed unless the save really happened.** The save coming first was never
    // protection on its own: `SafeLocalStorage.write` cannot throw, so the only failure it has is a
    // silent `false`, and discarding that meant the line was deleted over a save that did not occur —
    // in a private window, with site data blocked, or on a full quota. The shopper is told, in an
    // `role="alert"`, and the item is still where they left it.
    if (!this.wishlist.save(slug)) {
      this.failure.set(
        'We could not save that to your wishlist, so it is still in your bag. ' +
          'Your browser may be blocking site data — private browsing often does.',
      );
      return;
    }

    this.mutate(variantId, this.api.remove(variantId), (cart) =>
      `${label} moved to your wishlist. ${this.bagSummary(cart)}`,
    );
  }

  /** Asks again after a failed read, behind a "Try again" button. */
  retry(): void {
    this.failure.set(null);
    this.load();
  }

  private mutate(
    variantId: number,
    request: Observable<CartDto>,
    describe: (cart: CartDto) => string,
  ): void {
    if (this.isBusy(variantId)) {
      return;
    }

    this.setBusy(variantId, true);
    this.failure.set(null);

    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (cart) => {
        this.setBusy(variantId, false);
        this.replace(cart);
        this.announce(describe(cart));
      },
      error: (error: unknown) => {
        this.setBusy(variantId, false);
        this.fail(error);
      },
    });
  }

  private accept(ticket: number, cart: CartDto, keepMessage = false): void {
    if (ticket !== this.ticket) {
      return;
    }

    this.current.set(cart);
    if (!keepMessage) {
      this.failure.set(null);
    }
    this.phase.set('ready');
  }

  /**
   * A failed read keeps whatever the bag was last known to hold.
   *
   * Emptying it would be the worse lie of the two: a shopper whose connection dropped would be told
   * their bag is empty, which is indistinguishable from the shop having lost it.
   */
  private rejectRead(ticket: number, error: unknown): void {
    if (ticket !== this.ticket) {
      return;
    }

    this.failure.set(cartErrorMessage(error));
    this.phase.set(this.current().lines.length > 0 ? 'ready' : 'error');
  }

  private replace(cart: CartDto): void {
    // A write's answer is the bag as it now is, so it supersedes any read still in flight.
    this.ticket++;
    this.current.set(cart);
    this.failure.set(null);
    this.phase.set('ready');
    this.loaded = true;
  }

  private fail(error: unknown): void {
    this.failure.set(cartErrorMessage(error));
  }

  private setBusy(variantId: number, busy: boolean): void {
    const next = new Set(this.pending());
    if (busy) {
      next.add(variantId);
    } else {
      next.delete(variantId);
    }

    this.pending.set(next);
  }

  /**
   * Announcements have to differ from the one before to be read out again, so an identical sentence
   * gets a trailing space. Invisible, and it is what makes two removals in a row both audible.
   */
  private announce(sentence: string): void {
    this.announcement.update((previous) => (previous === sentence ? `${sentence} ` : sentence));
  }

  private bagSummary(cart: CartDto): string {
    const count = cart.totals.itemCount;
    if (count === 0) {
      return 'Your bag is now empty.';
    }

    const items = count === 1 ? '1 item' : `${count} items`;

    // The notices are part of the state of the bag, so a screen reader hears them rather than only
    // seeing them: a line that has gone out of stock is the one thing a sighted shopper notices from
    // the colour of the row.
    const warnings = cart.lines.filter((line) => cartLineNotice(line) !== null).length;
    const caveat = warnings === 0 ? '' : warnings === 1 ? ' 1 item needs attention.' : ` ${warnings} items need attention.`;

    return `Your bag now has ${items}.${caveat}`;
  }
}
