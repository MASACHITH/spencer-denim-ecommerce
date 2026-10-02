import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { CartDto } from './cart.model';
import { ValidationProblemDetails } from '../catalog/catalog.model';

/**
 * The bag API (`/api/cart`). One place that knows the endpoint names.
 *
 * **Nothing here sends or reads a cart token.** The bag is identified by the `sd_cart` cookie, which
 * is `HttpOnly` — script cannot read it and does not need to. In the browser these are same-origin
 * requests behind the reverse proxy, so the cookie travels by itself. During server-side rendering
 * `cartCookieInterceptor` copies the visitor's cookie onto the outgoing request, because the SSR
 * process is a third party to the browser's jar.
 */
@Injectable({ providedIn: 'root' })
export class CartApi {
  static readonly url = '/api/cart';

  private readonly http = inject(HttpClient);

  /** The bag as it stands. Never creates one. */
  get(): Observable<CartDto> {
    return this.http.get<CartDto>(CartApi.url);
  }

  /** Add to Bag (PDP-03). Adding a variant already in the bag adds to its quantity, capped at 10. */
  add(variantId: number, quantity: number): Observable<CartDto> {
    return this.http.post<CartDto>(`${CartApi.url}/items`, { variantId, quantity });
  }

  /** Set a line's quantity to an exact number, 1–10. Zero is a removal, not a quantity. */
  setQuantity(variantId: number, quantity: number): Observable<CartDto> {
    return this.http.patch<CartDto>(`${CartApi.url}/items/${variantId}`, { quantity });
  }

  /** Take a line out. Idempotent: removing a line that is gone answers the bag. */
  remove(variantId: number): Observable<CartDto> {
    return this.http.delete<CartDto>(`${CartApi.url}/items/${variantId}`);
  }
}

/**
 * How a failed bag request has to be treated, which is not the same as a status code.
 *
 * - `soldOut` — the API answered **409**. The request was fine; there is none left. The shopper needs
 *   to know that specifically, not "something went wrong".
 * - `rejected` — a **400**. The API's own message names what it refused and is safe to show.
 * - `offline` — status 0: the browser never reached the shop.
 * - `failed` — anything else (500, a proxy's HTML error page).
 */
export type CartFailure = 'soldOut' | 'rejected' | 'offline' | 'failed';

/** Classifies a failed bag request. */
export function cartFailure(error: unknown): CartFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return 'failed';
  }

  if (error.status === 409) {
    return 'soldOut';
  }

  if (error.status === 400) {
    return 'rejected';
  }

  return error.status === 0 ? 'offline' : 'failed';
}

/**
 * A sentence for the shopper.
 *
 * A 400's message comes from the API, because the API is the thing that knows which field it refused
 * and why — the storefront's own validation should have caught it first, so reaching here at all
 * means the two disagree and the server's account is the one that matters. Everything else gets a
 * fixed sentence: no status codes and no stack traces in front of a shopper.
 */
export function cartErrorMessage(error: unknown): string {
  switch (cartFailure(error)) {
    case 'soldOut':
      return 'That size has just sold out, so nothing was added to your bag.';
    case 'rejected':
      return (
        firstValidationMessage((error as HttpErrorResponse).error) ??
        'We could not make that change to your bag.'
      );
    case 'offline':
      return 'We could not reach the store. Check your connection and try again.';
    default:
      return 'We could not update your bag just now. Please try again in a moment.';
  }
}

function firstValidationMessage(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const errors = (body as ValidationProblemDetails).errors;
  if (!errors) {
    return null;
  }

  for (const messages of Object.values(errors)) {
    const message = messages?.find(
      (candidate) => typeof candidate === 'string' && candidate.trim(),
    );
    if (message) {
      return message;
    }
  }

  return null;
}
