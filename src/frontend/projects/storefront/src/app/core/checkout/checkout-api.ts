import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  CheckoutSessionDto,
  CheckoutTotalsDto,
  OrderConfirmationDto,
  PlaceOrderRequest,
} from './checkout.model';
import { ValidationProblemDetails } from '../catalog/catalog.model';

/**
 * The checkout API (`/api/checkout`, `/api/orders/{publicId}`). One place that knows the endpoint names.
 *
 * **Nothing here sends or reads a cart token.** The visitor is identified by the `sd_cart` cookie, which
 * is `HttpOnly`; in the browser these are same-origin requests behind the reverse proxy so the cookie
 * travels by itself, and during server-side rendering `cartCookieInterceptor` copies it onto the outgoing
 * request, because the SSR process is a third party to the browser's jar.
 */
@Injectable({ providedIn: 'root' })
export class CheckoutApi {
  static readonly url = '/api/checkout';
  static readonly orderUrl = '/api/orders';

  private readonly http = inject(HttpClient);

  /**
   * The checkout page for this visitor.
   *
   * Both ids are optional, and omitting them is not the same as sending zero: with no district there is
   * no zone, so there is no delivery fee and no grand total. The server declines to invent one.
   */
  get(districtId: number | null, deliveryMethodId: number | null): Observable<CheckoutSessionDto> {
    let params = new HttpParams();

    if (districtId !== null) {
      params = params.set('districtId', districtId);
    }

    if (deliveryMethodId !== null) {
      params = params.set('deliveryMethodId', deliveryMethodId);
    }

    return this.http.get<CheckoutSessionDto>(CheckoutApi.url, { params });
  }

  /** Place the order. Re-sending the same `checkoutKey` returns the first order and creates nothing. */
  place(request: PlaceOrderRequest): Observable<OrderConfirmationDto> {
    return this.http.post<OrderConfirmationDto>(`${CheckoutApi.url}/orders`, request);
  }

  /** One placed order, by the unguessable token in its link (CHK-04). */
  order(publicId: string): Observable<OrderConfirmationDto> {
    return this.http.get<OrderConfirmationDto>(
      `${CheckoutApi.orderUrl}/${encodeURIComponent(publicId)}`,
    );
  }
}

/**
 * How a failed checkout has to be treated, which is not the same as a status code.
 *
 * The three 409s are the ones a shopper can act on, and they need to be told apart because the action
 * differs: adjust the bag, look at a new total, or wait. Collapsing them into "something went wrong"
 * would leave a shopper pressing the same button.
 *
 * - `invalid` — **400**. The API names the fields it refused and those messages are safe to show.
 * - `soldOut` — **409** with `shortLines`. Items sold out mid-checkout; nothing was placed.
 * - `totalChanged` — **409** with `totals`. G-10: the price moved, nothing was placed, show the new one.
 * - `blocked` — **409** with `blockers`. The bag or the shop's state refuses checkout.
 * - `notFound` — **404**, for an order link that names no order.
 * - `offline` — status 0: the browser never reached the shop.
 * - `failed` — anything else.
 */
export type CheckoutFailure =
  | 'invalid'
  | 'soldOut'
  | 'totalChanged'
  | 'blocked'
  | 'notFound'
  | 'offline'
  | 'failed';

/** The extra fields a checkout 409 carries, as `ProblemDetails` extensions. */
interface CheckoutProblem {
  readonly title?: string;
  readonly detail?: string;
  readonly shortLines?: Readonly<Record<string, number>>;
  readonly totals?: CheckoutTotalsDto;
  readonly blockers?: readonly string[];
}

/** Classifies a failed checkout request. */
export function checkoutFailure(error: unknown): CheckoutFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return 'failed';
  }

  if (error.status === 400) {
    return 'invalid';
  }

  if (error.status === 404) {
    return 'notFound';
  }

  if (error.status === 409) {
    // The three 409s are told apart by which extension the body carries, not by the title: the title is
    // prose and would be matched by string comparison, which is exactly the kind of coupling that breaks
    // silently when someone improves the wording.
    const body = problemBody(error);

    if (body?.shortLines) {
      return 'soldOut';
    }

    if (body?.totals) {
      return 'totalChanged';
    }

    return 'blocked';
  }

  return error.status === 0 ? 'offline' : 'failed';
}

/** Per-variant available quantities from a sold-out 409, or null. */
export function shortLinesOf(error: unknown): ReadonlyMap<number, number> | null {
  const body = problemBody(error);

  if (!body?.shortLines) {
    return null;
  }

  const lines = new Map<number, number>();

  for (const [variantId, available] of Object.entries(body.shortLines)) {
    const id = Number(variantId);
    if (Number.isInteger(id) && typeof available === 'number') {
      lines.set(id, available);
    }
  }

  return lines;
}

/** The freshly priced totals from a prices-changed 409, or null. */
export function changedTotalsOf(error: unknown): CheckoutTotalsDto | null {
  return problemBody(error)?.totals ?? null;
}

/** Field errors from a 400, keyed by the field name the API used. */
export function fieldErrorsOf(error: unknown): ReadonlyMap<string, string> {
  const errors = new Map<string, string>();

  if (!(error instanceof HttpErrorResponse)) {
    return errors;
  }

  const body = error.error as ValidationProblemDetails | null;

  if (!body || typeof body !== 'object' || !body.errors) {
    return errors;
  }

  for (const [field, messages] of Object.entries(body.errors)) {
    const message = messages?.find(
      (candidate) => typeof candidate === 'string' && candidate.trim(),
    );

    if (message) {
      // The API's field names are camelCase and nested as `address.line1`, which is the same key the
      // form's controls use, so the page can put each message next to its own input.
      errors.set(field, message);
    }
  }

  return errors;
}

/**
 * A sentence for the shopper.
 *
 * A 400's message comes from the API, because the API is the thing that knows which field it refused —
 * the storefront's own validation should have caught it first, so reaching here means the two disagree
 * and the server's account is the one that matters. Everything else gets a fixed sentence: no status
 * codes and no stack traces in front of a shopper.
 */
export function checkoutErrorMessage(error: unknown): string {
  switch (checkoutFailure(error)) {
    case 'invalid': {
      const first = fieldErrorsOf(error).values().next();
      return first.done ? 'Please check the details above and try again.' : first.value;
    }
    case 'soldOut':
      return (
        'Some of your items sold out while you were checking out. Your order has not been placed and ' +
        'nothing has been charged.'
      );
    case 'totalChanged':
      return (
        'The total has changed since this page was loaded. Check the new total below and place your ' +
        'order again. Nothing has been placed.'
      );
    case 'blocked':
      return problemBody(error)?.detail ?? 'Checkout is not available at the moment.';
    case 'notFound':
      return 'We could not find that order.';
    case 'offline':
      return 'We could not reach the store. Check your connection and try again.';
    default:
      return 'We could not place your order just now. Please try again in a moment.';
  }
}

function problemBody(error: unknown): CheckoutProblem | null {
  if (!(error instanceof HttpErrorResponse)) {
    return null;
  }

  const body = error.error;

  return typeof body === 'object' && body !== null ? (body as CheckoutProblem) : null;
}
