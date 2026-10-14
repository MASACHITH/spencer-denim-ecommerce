import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AccountMessageDto,
  CustomerAddressDto,
  CustomerAddressEdit,
  CustomerOrderPageDto,
  CustomerProfileDto,
  CustomerProfileEdit,
  CustomerSessionDto,
  EmailConfirmedDto,
} from './account.model';
import { ValidationProblemDetails } from '../catalog/catalog.model';
import { fieldErrorsOf } from '../checkout/checkout-api';

/**
 * The customer account API (`/api/account`). One place that knows the endpoint names (ACC-01, ACC-02).
 *
 * **Nothing here sends or reads a session token.** The customer is identified by `sd_customer_auth`,
 * which is `HttpOnly`; in the browser these are same-origin requests behind the reverse proxy and the
 * cookie travels by itself. There is deliberately no SSR counterpart: `cartCookieInterceptor` forwards
 * `sd_cart` and nothing else, so **every one of these calls made during a server render would answer
 * 401** — which is why `AccountStore` never makes one there and the account pages render only their
 * shell on the server. The interceptor's own docblock says why it forwards one cookie and never the
 * whole `Cookie` header; nothing in this task changes that.
 *
 * **One `/api/account/orders` and no order-detail route.** The server has no customer order-detail
 * endpoint on purpose: `GET /api/orders/{token}` already answers the whole order for whoever holds its
 * token, and the history hands each row its own token, so the detail page is reached by the existing
 * `/order/{token}` route. A second projection would be a second answer to "what does this order say".
 *
 * **The districts for the address and profile forms are not here.** They come from `GET /api/checkout`,
 * which answers `districts` whether or not the visitor has a bag (`GetCheckoutQueryHandler`: "three
 * things that need the reference data whether or not there is a bag"), through the existing
 * {@link CheckoutApi}. Adding a second districts endpoint — or a second client for one — would be a
 * second list of the shop's 25 districts to keep in step with `ref.Districts.IsActive`, which an admin
 * edits; and the one the checkout form already uses is the one whose contents checkout will actually
 * quote for. The cost is that these two pages read a payload wider than they need, which is a wasted
 * field or two and not a wasted request.
 */
@Injectable({ providedIn: 'root' })
export class AccountApi {
  static readonly url = '/api/account';

  private readonly http = inject(HttpClient);

  /**
   * Creates an account (doc 11 P11 step A).
   *
   * **202 for every address**, free, already a customer's or a staff member's alike (doc 08 §3.1 DB10),
   * and **no session**: a response that sometimes carried one would itself say whether the address was
   * free. The caller shows `message` and nothing of its own.
   */
  register(body: {
    readonly firstName: string;
    readonly lastName: string | null;
    readonly email: string;
    readonly password: string;
    readonly phone: string | null;
  }): Observable<AccountMessageDto> {
    return this.http.post<AccountMessageDto>(`${AccountApi.url}/register`, body);
  }

  /** Signs in and issues the cookie. The server merges the guest bag in the same request. */
  signIn(email: string, password: string): Observable<CustomerSessionDto> {
    return this.http.post<CustomerSessionDto>(`${AccountApi.url}/login`, { email, password });
  }

  /** Removes the cookie. 204, and the browser's guest bag is **not** restored (doc 11 P11 step B.7). */
  signOut(): Observable<void> {
    return this.http.post<void>(`${AccountApi.url}/logout`, null);
  }

  /**
   * Who is signed in, read fresh from the database rather than from the cookie's claims — so a customer
   * who has just confirmed in another tab is not told to confirm again. 401 when there is no session.
   */
  me(): Observable<CustomerSessionDto> {
    return this.http.get<CustomerSessionDto>(`${AccountApi.url}/me`);
  }

  /**
   * Redeems a confirmation link and links the guest orders placed with that address (doc 08 §3.1 D5).
   *
   * **The token goes in the body although it arrived in the page's URL.** A token in a request URL lands
   * in the API's access log and in any proxy's; the page's own address is the browser's business. The
   * page strips it from the address bar after reading it, so it does not sit in history either.
   */
  confirmEmail(token: string): Observable<EmailConfirmedDto> {
    return this.http.post<EmailConfirmedDto>(`${AccountApi.url}/confirm-email`, { token });
  }

