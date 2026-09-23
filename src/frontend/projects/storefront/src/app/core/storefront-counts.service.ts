import {
  Injectable,
  Injector,
  Signal,
  afterNextRender,
  effect,
  inject,
  signal,
} from '@angular/core';

import { LocalWishlist } from './wishlist/local-wishlist.service';

/**
 * The wishlist and bag counts shown on the header icons (GLB-04).
 *
 * The wishlist number comes from {@link LocalWishlist} until the customer's saved list exists
 * (W2-08): a heart on a product card that saved something and left the header reading
 * "Wishlist, 0 items" was telling the shopper their tap had not worked.
 *
 * It is taken up only after the first render. The count lives in this browser and the server
 * cannot know it, so reading it any earlier would grow the header during hydration — a badge the
 * server never sent — which is exactly what hydration refuses.
 *
 * TODO(BAG-01): the bag still has no source and stays at zero, which is also its empty state.
 */
@Injectable({ providedIn: 'root' })
export class StorefrontCounts {
  private readonly wishlistCount = signal(0);
  private readonly bagCount = signal(0);

  constructor() {
    const wishlist = inject(LocalWishlist);
    const injector = inject(Injector);

    afterNextRender(() => {
      effect(() => this.setWishlist(wishlist.count()), { injector });
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
