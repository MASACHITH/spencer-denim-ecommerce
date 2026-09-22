import { Injectable, Signal, signal } from '@angular/core';

/**
 * The wishlist and bag counts shown on the header icons (GLB-04).
 *
 * TODO(WISH-01 / BAG-01): the wishlist and bag features own these numbers. Until they exist the
 * counts stay at zero and no badge is drawn, which is also the correct empty state afterwards.
 */
@Injectable({ providedIn: 'root' })
export class StorefrontCounts {
  private readonly wishlistCount = signal(0);
  private readonly bagCount = signal(0);

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
