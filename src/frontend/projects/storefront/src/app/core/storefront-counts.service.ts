import {
  Injectable,
  Injector,
  Signal,
  afterNextRender,
  effect,
  inject,
  signal,
} from '@angular/core';

import { CartStore } from './cart/cart.store';
import { LocalWishlist } from './wishlist/local-wishlist.service';

/**
 * The wishlist and bag counts shown on the header icons (GLB-04).
 *
 * The wishlist number comes from {@link LocalWishlist}, which **is** the wishlist (W2-08, WISH-01):
 * doc 02's WISH-01 line and doc 08 §1.7 both keep a guest's saved products in the browser, so there is
 * no server-side count to prefer over this one. A heart on a product card that saved something and
 * left the header reading "Wishlist, 0 items" was telling the shopper their tap had not worked.
 * W3-01 adds the account list and the merge; this number will then be whichever of the two is live.
 *
 * It is taken up only after the first render. The count lives in this browser and the server
 * cannot know it, so reading it any earlier would grow the header during hydration — a badge the
 * server never sent — which is exactly what hydration refuses.
 *
 * The bag number comes from {@link CartStore} (W2-09, CART-01/02). It is **not** read during a server
 * render on an ordinary page: the bag is per-visitor, so a count in the markup would make every page
 * personal and uncacheable for the sake of one number. The store is asked for it after the first render
 * instead, exactly as the wishlist is, so the badge appears a moment after the shell. The one page that
 * does render bag contents on the server is `/bag` itself, which answers `no-store` and `noindex`.
 */
@Injectable({ providedIn: 'root' })
export class StorefrontCounts {
  private readonly wishlistCount = signal(0);
  private readonly bagCount = signal(0);

  constructor() {
    const wishlist = inject(LocalWishlist);
    const cart = inject(CartStore);
    const injector = inject(Injector);

    afterNextRender(() => {
      effect(() => this.setWishlist(wishlist.count()), { injector });

      // The read is started here rather than in the store, so nothing fetches the bag during a server
      // render of a page that is not the bag page. `ensureLoaded` is a no-op when the bag page has
      // already read it.
      cart.ensureLoaded();
      effect(() => this.setBag(cart.itemCount()), { injector });
    });
  }

  readonly wishlist: Signal<number> = this.wishlistCount.asReadonly();
  readonly bag: Signal<number> = this.bagCount.asReadonly();

  setWishlist(count: number): void {
    this.wishlistCount.set(normalise(count));
  }

  setBag(count: number): void {
    this.bagCount.set(normalise(count));
  }
}

/** A count is never negative and never fractional, whatever the caller passes. */
function normalise(count: number): number {
  return Number.isFinite(count) ? Math.max(0, Math.trunc(count)) : 0;
}
