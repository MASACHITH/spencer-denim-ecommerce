import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { SafeLocalStorage } from '@spencer-denim/shared';

/** Where the saved products live in this browser. */
const STORAGE_KEY = 'sd.wishlist.local';

/** The shape of what is stored today. Version 1 was a bare array of slugs; see {@link LocalWishlist.read}. */
const STORAGE_VERSION = 2;

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
 * One saved product, as this browser holds it (WISH-01).
 *
 * **It holds keys and labels, not a product.** The slug is the key; the name and the colourway are kept
 * only so that an entry can be *named and removed* when the shop no longer sells it (doc 11 §575) — at
 * which point the API has no card to send and the page would otherwise have nothing to print but a
 * slug. Everything a card draws (price, photograph, stock, rating, colours) is read live from
 * `/api/wishlist` each time the page is opened, because a wishlist is a list a shopper comes back to in
 * a week and a stored price is a price that has since changed. That is the opposite trade from
 * `RecentlyViewed` (PDP-07), which stores whole cards and whose own `TODO(W2-07)` says it should stop.
 */
export interface WishlistEntry {
  readonly slug: string;

  /** What the product was called when it was saved, or null when we were never told. */
  readonly name: string | null;

  /**
   * `catalog.ProductColours.Id` for the colourway the shopper was looking at, or null when they chose
   * none — the heart on a listing card has no colour selected (CARD-01). Doc 08 §3.5 keeps
   * `WishlistItems.ProductColourId` nullable for exactly this, and doc 11 §553 says "optional colour".
   */
  readonly colourId: number | null;

  readonly colourName: string | null;

  /**
   * `Date.now()` when it was first saved, or 0 for an entry carried over from the version-1 shape,
   * which had no save times.
   *
   * The stored list is kept in newest-first order by the writes themselves rather than sorted on this
   * field, so a version-1 list keeps the order it was written in instead of being reordered by a
   * timestamp nobody recorded. The field is what a later "sort by date saved" would use, and what tells
   * a migrated entry from a new one.
   */
  readonly addedAt: number;
}

/**
 * What a caller knows when it saves something.
 *
 * **An absent field means "say nothing about it", not "clear it".** That distinction is here because of
 * a defect this project has already shipped once: W1-12's category save did not mention the image
 * columns and cleared every one of them, past 1,571 passing tests, because every fixture had those
 * columns null. The same shape exists here — the heart on a listing card knows the product's name and
 * nothing about a colourway, while "move to wishlist" from the bag knows both — so a card's heart on a
 * product saved from the bag must not drop the colour it was saved with. `colourId: undefined` keeps
 * what is stored; `colourId: null` is an explicit "no colour".
 */
export interface WishlistSave {
  readonly slug: string;
  readonly name?: string | null;
  readonly colourId?: number | null;
  readonly colourName?: string | null;
}

/**
 * The products this browser has saved (WISH-01).
 *
 * **It is deliberately per-browser.** Doc 02's WISH-01 line says "guests keep it in the browser", doc 08
 * §1.7 lists the guest wishlist under "Client-side only (no tables)", and doc 11 §553 says it again. The
 * bag went the other way — server-side for everyone, with a cookie token — for the four reasons doc 08
 * §3.5 sets out, and none of them applies to a wishlist: there is nothing to price, nothing to check
 * against stock at save time, and no header count rendered on the server. So this service is the whole
 * of a guest's wishlist, and `sales.WishlistItems` is where W3-01 will merge it on sign-in.
 *
 * Everything is held in `localStorage` through {@link SafeLocalStorage}, so a private window or blocked
 * site data degrades to "nothing is saved" instead of throwing, and the server renders every heart
 * unpressed (it has no storage and must not guess).
 */
@Injectable({ providedIn: 'root' })
export class LocalWishlist {
  private readonly storage = inject(SafeLocalStorage);
  private readonly saved = signal<readonly WishlistEntry[]>(this.read());

  /**
   * The saved products, **most recently saved first**.
   *
   * Always empty on the server, and empty in the browser until something is saved — so a component
   * reading it during a render gets the same answer the server gave, which is what hydration requires.
   * The wishlist page still reads it only after the first render, because the *server's* answer is
   * "empty" for everyone and the page would otherwise grow nodes Angular never sent.
   */
  readonly entries: Signal<readonly WishlistEntry[]> = this.saved.asReadonly();

  /** How many products are saved, for the header count (GLB-04). */
  readonly count: Signal<number> = computed(() => this.saved().length);

  /** The saved slugs, newest first — what `/api/wishlist` is asked about. */
  readonly slugs: Signal<readonly string[]> = computed(() => this.saved().map((entry) => entry.slug));

  /** Whether one product is saved. Always false on the server. */
  has(slug: string): boolean {
    return this.saved().some((entry) => entry.slug === slug);
  }

