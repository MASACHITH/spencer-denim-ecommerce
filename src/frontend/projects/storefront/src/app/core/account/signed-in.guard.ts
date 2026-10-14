import { isPlatformBrowser } from '@angular/common';
import { PLATFORM_ID, inject } from '@angular/core';
import {
  ActivatedRouteSnapshot,
  CanActivateFn,
  GuardResult,
  Router,
  RouterStateSnapshot,
} from '@angular/router';
import { Observable, map } from 'rxjs';

import { AccountStore } from './account.store';

/**
 * The routes that need a signed-in customer, and where a signed-out visitor goes instead.
 *
 * ## `canActivate`, not `canMatch`, and that is not a preference
 *
 * A `canMatch` guard that answers no lets the router **carry on down the table**, which on this
 * storefront is a specific and wrong destination: `/account/orders` would fall past the account routes
 * to `:department/:category`, whose guard would ask the catalogue about a department called "account",
 * get no, and land on the 404 page. A signed-out shopper following a bookmark to their own order history
 * would be told the page does not exist. `canActivate` keeps the match and redirects, which is what the
 * brief asks for and what a shopper can act on.
 *
 * ## It lets the server render the page
 *
 * `sd_customer_auth` is not forwarded during SSR — `cartCookieInterceptor` forwards `sd_cart` and nothing
 * else, on purpose — so the server genuinely cannot tell whether this visitor is signed in. The three
 * answers it could give are: redirect everyone to the sign-in page (every bookmarked account URL would
 * serve the wrong page, and the browser would then have to navigate away from it), block the render until
 * a cookie it does not have resolves (impossible), or render the page's shell and let the browser decide.
 * It does the third. The page's own markup carries no personal data on the server, answers `no-store` and
 * `noindex`, and the browser's first navigation runs this guard for real.
 *
 * `AccountStore.load()` returns `of(null)` synchronously on the server, which would redirect — so the
 * platform check comes **first** and is the thing being tested, not a side effect of the store's answer.
 *
 * ## The cold-load race, and why there is only one request
 *
 * On a cold load this guard runs during the first navigation, and the site header schedules its own read
 * from `afterNextRender`. `AccountStore.load()` is single-flight: whichever asks first makes the request
 * and the other gets the same observable, so there is one `GET /api/account/me` and no window in which
 * the two disagree. Checked against `app.config.ts`: `provideRouter(routes, withComponentInputBinding())`
 * sets no `onSameUrlNavigation`, so the default `'ignore'` applies and re-navigating to the same URL does
 * not re-run this guard — and if it did, the store would answer it from the memo without a request.
 */
export const customerSignedIn: CanActivateFn = (
  route: ActivatedRouteSnapshot,
  state: RouterStateSnapshot,
): Observable<GuardResult> | GuardResult => {
  // The server has no cookie to read, so it renders the shell. See the docblock.
  if (!isPlatformBrowser(inject(PLATFORM_ID))) {
    return true;
  }

  const router = inject(Router);
  const store = inject(AccountStore);

  return store.load().pipe(
    map((customer) =>
      customer !== null
        ? true
        : router.createUrlTree(['/account/sign-in'], {
            // **The whole URL including its query string**, taken from the router's state rather than
            // rebuilt from the snapshot: `/account/orders?page=3` has to come back as page 3, and a
            // shopper sent to sign in from page 3 of their history and returned to page 1 would read it
            // as having lost their place.
            queryParams: { returnTo: state.url },
          }),
    ),
  );
};

/**
 * Where to go after signing in, from a `returnTo` the sign-in page was given.
 *
 * **Same-origin paths only, and nothing else.** `returnTo` arrives in the address bar, so it is a string
 * a stranger can write: a link to `/account/sign-in?returnTo=https://example.invalid/pay` would turn the
 * shop's own sign-in form into an open redirect, which is a phishing primitive and the one real security
 * decision on these pages. So the value must start with a single `/` and must not start with `//` — which
 * a browser reads as a protocol-relative absolute URL to another host — and anything else falls back to
 * the order history.
 *
 * It is not checked against the route table. An unknown path lands on the storefront's own 404, which is
 * a page of this site; refusing it here would mean this function needing to know every route.
 */
export function returnPath(returnTo: string | null | undefined): string {
  if (typeof returnTo !== 'string' || !returnTo.startsWith('/') || returnTo.startsWith('//')) {
    return DEFAULT_RETURN_PATH;
  }

  // A backslash is the other way out of an origin: some browsers normalise `/\evil.test` to `//evil.test`
  // before resolving it. Refused rather than rewritten.
  return returnTo.includes('\\') ? DEFAULT_RETURN_PATH : returnTo;
}

/**
 * Where a shopper lands when they signed in without being sent anywhere.
 *
 * The order history rather than a hub, for the reason `account.routes.ts` gives: doc 11 P11 step 12's
 * list is what a customer signs in to look at.
 */
export const DEFAULT_RETURN_PATH = '/account/orders';
