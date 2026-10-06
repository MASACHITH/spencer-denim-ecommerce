import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { ProductCardDto } from '../catalog/catalog.model';

/**
 * One saved product as the API answers for it.
 *
 * Mirrors `SpencerDenim.Application.Sales.Wishlist.Contracts.WishlistEntryDto`. `product` is **null**
 * when the shop no longer offers it — unpublished, soft-deleted, or in a category the client has
 * deactivated. That is an answer, not an error: doc 11 §575 says a wishlist item removed from sale "is
 * shown as unavailable; can be removed".
 */
export interface WishlistEntryResponse {
  readonly slug: string;
  readonly product: ProductCardDto | null;
}

/** The whole answer. One entry per slug asked about, in the order asked. */
export interface WishlistResponse {
  readonly items: readonly WishlistEntryResponse[];
}

/**
 * How many slugs one request may name. Must match
 * `GetWishlistQueryHandler.MaxSlugs`: the server truncates at this number, and the page says so rather
 * than appearing to lose items.
 */
export const WISHLIST_MAX_SLUGS = 200;

/**
 * The wishlist API (`/api/wishlist`). One place that knows the endpoint name.
 *
 * **It sends the shopper's slugs and no identity at all.** The list lives in this browser (doc 08 §1.7),
 * so there is no cookie to forward and nothing for `cartCookieInterceptor` to do — which is why
 * `/api/wishlist` is deliberately *not* in that interceptor's per-visitor list. A `GET` because it reads
 * and changes nothing.
 */
@Injectable({ providedIn: 'root' })
export class WishlistApi {
  static readonly url = '/api/wishlist';

  private readonly http = inject(HttpClient);

  /**
   * The live cards for `slugs`, in the order given.
   *
   * The caller is expected to have trimmed the list to {@link WISHLIST_MAX_SLUGS}; the server truncates
   * anyway, and silently dropping the tail here would hide from the page the one fact it needs in order
   * to tell the shopper.
   */
  get(slugs: readonly string[]): Observable<WishlistResponse> {
    return this.http.get<WishlistResponse>(WishlistApi.url, {
      // `appendAll` with an array gives `?slug=a&slug=b`, which is what the endpoint's `string[]` binds
      // from. A single comma-joined parameter would break on a slug containing a comma — slugs cannot
      // today (CK_Products_Slug), but a contract that depends on that is a contract that breaks quietly
      // when the rule changes.
      params: new HttpParams().appendAll({ slug: [...slugs] }),
    });
  }
}

/**
 * How a failed wishlist request has to be treated.
 *
 * - `offline` — status 0: the browser never reached the shop.
 * - `failed` — anything else. There is no `rejected` here, because there is nothing for the API to
 *   refuse: a junk slug, a duplicate or an over-long list is dropped or truncated rather than 400'd
 *   (see `GetWishlistQueryHandler`), so a 400 from this endpoint would be a bug and not a message to
 *   pass on to a shopper.
 */
export type WishlistFailure = 'offline' | 'failed';

export function wishlistFailure(error: unknown): WishlistFailure {
  return error instanceof HttpErrorResponse && error.status === 0 ? 'offline' : 'failed';
}

/** A sentence for the shopper. No status codes and no stack traces in front of one. */
export function wishlistErrorMessage(error: unknown): string {
  return wishlistFailure(error) === 'offline'
    ? 'We could not reach the store. Check your connection and try again.'
    : 'We could not load your wishlist just now. Please try again in a moment.';
}
