import { HttpInterceptorFn } from '@angular/common/http';
import { REQUEST, inject } from '@angular/core';

import { CartApi } from './cart-api';

/** The cookie that holds `sales.Carts.Token`. Must match `CartCookie.Name` in the API. */
const CART_COOKIE = 'sd_cart';

/**
 * During server-side rendering, copies the visitor's bag cookie onto the request the SSR process
 * makes to the API.
 *
 * **Why this is needed at all.** In the browser, `/api/cart` is same-origin behind the reverse proxy
 * and the cookie travels by itself. On the server, the request is made by the Node process to the
 * internal API address: it has no cookie jar, so without this the server would render every bag as
 * empty and the page would visibly fill in after hydration — which is exactly the thing SSR exists
 * to avoid, and which would also have been invisible to any test that only drove the browser.
 *
 * **What it deliberately does not do.**
 * - It forwards **only `sd_cart`**, never the whole `Cookie` header. The same browser may be carrying
 *   `sd_admin_auth`; relaying a staff session from the SSR process to the API on a shopper's page
 *   render would hand the storefront an authenticated identity it has no business holding.
 * - It applies **only to `/api/cart`** requests. No other call the storefront makes is per-visitor.
 * - It never writes a cookie back. A read never creates a cart (doc 08 §3.5: the row is created on
 *   the first Add to Bag), so the API issues no `Set-Cookie` on a render, and there is nothing for the
 *   SSR response to relay. Every mutation happens in the browser, where the cookie round-trip is the
 *   browser's own.
 *
 * In the browser `REQUEST` is null and this is a no-op.
 */
export const cartCookieInterceptor: HttpInterceptorFn = (request, next) => {
  const incoming = inject(REQUEST, { optional: true });
  if (!incoming || !isCartRequest(request.url)) {
    return next(request);
  }

  const token = readCookie(incoming.headers.get('cookie'), CART_COOKIE);
  if (!token) {
    return next(request);
  }

  return next(request.clone({ setHeaders: { Cookie: `${CART_COOKIE}=${token}` } }));
};

/**
 * Whether this is a bag call. The URL may already be absolute — `apiBaseUrlInterceptor` runs in the
 * same chain and rewrites `/api/...` to the internal address during SSR — so the path is matched
 * wherever it sits in the string rather than only at the start.
 */
function isCartRequest(url: string): boolean {
  return url.startsWith(CartApi.url) || url.includes(`${CartApi.url}`);
}

/**
 * One cookie's value out of a `Cookie` header.
 *
 * Lesson L1 applies: this is a string a browser handed us. A header with a stray `=`, a repeated
 * name, trailing semicolons or no value at all must read as "no cookie" rather than throw during a
 * render — a 500 on the bag page would be the result. The value is passed through untouched and the
 * API refuses anything that is not a canonical GUID.
 */
function readCookie(header: string | null, name: string): string | null {
  if (!header) {
    return null;
  }

  for (const part of header.split(';')) {
    const separator = part.indexOf('=');
    if (separator < 0) {
      continue;
    }

    if (part.slice(0, separator).trim() === name) {
      const value = part.slice(separator + 1).trim();
      return value.length > 0 ? value : null;
    }
  }

  return null;
}