  /** What is stored for one product, or null. */
  entry(slug: string): WishlistEntry | null {
    return this.saved().find((candidate) => candidate.slug === slug) ?? null;
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
   * of persistence; only a write that returns `true` is. Re-writing an unchanged list is one cheap
   * `setItem` and it is the only way to find out whether storage is working now.
   */
  save(save: WishlistSave | string): boolean {
    return this.persist(this.withEntry(normalise(save)));
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
  toggle(save: WishlistSave | string): WishlistOutcome {
    const input = normalise(save);
    const saving = !this.has(input.slug);
    const next = saving ? this.withEntry(input) : this.without(input.slug);

    if (!this.persist(next)) {
      return 'unavailable';
    }

    return saving ? 'saved' : 'removed';
  }

  /**
   * Takes one product out of the wishlist (WISH-01: remove).
   *
   * `false` means storage refused the write and **the item is still saved** — the caller must say so
   * rather than drop the row from the page, which would be the same lie as a filled heart over a
   * refused save, in the other direction. Removing something that is not there is a successful no-op:
   * the end state the caller asked for is the end state, and a second tap on Remove must not read as a
   * storage failure.
   */
  remove(slug: string): boolean {
    return this.persist(this.without(slug));
  }

  /**
   * Empties the wishlist (WISH-01: clear).
   *
   * An empty list is **written**, not `removeItem`'d, for one reason: a write is the only thing that
   * finds out whether storage is working, and this is a caller that is about to tell the shopper their
   * wishlist is empty. `removeItem` on a browser that refuses site data succeeds at removing nothing
   * and would have reported success.
   */
  clear(): boolean {
    return this.persist([]);
  }

  /** This browser's list with one entry added or merged in, newest first. */
  private withEntry(input: WishlistSave): readonly WishlistEntry[] {
    const current = this.saved();
    const existing = current.find((entry) => entry.slug === input.slug);

    const merged: WishlistEntry = {
      slug: input.slug,
      // `'name' in input` and not `input.name ?? existing.name`: see {@link WishlistSave}. An explicit
      // null clears the label; an absent field leaves whatever is stored alone. `??` cannot tell those
      // two apart, and that is precisely how W1-12 cleared every category image.
      name: 'name' in input ? (input.name ?? null) : (existing?.name ?? null),
      colourId: 'colourId' in input ? (input.colourId ?? null) : (existing?.colourId ?? null),
      colourName: 'colourName' in input ? (input.colourName ?? null) : (existing?.colourName ?? null),
      // The original save time survives a re-save, so re-tapping a saved heart does not reorder the
      // shopper's list under them.
      addedAt: existing?.addedAt ?? Date.now(),
    };

    return existing
      ? current.map((entry) => (entry.slug === input.slug ? merged : entry))
      : [merged, ...current];
  }

  private without(slug: string): readonly WishlistEntry[] {
    return this.saved().filter((entry) => entry.slug !== slug);
  }

  /**
   * The one place that writes. Storage first, signal second, and the signal is not touched unless the
   * write reported success — so nothing in this application can claim a product is saved when it is
   * not.
   */
  private persist(next: readonly WishlistEntry[]): boolean {
    const payload = JSON.stringify({ v: STORAGE_VERSION, items: next });
    if (!this.storage.write(STORAGE_KEY, payload)) {
      return false;
    }

    this.saved.set(next);
    return true;
  }

  /**
   * Lesson L1: this is data a browser hands us, not data we wrote this run. A value another tab,
   * an older build or a curious visitor left behind must not break a product grid, so anything
   * that is not readable is read as an empty wishlist.
   *
   * **Version 1 is read, not discarded.** Until this task the stored value was a bare array of slugs,
   * and shoppers have been tapping that heart since W2-04. Throwing those away on the day the wishlist
   * page shipped would have emptied every existing shopper's wishlist — so a `string[]` is read as
   * entries with no label and no colourway, which is exactly what was known about them, and the first
   * write in this browser upgrades the stored shape. The names fill in by themselves: the page asks the
   * API about the slugs and shows live cards regardless of what is stored.
   *
   * A v1 list has no save times either. `addedAt: 0` keeps the stored order (v1's array order, which
   * was insertion order from `Set`) rather than inventing timestamps that would reorder it.
   */
  private read(): readonly WishlistEntry[] {
    const raw = this.storage.read(STORAGE_KEY);
    if (!raw) {
      return [];
    }

    try {
      const parsed: unknown = JSON.parse(raw);

      if (Array.isArray(parsed)) {
        return parsed
          .filter((value): value is string => typeof value === 'string' && value.trim().length > 0)
          .map((slug) => ({ slug, name: null, colourId: null, colourName: null, addedAt: 0 }));
      }

      if (typeof parsed !== 'object' || parsed === null) {
        return [];
      }

      const items = (parsed as { items?: unknown }).items;
      if (!Array.isArray(items)) {
        return [];
      }

      const seen = new Set<string>();
      const entries: WishlistEntry[] = [];

      for (const item of items) {
        const entry = toEntry(item);

        // A duplicated slug in storage is one entry, not two: the count the header shows and the
        // "already saved" heart both have to agree with "one entry per product" (doc 11 §553).
        if (entry && !seen.has(entry.slug)) {
          seen.add(entry.slug);
          entries.push(entry);
        }
      }

      return entries;
    } catch {
      return [];
    }
  }
}

/** A bare slug is the same as `{ slug }`: callers that know nothing else stay a one-liner. */
function normalise(save: WishlistSave | string): WishlistSave {
  return typeof save === 'string' ? { slug: save } : save;
}

/**
 * One stored item as an entry, or null when it is not one.
 *
 * Only the slug is required — everything else is a label whose absence costs nothing — but a junk type
 * in one of them must not reach a template, so each is read defensively and anything unusable becomes
 * null rather than failing the whole entry. A fresh object is built rather than the parsed one narrowed
 * in place, so nothing a newer build of the site (or a curious visitor) left in the stored shape travels
 * on into a signal.
 */
function toEntry(value: unknown): WishlistEntry | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const stored = value as Record<string, unknown>;
  const slug = stored['slug'];

  if (typeof slug !== 'string' || slug.trim().length === 0) {
    return null;
  }

  return {
    slug,
    name: typeof stored['name'] === 'string' ? stored['name'] : null,
    colourId: typeof stored['colourId'] === 'number' && Number.isFinite(stored['colourId'])
      ? stored['colourId']
      : null,
    colourName: typeof stored['colourName'] === 'string' ? stored['colourName'] : null,
    addedAt: typeof stored['addedAt'] === 'number' && Number.isFinite(stored['addedAt'])
      ? stored['addedAt']
      : 0,
  };
}
