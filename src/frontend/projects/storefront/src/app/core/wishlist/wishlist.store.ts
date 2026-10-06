import { DestroyRef, Injectable, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { LocalWishlist, WishlistEntry } from './local-wishlist.service';
import { WISHLIST_MAX_SLUGS, WishlistApi, wishlistErrorMessage } from './wishlist-api';
import { ProductCardDto } from '../catalog/catalog.model';

/** What the wishlist page is doing. `loading` is only ever the first read. */
export type WishlistPhase = 'idle' | 'loading' | 'ready' | 'error';

/**
 * One row of the wishlist page.
 *
 * Three states, and the page draws a different thing for each:
 * - `pending` — the shopper has it saved and we have not heard back about it yet;
 * - a `product` — draw the card;
 * - resolved with no `product` — the shop has withdrawn it (doc 11 §575): name it from what is stored
 *   and offer Remove.
 */
export interface WishlistRow {
  readonly entry: WishlistEntry;
  readonly product: ProductCardDto | null;
  readonly pending: boolean;
}

/**
 * The shopper's wishlist, as a page can draw it (WISH-01).
 *
 * **Two sources, and only one of them is authoritative about membership.** {@link LocalWishlist} is the
 * wishlist: what is in it, in what order, and with which colourway. This store adds the one thing the
 * browser cannot know — what each of those products costs and whether the shop still sells it — by
 * asking `/api/wishlist`. So a removal takes effect in the same frame, from the local list, without
 * waiting for or re-issuing a request; and a price is never older than the page.
 *
 * **Provided in root**, like `CartStore`, because the header count (GLB-04) and the page are two views
 * of one list. Nothing here is read during a server render: the list is in `localStorage`, the server
 * has none, and a page that grew rows during hydration would be adding nodes Angular never sent. The
 * page calls {@link load} from `afterNextRender`.
 */
@Injectable({ providedIn: 'root' })
export class WishlistStore {
  private readonly api = inject(WishlistApi);
  private readonly local = inject(LocalWishlist);
  private readonly destroyRef = inject(DestroyRef);

  /** What the API has said about each slug. A present key with a null value means "withdrawn". */
  private readonly resolved = signal<ReadonlyMap<string, ProductCardDto | null>>(new Map());
  private readonly phase = signal<WishlistPhase>('idle');
  private readonly failure = signal<string | null>(null);
  private readonly announcement = signal('');
  private readonly moving = signal<string | null>(null);

  /** Guards against an answer from a superseded read overwriting a newer one. */
  private ticket = 0;

  /** The saved products, newest first. The wishlist itself, straight from the browser's list. */
  readonly entries = this.local.entries;

  /** What the header badge shows (GLB-04), including items the shop has withdrawn. */
  readonly count = this.local.count;

  readonly isEmpty = computed(() => this.local.count() === 0);

  readonly status: Signal<WishlistPhase> = this.phase.asReadonly();

  readonly isLoading = computed(() => this.phase() === 'loading');

  /** The last thing that went wrong, as a sentence. */
  readonly error: Signal<string | null> = this.failure.asReadonly();

  /**
   * The slug whose "Move to bag" is open, or null.
   *
   * The page uses it to render exactly one size chooser. It is a slug and not a boolean because a
   * wishlist entry is a *product* — `ProductCardDto` carries colours and no variants at all — so there
   * is a size to choose before anything can go in a bag (doc 11 §539: "move to bag (size must be chosen
   * first)").
   */
  readonly movingSlug: Signal<string | null> = this.moving.asReadonly();

  /**
   * One sentence for the live region, so a screen reader hears what changed.
   *
   * A signal rather than a direct DOM write, exactly as `CartStore.lastChange` is: the region is
   * rendered by the component that owns it, and a value that only ever moves forward is one a test can
   * read without racing the announcement.
   */
  readonly lastChange: Signal<string> = this.announcement.asReadonly();

  /**
   * True when the shopper has saved more than one request can ask about.
   *
   * Nothing is removed from their list — see `GetWishlistQueryHandler.MaxSlugs` for why truncating the
   * *request* is the least bad of the three options — but the page has to say so rather than appear to
   * have lost the rest.
   */
  readonly truncated = computed(() => this.local.count() > WISHLIST_MAX_SLUGS);

  /** One row per saved product, newest first. */
  readonly rows: Signal<readonly WishlistRow[]> = computed(() => {
    const resolved = this.resolved();

    return this.local.entries().map((entry) => ({
      entry,
      product: resolved.get(entry.slug) ?? null,
      pending: !resolved.has(entry.slug),
    }));
  });

  /** The withdrawn rows, which the page gathers a single explanation for. */
  readonly unavailableCount = computed(
    () => this.rows().filter((row) => !row.pending && row.product === null).length,
  );

  /**
   * Reads the live cards for everything currently saved.
   *
   * Called from the page's `afterNextRender`. An empty wishlist issues no request at all: the answer is
   * known, and a shop should not be asked a question whose answer is in the browser.
   */
  load(): void {
    const slugs = this.local.slugs().slice(0, WISHLIST_MAX_SLUGS);
    const ticket = ++this.ticket;

    if (slugs.length === 0) {
      this.resolved.set(new Map());
      this.failure.set(null);
      this.phase.set('ready');
      return;
    }

    if (this.phase() !== 'ready') {
      this.phase.set('loading');
    }

    this.api
      .get(slugs)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          if (ticket !== this.ticket) {
            return;
          }

          const next = new Map<string, ProductCardDto | null>();
          for (const item of response.items ?? []) {
            if (typeof item?.slug === 'string') {
              next.set(item.slug, item.product ?? null);
            }
          }

          this.resolved.set(next);
          this.failure.set(null);
          this.phase.set('ready');
        },
        error: (error: unknown) => {
          if (ticket !== this.ticket) {
            return;
          }

          // **A failed read never empties the page.** The saved list is in the browser and is still
          // correct; what failed is the prices and the photographs. So the rows stay, as `pending`, with
          // a message and a Try again — rather than a wishlist that reads as empty, which is
          // indistinguishable from the shop having lost it. That is the same decision
          // `CartStore.rejectRead` makes, for the same reason.
          this.failure.set(wishlistErrorMessage(error));
          this.phase.set('error');
        },
      });
  }

  /** Asks again after a failed read, behind a "Try again" button. */
  retry(): void {
    this.failure.set(null);
    this.load();
  }

  /**
   * Takes one product out (WISH-01: remove).
   *
   * `label` is what to call it in the announcement — the live name when the card arrived, the stored one
   * when the shop has withdrawn it, and "This item" when neither is known.
   */
  remove(slug: string, label: string): boolean {
    if (!this.local.remove(slug)) {
      // Storage refused the write, so the item is **still saved**. Saying "removed" and dropping the row
      // would be the mirror of a filled heart over a refused save: the page would disagree with the
      // browser until the next reload, and the shopper would have no idea.
      this.failure.set(
        `We could not remove ${label} from your wishlist, so it is still there. ` +
          'Your browser may be blocking site data — private browsing often does.',
      );
      return false;
    }

    this.failure.set(null);
    this.announce(`${label} removed from your wishlist. ${this.summary()}`);
    return true;
  }

  /**
   * Empties the wishlist (WISH-01: clear).
   *
   * It does not clear {@link resolved}: those are answers about products, not membership, and keeping
   * them means an Undo — or a save made from another tab and picked up on the next read — does not have
   * to re-ask. Nothing is drawn from them unless the slug is in the list.
   */
  clear(): boolean {
    const emptied = this.local.count();

    if (!this.local.clear()) {
      this.failure.set(
        'We could not clear your wishlist, so everything is still saved. ' +
          'Your browser may be blocking site data — private browsing often does.',
      );
      return false;
    }

    this.failure.set(null);
    this.moving.set(null);
    this.announce(
      emptied === 1
        ? 'Wishlist cleared. 1 item removed. Your wishlist is now empty.'
        : `Wishlist cleared. ${emptied} items removed. Your wishlist is now empty.`,
    );
    return true;
  }

  /**
   * Opens the size chooser for one saved product (WISH-01: move to bag).
   *
   * Nothing is added and nothing is removed here. A wishlist entry names a product, and a bag line needs
   * a variant, so the shopper picks a colour and a size first (doc 11 §539) — which is also why this is
   * Quick View rather than a one-tap add: the dialog is the only thing in the storefront that has the
   * variants.
   */
  beginMove(slug: string): void {
    this.failure.set(null);
    this.moving.set(slug);
  }

  /** The chooser was dismissed without adding anything. The item stays saved. */
  cancelMove(): void {
    this.moving.set(null);
  }

  /**
   * The bag has accepted the item, so it leaves the wishlist (WISH-01: move to bag).
   *
   * **This is only ever called after a successful add**, which is the whole of the protection. W2-09
   * shipped the mirror of this defect in the other direction — "move to wishlist" removed the bag line
   * over a save that had silently failed, and the item was then in neither place — and this is the same
   * exchange seen from the other side. `QuickView` raises its `added` output in the one branch where
   * `CartStore` has answered with a bag and no error; a sold-out, offline or refused add raises nothing,
   * the dialog stays open with the reason on it, and the item is still in the wishlist.
   *
   * **If the removal then fails, the item is in both places and the shopper is told.** That is the right
   * way round: an item in the bag *and* the wishlist is a tidy-up, an item in neither is a loss.
   */
  completeMove(slug: string, label: string): void {
    this.moving.set(null);

    if (!this.local.remove(slug)) {
      this.failure.set(
        `${label} is in your bag, but we could not take it out of your wishlist, so it is still saved. ` +
          'Your browser may be blocking site data — private browsing often does.',
      );
      this.announce(`${label} added to your bag. It is still in your wishlist.`);
      return;
    }

    this.announce(`${label} moved to your bag. ${this.summary()}`);
  }

  /**
   * Announcements have to differ from the one before to be read out again, so an identical sentence gets
   * a trailing space. Invisible, and it is what makes two removals in a row both audible — the ADM-22
   * defect, where a repeated identical message was never announced a second time.
   */
  private announce(sentence: string): void {
    this.announcement.update((previous) => (previous === sentence ? `${sentence} ` : sentence));
  }

  private summary(): string {
    const count = this.local.count();
    if (count === 0) {
      return 'Your wishlist is now empty.';
    }

    return count === 1 ? 'Your wishlist now has 1 item.' : `Your wishlist now has ${count} items.`;
  }
}
