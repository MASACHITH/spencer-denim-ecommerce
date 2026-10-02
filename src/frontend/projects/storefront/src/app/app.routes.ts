import { Routes } from '@angular/router';

import {
  isKnownCategory,
  isKnownDepartment,
  isMissingCategory,
  isUnconfirmedCatalogue,
} from './core/navigation/catalog-route.guards';
import { ListingMode } from './pages/listing/listing-mode';

/** The listing serves a department, its category tree, New Arrivals and Sale (DEPT-01..03, PLP-01..07). */
const listingPage = () => import('./pages/listing/listing-page').then((m) => m.ListingPage);

/** The product detail page (PDP-01..07). */
const productPage = () => import('./pages/product/product-page').then((m) => m.ProductPage);

/** The search results page (SRCH-02). */
const searchPage = () => import('./pages/search/search-page').then((m) => m.SearchPage);

/** An address the catalogue does not have. Answers a real 404 (see the component). */
const notFoundPage = () => import('./pages/not-found/not-found-page').then((m) => m.NotFoundPage);

/** An address the catalogue could not be asked about. Answers 503 with `Retry-After` (#30). */
const catalogUnavailablePage = () =>
  import('./pages/catalog-unavailable/catalog-unavailable-page').then(
    (m) => m.CatalogUnavailablePage,
  );

/**
 * Addresses the storefront's own header, footer and search form point at, whose pages belong to tasks
 * that have not landed (#28).
 *
 * They are gathered here rather than written into the route list one by one so that
 * `app.routes.spec.ts` can hold the two halves of the invariant together: every path in this list is
 * linked from the shell *and* has no page of its own, and every other address answers 404.
 *
 * **If you are building one of these pages, delete its entry in the same change.** That is the whole
 * maintenance rule, and it is here rather than only in a journal because this is where someone adding
 * a route will be looking. Each entry is owed by a known task: GLB-10 for `about`, `contact`,
 * `returns`, `shipping`, `privacy` and `terms`; W2-08 for `wishlist`; W2-09 for `bag`; W3-01 for
 * `account`; W3-02 for `checkout`. A leftover entry cannot take effect — the real route is declared
 * above this list and wins — but it says the site has no such page, so the spec fails on one.
 *
 * **`/search` was in this list and has been taken out**, because W2-07 built the page (SRCH-02) and an
 * entry for an address that has a real route is dead: it cannot take effect, since the real route is
 * declared above it, and it tells the next reader that `/search` has no page. It was added here when
 * the page did not exist and the header's form already navigated to it. `app.routes.spec.ts` now
 * asserts that no path in this list has a route of its own, so the next one to be built cannot be left
 * behind the same way.
 */
export const UNBUILT_PATHS = [
  'about',
  'account',
  'bag',
  'checkout',
  'contact',
  'privacy',
  'returns',
  'shipping',
  'terms',
  'wishlist',
] as const;

