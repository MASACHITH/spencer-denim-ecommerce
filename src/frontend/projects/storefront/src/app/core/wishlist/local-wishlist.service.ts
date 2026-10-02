import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { SafeLocalStorage } from '@spencer-denim/shared';

/** Where the saved product slugs live in this browser. */
const STORAGE_KEY = 'sd.wishlist.local';

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
   * Saves a product, whether or not it was already saved.
   *
   * Separate from {@link toggle} because "move to wishlist" (CART-01) is not a toggle: a shopper moving
   * a bag line to the wishlist must end with it saved, and `toggle` on something already saved would
   * remove it — so the bag line would be deleted and the wishlist entry with it.
   */
  save(slug: string): void {
    if (this.slugs().has(slug)) {
      return;
    }

    const next = new Set(this.slugs()).add(slug);
    this.slugs.set(next);
    this.storage.write(STORAGE_KEY, JSON.stringify([...next]));
  }

  /** Saves or unsaves a product, and returns its new state. */
  toggle(slug: string): boolean {
    const next = new Set(this.slugs());
    const saved = !next.delete(slug);
    if (saved) {
      next.add(slug);
    }

    this.slugs.set(next);
    this.storage.write(STORAGE_KEY, JSON.stringify([...next]));
    return saved;
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