  /** Mints a fresh link, which invalidates any already in the inbox. Needs a session. */
  resendConfirmation(): Observable<AccountMessageDto> {
    return this.http.post<AccountMessageDto>(`${AccountApi.url}/resend-confirmation`, null);
  }

  /** One sentence and 202 for every address (doc 08 §3.1 DB10). */
  forgotPassword(email: string): Observable<AccountMessageDto> {
    return this.http.post<AccountMessageDto>(`${AccountApi.url}/forgot-password`, { email });
  }

  /**
   * Spends a reset link and sets the password. **No session is issued** — the link was opened by whatever
   * browser read the email — so the page asks the shopper to sign in.
   *
   * A refused password does not spend the token, so the same link can be used again with a longer one.
   */
  resetPassword(token: string, password: string): Observable<AccountMessageDto> {
    return this.http.post<AccountMessageDto>(`${AccountApi.url}/reset-password`, { token, password });
  }

  /**
   * Changes the password and reissues **this** browser's cookie, signing every other browser out.
   *
   * A wrong current password counts towards the same lockout as a wrong sign-in (#146), so this can
   * answer 423 — which is why {@link accountFailure} classifies the lock for both routes.
   */
  changePassword(currentPassword: string, newPassword: string): Observable<CustomerSessionDto> {
    return this.http.post<CustomerSessionDto>(`${AccountApi.url}/change-password`, {
      currentPassword,
      newPassword,
    });
  }

  profile(): Observable<CustomerProfileDto> {
    return this.http.get<CustomerProfileDto>(`${AccountApi.url}/profile`);
  }

  /**
   * Saves the profile.
   *
   * A district the shop no longer serves is **dropped rather than refused** by the server, and the answer
   * shows it unset — refusing the whole save would lose a name change for a reason that has nothing to do
   * with the name. The page re-renders from the answer, so it never shows a district the server did not
   * keep.
   */
  saveProfile(edit: CustomerProfileEdit): Observable<CustomerProfileDto> {
    return this.http.put<CustomerProfileDto>(`${AccountApi.url}/profile`, edit);
  }

  addresses(): Observable<readonly CustomerAddressDto[]> {
    return this.http.get<readonly CustomerAddressDto[]>(`${AccountApi.url}/addresses`);
  }

  /**
   * Every write below answers the **whole book**, and the page re-renders from it rather than patching
   * its own list.
   *
   * That is the server's decision and it is load-bearing: setting a default clears the previous one, and
   * deleting the default promotes another, so a client that merged one returned row into a local list
   * would show two defaults or none. One answer, one render.
   */
  addAddress(edit: CustomerAddressEdit): Observable<readonly CustomerAddressDto[]> {
    return this.http.post<readonly CustomerAddressDto[]>(`${AccountApi.url}/addresses`, edit);
  }

  /** @see addAddress for why this answers the whole book. */
  updateAddress(
    addressId: number,
    edit: CustomerAddressEdit,
  ): Observable<readonly CustomerAddressDto[]> {
    return this.http.put<readonly CustomerAddressDto[]>(
      `${AccountApi.url}/addresses/${addressId}`,
      edit,
    );
  }

  /** @see addAddress for why this answers the whole book. */
  deleteAddress(addressId: number): Observable<readonly CustomerAddressDto[]> {
    return this.http.delete<readonly CustomerAddressDto[]>(
      `${AccountApi.url}/addresses/${addressId}`,
    );
  }

  /** @see addAddress for why this answers the whole book. */
  setDefaultAddress(addressId: number): Observable<readonly CustomerAddressDto[]> {
    return this.http.post<readonly CustomerAddressDto[]>(
      `${AccountApi.url}/addresses/${addressId}/default`,
      null,
    );
  }

