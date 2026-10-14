import { Routes } from '@angular/router';

import { customerSignedIn } from '../../core/account/signed-in.guard';

/**
 * My account (ACC-01, ACC-02, doc 11 P11), loaded as one lazy chunk under `/account`.
 *
 * ## `/account` redirects to `/account/orders` rather than rendering a hub
 *
 * The brief left this to the builder and asked for a justification, so here it is. A hub whose entire
 * content is three links is a page a shopper has to get past in order to reach anything, and doc 11 P11
 * step 12's order list is what they signed in to look at — "where is my parcel" is the question the whole
 * of W3-01 exists to answer. The three links still exist, as {@link AccountNav}, on each of the three
 * signed-in pages, so nothing is lost but a click.
 *
 * The redirect also makes the existing shell links come out right without changing them. The header's
 * account icon and the mobile drawer's "Sign in / Register" both point at `/account`: a signed-out
 * visitor following either is sent by {@link customerSignedIn} to `/account/sign-in?returnTo=/account/orders`
 * and lands on their orders once they sign in, and a signed-in one goes straight there. One link, two
 * correct outcomes.
 *
 * **`pathMatch: 'full'` on the redirect**, which is load-bearing for the reason `UNBUILT_PATHS` records
 * measuring: a route's default is `'prefix'`, so a redirect declared as `path: ''` without it would also
 * match every child address and send `/account/orders` to itself in a loop.
 *
 * ## Which routes are guarded, and which deliberately are not
 *
 * Guarded: `orders`, `addresses`, `profile`. Each needs `GET /api/account/me` to have said yes, and a
 * signed-out visitor is sent to the sign-in page with the whole address — query string included — in
 * `returnTo`.
 *
 * Not guarded: `sign-in`, `register`, `forgot-password`, `reset-password`, `confirm-email`. The first two
 * are for people who are not signed in by definition; the last three are opened from a link in an email,
 * by whatever browser happens to read it, which may have no session at all — guarding them would make a
 * reset link unusable in exactly the case it is needed. `confirm-email` is also the one route that
 * *creates* a session, so requiring one would be circular.
 *
 * **There is no guard that keeps a signed-in shopper off the sign-in page.** `sign-in-page.ts` says why:
 * a page a shopper deliberately opened that bounces them elsewhere fights the back button, so it says
 * "you are already signed in as …" and offers the way on instead.
 *
 * ## One chunk, lazily
 *
 * `loadChildren` rather than eight `loadComponent`s, because these eight pages share four of this task's
 * files ({@link AccountNotice}, {@link AccountNav}, the API client, the store) and splitting them into
 * eight chunks would either duplicate those or create a shared chunk every one of them waits on. Nobody
 * who is not signed in pays for any of it: the chunk is fetched when `/account` is first visited.
 */
export const accountRoutes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'orders',
  },
  {
    path: 'sign-in',
    loadComponent: () => import('./sign-in-page').then((m) => m.SignInPage),
  },
  {
    path: 'register',
    loadComponent: () => import('./register-page').then((m) => m.RegisterPage),
  },
  {
    path: 'forgot-password',
    loadComponent: () => import('./forgot-password-page').then((m) => m.ForgotPasswordPage),
  },
  {
    path: 'reset-password',
    loadComponent: () => import('./reset-password-page').then((m) => m.ResetPasswordPage),
  },
  {
    path: 'confirm-email',
    loadComponent: () => import('./confirm-email-page').then((m) => m.ConfirmEmailPage),
  },
  {
    path: 'orders',
    canActivate: [customerSignedIn],
    loadComponent: () => import('./orders-page').then((m) => m.OrdersPage),
  },
  {
    path: 'addresses',
    canActivate: [customerSignedIn],
    loadComponent: () => import('./addresses-page').then((m) => m.AddressesPage),
  },
  {
    path: 'profile',
    canActivate: [customerSignedIn],
    loadComponent: () => import('./profile-page').then((m) => m.ProfilePage),
  },

  // Anything deeper under /account is an address this site does not have, and it falls through to the
  // storefront's own `**` route, which answers a real 404 with `noindex`. There is deliberately no
  // catch-all here: a second 404 page inside this chunk would be a second implementation of one, and a
  // redirect to `orders` would be the soft 404 #28 exists to remove — the wrong page under the wrong
  // URL, answered 200.
];