export const routes: Routes = [
  {
    // No `title` here: the page sets its own, as the listing and product pages do. A route title
    // and a component title are applied by different mechanisms at different moments, and the
    // loser is whichever ran first.
    path: '',
    loadComponent: () => import('./pages/home/home-page').then((m) => m.HomePage),
  },
  // The product page (W2-05, PDP-01..07). It is listed ahead of the department routes so a
  // product link is never read as a department listing. No `canMatch` guard: only the catalogue
  // knows whether a slug is a product, and the page answers a real 404 when it is not — which is
  // also what a malformed slug gets, because the API refuses to look one up.
  { path: 'product/:slug', loadComponent: productPage },

  // The search results page (W2-07, SRCH-02). A single-segment route, so it is listed **ahead of
  // `:department`**, which matches any single segment: a department called "search" is not what the
  // address means, and a `canMatch` verdict on it is not something to leave to the catalogue.
  //
  // It takes neither of the behaviours the listing routes grew in #30. There is no `canMatch` guard
  // because there is nothing to confirm — the page is valid for every term, including one that
  // matches nothing — and so there is no path by which it can answer 404 or 503. See the component.
  { path: 'search', loadComponent: searchPage },

  // Two of the five permanent navigation entries (GLB-05). They are the same listing with one
  // filter fixed and no department, so neither points at a not-found page while DEPT-02 and
  // DEPT-03 are outstanding; their banners and editorial copy arrive with those tasks.
  {
    path: 'new-arrivals',
    loadComponent: listingPage,
    data: { listing: 'new-arrivals' satisfies ListingMode },
  },
  { path: 'sale', loadComponent: listingPage, data: { listing: 'sale' satisfies ListingMode } },

  // Pages the site already links to and has not built yet (#28). Each one keeps the behaviour the
  // catch-all used to give it — a redirect to the home page — and the catch-all itself now answers a
  // real 404.
  //
  // **Why they have to be listed, rather than left to the catch-all.** The header and the footer link
  // to every one of these on every page of the site. Answering 404 on an address the shop's own footer
  // offers would hand a crawler a site-wide broken link, which is worse than the soft 404 #28 is
  // about. So the list is the price of fixing #28 before GLB-10, W2-08 and W2-09 land; #28 itself says
  // the catch-all may become the 404 once they have. **Each of these is deleted by the task that gives
  // the page a real route** — GLB-10 for the six content pages, W2-08 for /wishlist, W2-09 for /bag,
  // W3-01 for /account, W3-02 for /checkout — and W2-07 already deleted /search's, which is the
  // pattern. A leftover entry cannot take effect, because a real route declared above it wins, but it
  // is a lie about what the site has, so `app.routes.spec.ts` fails on one.
  //
  // Ahead of `:department` on purpose: these are known answers and should not wait on the catalogue
  // feed to be given. Behind `search` on purpose too, which is the same point from the other side:
  // W2-07's page is a real route and claims its address before this list is consulted.
  //
  // `pathMatch: 'full'`, and it is load-bearing. A route's default is `'prefix'`, so `path: 'account'`
  // also matched `/account/orders` — Angular then redirected to the matched prefix's target and kept
  // the rest, and the result was a **404 with an empty body**: a blank page for a shopper and nothing
  // for a crawler, which is worse than the soft 404 this change exists to remove. Measured on
  // `/account/orders`, `/bag/checkout` and `/about/x`, all three 404 at zero bytes. With `'full'` each
  // of these claims exactly its own address and everything deeper falls through to the 404 page.
  ...UNBUILT_PATHS.map((path) => ({ path, pathMatch: 'full' as const, redirectTo: '' })),

  // `:department` matches any single segment, so without the guard it swallowed every header and
  // footer link that is not built yet — /bag, /account, /contact, /privacy and the rest. A guard
  // that answers "no" lets the router carry on to `**`, which is where those went before.
  { path: ':department', canMatch: [isKnownDepartment], loadComponent: listingPage },
  { path: ':department/:category', canMatch: [isKnownCategory], loadComponent: listingPage },
  // The Kids tree is three levels deep (docs/08 §9), and the mega menu links to all of it, e.g.
  // /kids/girls/girls-dresses. The API filters on the deepest segment and returns its subtree.
  {
    path: ':department/:category/:subcategory',
    canMatch: [isKnownCategory],
    loadComponent: listingPage,
  },

  // A department we know with a category the **live** catalogue does not have — /women/shirts names
  // a Men's category — is a page that is missing, not the department page and not the home page.
  //
  // `isMissingCategory` and not `isKnownDepartment`: the guard has to have read a live tree before
  // it may say a page is gone. Read against the frozen fallback it said so about every category
  // added since the fallback was written, and a 404 with `noindex` drops a live listing out of the
  // index for the length of an API wobble (#30).
  { path: ':department/:category', canMatch: [isMissingCategory], loadComponent: notFoundPage },
  {
    path: ':department/:category/:subcategory',
    canMatch: [isMissingCategory],
    loadComponent: notFoundPage,
  },

  // The catalogue could not be asked, so neither the listing nor the 404 above can be right. These
  // answer 503 with `Retry-After`, which a crawler retries and which keeps the URL.
  {
    path: ':department/:category',
    canMatch: [isUnconfirmedCatalogue],
    loadComponent: catalogUnavailablePage,
  },
  {
    path: ':department/:category/:subcategory',
    canMatch: [isUnconfirmedCatalogue],
    loadComponent: catalogUnavailablePage,
  },

  // Anything left is an address this site does not have: a mistyped department (`/not-a-dept`), a
  // mistyped anything (`/bogus/thing`), a scanner's probe, a stale deep link.
  //
  // It used to redirect to the home page, which is a textbook soft 404 (#28): the wrong page under the
  // wrong URL, answered **200**, so a crawler following a typo or a stale link indexes the home page at
  // an address that is not it and nothing anywhere reports a problem. `NotFoundPage` answers a real 404
  // with `noindex`.
  //
  // It does **not** collide with #30. The 503 routes above claim every two- and three-segment address
  // whose first segment is a department the tree already holds, and they are matched first, so a
  // listing the catalogue could not be asked about still answers 503 with `Retry-After` rather than
  // being swept up here. The single-segment case is not ambiguous in the same way and so is not routed
  // the same way: **nothing creates a department at runtime** — the admin API has no create-department
  // endpoint, and W1-12 settled that a department's name and slug are not editable (docs/02 §I2) — so
  // the seeded list is closed, `core/navigation/navigation.data.ts` mirrors it, and a single segment
  // that is not in the tree in hand is not a department whichever tree that was. The assumption worth
  // writing down: if a later migration seeds a sixth department, that fallback has to gain it in the
  // same change, or its listing will answer 404 for the length of an outage.
  { path: '**', loadComponent: notFoundPage },
];