  /**
   * One page of the customer's own orders, newest first, with their tracking.
   *
   * **`pageSize` is not sent.** The server's default is ten and it echoes the size back, so the page
   * prints what it was given; sending a number from here would be a second copy of
   * `GetCustomerOrdersQueryHandler.DefaultPageSize` and the two would drift. The server clamps anything
   * outside 1–50 back to ten, so even a hand-typed `?pageSize=` in the address bar cannot make the page
   * disagree with itself.
   */
  orders(page: number): Observable<CustomerOrderPageDto> {
    const params = new HttpParams().set('page', Math.max(1, Math.trunc(page)));

    return this.http.get<CustomerOrderPageDto>(`${AccountApi.url}/orders`, { params });
  }
}

/**
 * How a failed account request has to be treated, which is not the same as a status code.
 *
 * Two statuses carry more than one meaning on this surface, and both distinctions are drawn from the
 * problem's `code` extension rather than from its title — `checkout-api.ts`'s lesson: a title is prose
 * and matching on it breaks silently when someone improves the wording.
 *
 * - `invalid` — **400** with `errors`: the server names the fields it refused and those messages are the
 *   ones to show, because the server is the thing that knows which field it refused.
 * - `invalidLink` — **400** `code: "invalid_link"`: a one-time link that is spent, expired, wrong, or for
 *   an address the account no longer uses. One opaque case for all four, deliberately, so a holder is
 *   never told a token was once real.
 * - `credentials` — **401** `code: "invalid_credentials"`: a wrong password, an unknown address, a staff
 *   address or a deactivated account. One refusal, and each costs the same password-hash work.
 * - `signedOut` — **401** with no such code: the session is gone or was never there. Every route except
 *   the five public ones answers this, and it means "sign in again", not "you got it wrong".
 * - `lockedOut` — **423** `code: "locked_out"`, from sign-in and from change-password alike.
 * - `rateLimited` — **429** `code: "rate_limited"`: too many attempts from this address.
 * - `addressLimit` — **409** `code: "address_limit_reached"`, carrying the server's own `limit`.
 * - `notFound` — **404** `code: "address_not_found"`: no such address, or not this customer's. One
 *   refusal for both, because the second tells a caller the id exists.
 * - `offline` — status 0: the browser never reached the shop.
 * - `failed` — anything else.
 */
export type AccountFailure =
  | 'invalid'
  | 'invalidLink'
  | 'credentials'
  | 'signedOut'
  | 'lockedOut'
  | 'rateLimited'
  | 'addressLimit'
  | 'notFound'
  | 'offline'
  | 'failed';

/** The extensions these problems carry, as RFC 9457 members beside `title` and `detail`. */
interface AccountProblem extends ValidationProblemDetails {
  /** `invalid_link`, `invalid_credentials`, `locked_out`, `rate_limited`, `address_limit_reached`… */
  readonly code?: string;

  /** Sent with `locked_out`: an ISO-8601 instant, round-tripped with `"o"`. */
  readonly lockoutEndUtc?: string;

  /** Sent with `address_limit_reached`: doc 08 §3.1's app cap, from the server rather than from here. */
  readonly limit?: number;
}

/** Classifies a failed account request. */
export function accountFailure(error: unknown): AccountFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return 'failed';
  }

  const body = problemBody(error);

  switch (error.status) {
    case 0:
      return 'offline';

    case 400:
      // `invalid_link` before the field check, because a spent link is not a field a shopper can fix and
      // must not be rendered beside an input. A validation problem carries `errors` and no `code`.
      return body?.code === 'invalid_link' ? 'invalidLink' : 'invalid';

    case 401:
      return body?.code === 'invalid_credentials' ? 'credentials' : 'signedOut';

    case 404:
      return 'notFound';

    case 409:
      return 'addressLimit';

    case 423:
      return 'lockedOut';

    case 429:
      return 'rateLimited';

    default:
      return 'failed';
  }
}

