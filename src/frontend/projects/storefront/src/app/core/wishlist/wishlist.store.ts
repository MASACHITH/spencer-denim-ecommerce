import { DestroyRef, Injectable, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import {
  LocalWishlist,
  WishlistEntry,
  WishlistOutcome,
  WishlistSave,
} from './local-wishlist.service';
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
 * A row, or the whole list, having just gone - so a page can put focus somewhere sensible.
 *
 * **It exists because removing a row destroys the control that was pressed**, which throws focus to
 * `<body>` exactly as a `[disabled]` attribute does. W2-08 round 1 kept `[disabled]` off the wishlist
 * page and then got the same defect from the other direction, on all three of its removal controls: a
 * keyboard or screen-reader shopper clearing a wishlist lost their place on every single item. Banning
 * the attribute was necessary and not sufficient, which is worth remembering the next time that rule is
 * quoted.
 *
 * The store reports *what happened*; where focus goes is the page's decision, because only the page
 * knows what it drew.
 */
export interface WishlistRemoval {
  /** `'row'` for one product, `'all'` for a clear. */
  readonly kind: 'row' | 'all';

  /**
   * Which visible row went, so the page can focus its successor. `-1` when the entry was not on screen -
   * past {@link WISHLIST_MAX_SLUGS}, or already gone.
   */
  readonly index: number;

  /** New on every removal, so two identical removals are still two events. */
  readonly ticket: number;
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
  private readonly removal = signal<WishlistRemoval | null>(null);

  /** Monotonic, so a repeat of the same removal is still a new value for the page to act on. */
  private removals = 0;

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

  /** The last row (or whole list) to go, for whoever has to re-home focus. See {@link WishlistRemoval}. */
  readonly lastRemoval: Signal<WishlistRemoval | null> = this.removal.asReadonly();

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

  /**
   * One row per saved product, newest first — **and no more rows than one request can answer for**.
   *
   * The slice is not cosmetic. {@link load} asks about the first {@link WISHLIST_MAX_SLUGS} slugs, so
   * anything past that would never be in {@link resolved} and would render as a row stuck on "Loading…"
   * for as long as the page stayed open — 201 rows drawn under a note saying 200 are shown. Nothing is
   * removed from the shopper's list: {@link count} and {@link truncated} still read the whole of it, and
   * the note says how many there are.
   */
  readonly rows: Signal<readonly WishlistRow[]> = computed(() => {
    const resolved = this.resolved();

    return this.local
      .entries()
      .slice(0, WISHLIST_MAX_SLUGS)
      .map((entry) => ({
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
    const index = this.visibleIndex(slug);

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
    this.removal.set({ kind: 'row', index, ticket: ++this.removals });
    return true;
  }

  /**
   * The heart, from wherever it is pressed (CARD-01, PDP-01).
   *
   * **It is here rather than only on {@link LocalWishlist} because a removal has to be *said*.** The
   * card's heart called `LocalWishlist.toggle` directly, so a row vanishing from the wishlist page
   * announced nothing at all while the row's own Remove button announced a full sentence - one feature,
   * two controls, one of them silent. Going through the store means every heart in the storefront
   * announces and reports its removal, so the page's live region and its focus handling cover all three
   * of its removal controls rather than one.
   *
   * The outcome is returned unchanged, because the heart's own accessible name is what tells a shopper
   * on a listing page that storage refused the write: there is no room for a sentence beside
   * twenty-four cards, so the control says it itself.
   */
  toggle(save: WishlistSave, label: string): WishlistOutcome {
    const index = this.local.has(save.slug) ? this.visibleIndex(save.slug) : -1;
    const outcome = this.local.toggle(save);

    switch (outcome) {
      case 'saved':
        this.failure.set(null);
        this.announce(`${label} saved to your wishlist. ${this.summary()}`);
        break;

      case 'removed':
        this.failure.set(null);
        this.announce(`${label} removed from your wishlist. ${this.summary()}`);
        this.removal.set({ kind: 'row', index, ticket: ++this.removals });
        break;

      case 'unavailable':
      default:
        // Nothing changed anywhere, so nothing is announced and nothing is reported as removed.
        this.failure.set(
          `We could not change your wishlist, so ${label} is exactly as it was. ` +
            'Your browser may be blocking site data - private browsing often does.',
        );
        break;
    }

    return outcome;
  }

  /** Whether one product is saved. Delegated, so a card needs one injection rather than two. */
  isSaved(slug: string): boolean {
    return this.local.has(slug);
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
    this.removal.set({ kind: 'all', index: 0, ticket: ++this.removals });
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
    const index = this.visibleIndex(slug);

    if (!this.local.remove(slug)) {
      this.failure.set(
        `${label} is in your bag, but we could not take it out of your wishlist, so it is still saved. ` +
          'Your browser may be blocking site data — private browsing often does.',
      );
      this.announce(`${label} added to your bag. It is still in your wishlist.`);
      return;
    }

    this.announce(`${label} moved to your bag. ${this.summary()}`);

    // A move removes a row too, so focus needs re-homing just as a Remove does - and here it is worse
    // than a plain removal: `ui/modal` returns focus to the control that opened it, which is the Move to
    // bag button that has just been destroyed with its row. The page's handler runs after the render
    // that removed it, so it wins.
    this.removal.set({ kind: 'row', index, ticket: ++this.removals });
  }

  /**
   * Announcements have to differ from the one before to be read out again, so an identical sentence gets
   * a trailing space. Invisible, and it is what makes two removals in a row both audible — the ADM-22
   * defect, where a repeated identical message was never announced a second time.
   */
  private announce(sentence: string): void {
    this.announcement.update((previous) => (previous === sentence ? `${sentence} ` : sentence));
  }

  /**
   * Where one slug is in the list the page has actually drawn, or `-1`.
   *
   * The *visible* list, so it matches what the page can put focus on: {@link rows} is sliced at
   * {@link WISHLIST_MAX_SLUGS}, and an entry past the cap has no control for a successor to follow.
   */
  private visibleIndex(slug: string): number {
    return this.local
      .entries()
      .slice(0, WISHLIST_MAX_SLUGS)
      .findIndex((entry) => entry.slug === slug);
  }

  private summary(): string {
    const count = this.local.count();
    if (count === 0) {
      return 'Your wishlist is now empty.';
    }

    return count === 1 ? 'Your wishlist now has 1 item.' : `Your wishlist now has ${count} items.`;
  }
}
