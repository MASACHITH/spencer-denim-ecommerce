import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { SafeLocalStorage } from '@spencer-denim/shared';

/** Where the saved product slugs live in this browser. */
const STORAGE_KEY = 'sd.wishlist.local';

/**
 * What a change to the wishlist managed.
 *
 * `'unavailable'` is not an error state to recover from — it is the ordinary answer in a private
 * window, with site data blocked, or on a full quota — and it means **nothing changed**, in storage or
 * in memory. A caller that shows a filled heart for it is lying to the shopper; a caller that destroys
 * something for it is losing their item.
 */
export type WishlistOutcome = 'saved' | 'removed' | 'unavailable';

/**
 * The wishlist heart on a product card, until the real wishlist exists (W2-08, WISH-01).
 *
 * It is deliberately local and per-browser: there is no wishlist API yet, and a heart that
 * silently forgot what it was told would be worse than no heart at all. Everything is held in
 * `localStorage` through {@link SafeLocalStorage}, so a private window or blocked site data
 * degrades to "nothing is saved" instead of throwing, and the server renders every heart
 * unpressed (it has no storage and must not guess).
 *
 * TODO(W2-08): swap the store for the customer's saved list and keep this as the signed-out
 * fallback. Nothing outside this service knows where the slugs are kept.
 */
@Injectable({ providedIn: 'root' })
export class LocalWishlist {
  private readonly storage = inject(SafeLocalStorage);
  private readonly slugs = signal<ReadonlySet<string>>(this.read());

  /** How many products are saved, for the header count (GLB-04). */
  readonly count: Signal<number> = computed(() => this.slugs().size);

  /** Whether one product is saved. Always false on the server. */
  has(slug: string): boolean {
    return this.slugs().has(slug);
  }

  /**
   * Saves a product, whether or not it was already saved. **Returns whether it is really saved.**
   *
   * Separate from {@link toggle} because "move to wishlist" (CART-01) is not a toggle: a shopper moving
   * a bag line to the wishlist must end with it saved, and `toggle` on something already saved would
   * remove it — so the bag line would be deleted and the wishlist entry with it.
   *
   * **It returns a boolean because the caller destroys something on the strength of it.**
   * `SafeLocalStorage.write` cannot throw — it catches everything and returns `false` — so the only
   * failure available here is a silent one, and a `void` return made it invisible: a private window,
   * blocked site data or a full quota meant the bag line was removed over a save that never happened,
   * and the item was then in neither place. `CartStore.moveToWishlist` keeps the line when this says
   * `false`.
   *
   * **It writes every time, even for a slug it already holds**, and that is the second half of the same
   * defect. It used to return `true` on an early `has()` check without attempting a write — so once
   * {@link toggle} had put a slug in the signal over a refused write, this vouched for a save that had
   * never happened and the bag line was destroyed anyway, with no message. The signal is not evidence
   * of persistence; only a write that returns `true` is. Re-writing an unchanged set is one cheap
   * `setItem` and it is the only way to find out whether storage is working now.
   */
  save(slug: string): boolean {
    return this.persist(new Set(this.slugs()).add(slug));
  }

  /**
   * Saves or unsaves a product, and says which it managed.
   *
   * **It is write-truthful**, which it was not: it used to set the signal first and discard
   * `write()`'s boolean, so a shopper in a private window tapped the heart, the badge incremented, the
   * heart filled, and nothing was stored — with nothing to tell them. That was a real defect on the
   * card and the product page on its own, and it was also how B2 survived a round: the signal it had
   * set made {@link save}'s early return vouch for a save that had not happened, and the bag line was
   * removed on exactly that strength.
   *
   * So the write happens first and the signal is only updated if it succeeded. `'unavailable'` means
   * nothing changed anywhere — the caller should say so rather than show a filled heart.
   */
  toggle(slug: string): WishlistOutcome {
    const next = new Set(this.slugs());
    const saving = !next.delete(slug);
    if (saving) {
      next.add(slug);
    }

    if (!this.persist(next)) {
      return 'unavailable';
    }

    return saving ? 'saved' : 'removed';
  }

  /**
   * The one place that writes. Storage first, signal second, and the signal is not touched unless the
   * write reported success — so nothing in this application can claim a product is saved when it is
   * not.
   */
  private persist(next: ReadonlySet<string>): boolean {
    if (!this.storage.write(STORAGE_KEY, JSON.stringify([...next]))) {
      return false;
    }

    this.slugs.set(next);
    return true;
  }

  /**
   * Lesson L1: this is data a browser hands us, not data we wrote this run. A value another tab,
   * an older build or a curious visitor left behind must not break a product grid, so anything
   * that is not an array of strings is read as an empty wishlist.
   */
  private read(): ReadonlySet<string> {
    const raw = this.storage.read(STORAGE_KEY);
    if (!raw) {
      return new Set();
    }

    try {
      const parsed: unknown = JSON.parse(raw);
      return new Set(
        Array.isArray(parsed) ? parsed.filter((value): value is string => typeof value === 'string') : [],
      );
    } catch {
      return new Set();
    }
  }
}
