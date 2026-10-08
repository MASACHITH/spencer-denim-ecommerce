import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  CancelOrderDto,
  CheckoutSessionDto,
  CheckoutTotalsDto,
  OrderConfirmationDto,
  OrderPaymentProofDto,
  PlaceOrderRequest,
  SlipUploadedDto,
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

  /**
   * The bank reference the customer is quoting for their transfer (PAY-01, doc 11 P7 step 3).
   *
   * An empty box clears it, which a customer who mistyped it needs — so `null` is sent rather than `''`,
   * because the API reads blank as "clear" and a missing field as "leave alone".
   */
  setPaymentReference(publicId: string, reference: string | null): Observable<OrderPaymentProofDto> {
    return this.http.put<OrderPaymentProofDto>(
      `${CheckoutApi.orderUrl}/${encodeURIComponent(publicId)}/payment-reference`,
      { reference: reference === null || reference.trim().length === 0 ? null : reference.trim() },
    );
  }

  /**
   * Uploads the payment slip, replacing whatever the order already holds (doc 11 P7 step 3).
   *
   * **No antiforgery token and no `Content-Type` header.** There is no customer session for a forged
   * request to ride on (ACC-01 is W3-01) and the order's token is the only credential there is; and the
   * browser has to set the multipart boundary itself, so setting the type by hand here would produce a
   * body the server cannot parse.
   */
  uploadPaymentSlip(publicId: string, file: File): Observable<SlipUploadedDto> {
    const form = new FormData();
    form.append('file', file, file.name);

    return this.http.post<SlipUploadedDto>(
      `${CheckoutApi.orderUrl}/${encodeURIComponent(publicId)}/payment-slip`,
      form,
    );
  }

  /**
   * Cancels the shopper's own unpaid order (doc 11 P10 flow A).
   *
   * **No body and no token.** Doc 11 P10 asks the staff member for a reason and the customer only to
   * press the button, and the order's own link is the only credential there is — there is no customer
   * session for an antiforgery token to protect, exactly as `setPaymentReference` and the slip upload
   * already reason.
   *
   * A 404 means "this order can no longer be cancelled", which is deliberately the same answer as "no
   * such order": the token never expires and is a guest's only credential, so a stranger holding a
   * guessed GUID must not learn that it names a real order.
   */
  cancelOrder(publicId: string): Observable<CancelOrderDto> {
    return this.http.post<CancelOrderDto>(
      `${CheckoutApi.orderUrl}/${encodeURIComponent(publicId)}/cancel`,
      null,
    );
  }

  /** Where the customer's own slip downloads from. A GET the browser follows. */
  paymentSlipUrl(publicId: string): string {
    return `${CheckoutApi.orderUrl}/${encodeURIComponent(publicId)}/payment-slip`;
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