/**
 * Field messages from a 400, keyed by the field name the API used.
 *
 * **Borrowed from `checkout-api.ts` rather than copied.** It is a generic RFC 9457 reader that happens to
 * live next to checkout because checkout needed it first: it reads `errors`, takes the first non-blank
 * message per field, and keys it by the API's own camelCase name — which is the same key these forms'
 * controls use, so each message lands next to its own input. A second copy would be a second answer to
 * "what did the server refuse", and the import is the cheaper of the two. Moving it to a
 * `core/http/problem-details.ts` is the right home and is a separate change: it would touch
 * `checkout-page.ts`, `checkout-api.ts` and their specs, which this task has no reason to disturb.
 */
export { fieldErrorsOf };

/** When a lockout clears, or null. Only `lockedOut` carries one. */
export function lockoutEndOf(error: unknown): Date | null {
  const stamp = problemBody(error)?.lockoutEndUtc;

  if (typeof stamp !== 'string') {
    return null;
  }

  const parsed = new Date(stamp);

  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * The address limit the server refused at, or null.
 *
 * Read from the problem rather than from `SAVED_ADDRESS_LIMIT`, so the sentence a shopper sees names the
 * number the server actually enforced even if the two ever disagree.
 */
export function addressLimitOf(error: unknown): number | null {
  const limit = problemBody(error)?.limit;

  return typeof limit === 'number' ? limit : null;
}

/**
 * A sentence for the shopper.
 *
 * **Three of these come from the server and are passed through.** A 400's field message, a spent link's
 * refusal and a lockout's detail are all written on the server, where the reason is known, and the server
 * has already been careful about two things this function must not undo: it never reveals whether an
 * address has an account, and it never promises an email. Everything else gets a fixed sentence here: no
 * status codes and no stack traces in front of a shopper.
 *
 * `signedOut` is the one that reads oddly out of context and is right in it: a shopper whose session
 * expired while they were on the addresses page has done nothing wrong, so it says what happened and what
 * to do rather than refusing them.
 */
export function accountErrorMessage(error: unknown): string {
  const failure = accountFailure(error);
  const body = problemBody(error);

  switch (failure) {
    case 'invalid': {
      const first = fieldErrorsOf(error).values().next();
      return first.done ? 'Please check the details above and try again.' : first.value;
    }

    case 'invalidLink':
    case 'lockedOut':
    case 'rateLimited':
    case 'addressLimit':
      // The server's own `detail`. These four are the cases where it carries the reason — a spent link,
      // a lock that clears by itself, a budget that refills, the cap and what to do about it — and a
      // sentence written here instead would be a second, vaguer account of the same refusal.
      return body?.detail ?? FALLBACKS[failure];

    default:
      return FALLBACKS[failure];
  }
}

/**
 * The sentence for each failure the server does not write one for.
 *
 * `credentials` is the one worth reading twice: it says *neither* which half was wrong *nor* whether the
 * address is known, because the server answers one 401 for a wrong password, an unknown address, a staff
 * address and a deactivated account alike (doc 08 §3.1 DB10). "We don't recognise that email" would undo
 * the whole property at the last step.
 */
const FALLBACKS: Record<AccountFailure, string> = {
  invalid: 'Please check the details above and try again.',
  invalidLink: 'That link cannot be used. Ask for a new one and use the newest message.',
  credentials: 'That email address and password do not match an account.',
  signedOut: 'You are not signed in any more. Sign in again to carry on.',
  lockedOut: 'Too many attempts. Try again in a few minutes — the lock clears by itself.',
  rateLimited: 'Too many attempts from here. Please try again shortly.',
  addressLimit: 'You cannot save another address. Remove one you no longer use and try again.',
  notFound: 'We could not find that. It may already have been removed.',
  offline: 'We could not reach the store. Check your connection and try again.',
  failed: 'Something went wrong at our end. Please try again in a moment.',
};

function problemBody(error: unknown): AccountProblem | null {
  if (!(error instanceof HttpErrorResponse)) {
    return null;
  }

  const body = error.error;

  return typeof body === 'object' && body !== null ? (body as AccountProblem) : null;
}
