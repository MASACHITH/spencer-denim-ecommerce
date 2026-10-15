import { Routes } from '@angular/router';

import {
  isKnownContentPage,
  isUnconfirmedContentPage,
} from './core/content/content-page.guards';
import {
  isKnownCategory,
  isKnownDepartment,
  isMissingCategory,
  isUnconfirmedCatalogue,
} from './core/navigation/catalog-route.guards';
import { NOT_A_PAGE_VIEW } from './core/traffic/page-view-tracker';
import { ListingMode } from './pages/listing/listing-mode';

/** The listing serves a department, its category tree, New Arrivals and Sale (DEPT-01..03, PLP-01..07). */
const listingPage = () => import('./pages/listing/listing-page').then((m) => m.ListingPage);

/** The product detail page (PDP-01..07). */
const productPage = () => import('./pages/product/product-page').then((m) => m.ProductPage);

/** The search results page (SRCH-02). */
const searchPage = () => import('./pages/search/search-page').then((m) => m.SearchPage);

/** The bag page (CART-01). */
const bagPage = () => import('./pages/bag/bag-page').then((m) => m.BagPage);

/** The wishlist page (WISH-01). */
const wishlistPage = () => import('./pages/wishlist/wishlist-page').then((m) => m.WishlistPage);

/** Checkout (CHK-01..03). */
const checkoutPage = () =>
  import('./pages/checkout/checkout-page').then((m) => m.CheckoutPage);

/** The order success page and a guest's order link (CHK-04). */
const orderPage = () => import('./pages/order/order-page').then((m) => m.OrderPage);

/** My account (W3-01, ACC-01, ACC-02). One lazy chunk; see `pages/account/account.routes.ts`. */
const accountRoutes = () => import('./pages/account/account.routes').then((m) => m.accountRoutes);

/** A published content page: About, Contact, Returns, Shipping, Privacy, Terms, or a custom one (GLB-10). */
const contentPage = () => import('./pages/content-page/content-page').then((m) => m.ContentPage);

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
 * a route will be looking. A leftover entry cannot take effect — the real route is declared above this
 * list and wins — but it says the site has no such page, so the spec fails on one.
 *
 * **W2-11 has removed six of the seven**, which is that rule being followed: GLB-10 built `about`,
 * `contact`, `returns`, `shipping`, `privacy` and `terms`, so the entries claiming the site has no such
 * addresses go in the same change. They are not replaced by six fixed routes, because a content page's
 * slug is editable (doc 11 P13 step 12) and six hard-coded paths would be wrong the first time a client
 * renames one. A single `:slug` route behind a guard over the published-page index serves all six and
 * any extra page the client creates — see the bottom of the route list.
 *
 * **The list is now empty, and `account` was the last entry (W3-01).** That is the rule above being
 * followed to its end: `/account` and its eight pages are real, so the entry claiming the site has no
 * such address is deleted in the same change. The constant stays, typed `readonly string[]` so an empty
 * literal is still assignable, because the invariant it carries is the useful part and the next task that
 * links the shell at a page it has not built yet needs somewhere to say so — the alternative is deleting
 * the constant, the spec and the explanation, and rediscovering #28.
 *
 * `app.routes.spec.ts` has a matching change: its `linkedFromTheShell` list held `'account'` and now
 * holds nothing, for the same reason, and the test that every entry here has no real route of its own is
 * what would have caught a leftover.
 *
 * **`/wishlist` has been taken out by W2-08**, which is that rule being followed: the page is real, so
 * the entry claiming the site has no such address goes in the same change.
 *
 * **`/search` and `/bag` were in this list and have been taken out**, because W2-07 built the search
 * page (SRCH-02) and W2-09 built the bag page (CART-01), and an entry for an address that has a real
 * route is dead: it cannot take effect, since the real route is declared above it, and it tells the
 * next reader that the address has no page. Both were added here when the page did not exist and the
 * shell already linked to it. `app.routes.spec.ts` asserts that no path in this list has a route of
 * its own, so the next one to be built cannot be left behind the same way.
 *
 * **`/checkout` has been taken out by W3-02**, which is the rule above being followed: the page is real,
 * so the entry claiming the site has no such address is deleted in the same change. `/order/:publicId`
 * was never in this list — nothing linked to it before there were orders to link to.
 *
 * `/bag` is worth one more line, because the rebase very nearly got it wrong: git **auto-merged**
 * W2-09's route declaration in above this list and left `'bag'` in the list, with no conflict to look
 * at — exactly how `/search` survived here through W2-07. The route wins at runtime, so every
 * behavioural test still passed; only the spec below could see it.
 */
export const UNBUILT_PATHS: readonly string[] = [];

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

  // The bag (W2-09, CART-01). Like `search`, a single-segment route listed **ahead of `:department`**,
  // which matches any single segment, and ahead of `UNBUILT_PATHS` below, whose `bag` entry this
  // change removes — before this page existed the header's bag link redirected to the home page.
  //
  // It takes no `canMatch` guard — the page is valid for every visitor, including one with an empty
  // bag — and so it can never answer 404 or 503.
  { path: 'bag', loadComponent: bagPage },

  // The wishlist (W2-08, WISH-01). A single-segment route listed **ahead of `:department`**, which
  // matches any single segment, and its `UNBUILT_PATHS` entry is removed in this change — the rule that
  // list states for itself, and the one `/search` and `/bag` were each left behind by once.
  //
  // No `canMatch` guard: the page is valid for every visitor, including one with nothing saved, which it
  // says rather than 404ing. It is personal, so it answers `no-store` and `noindex` like `/bag` — but
  // unlike `/bag` it renders **no list on the server at all**, because a guest's wishlist is in their own
  // browser (doc 08 §1.7) and the server has nothing to render it from. See the component.
  { path: 'wishlist', loadComponent: wishlistPage },

  // My account (W3-01, ACC-01, ACC-02). Listed **ahead of `:department`**, which matches any single
  // segment, and its `UNBUILT_PATHS` entry is removed in this change — the rule that list states for
  // itself, and the one `/search` and `/bag` were each left behind by once.
  //
  // **`loadChildren` and no `pathMatch`**, so this claims `/account` and everything under it. The child
  // table redirects `/account` itself to `/account/orders` and guards the three signed-in pages; see
  // `pages/account/account.routes.ts` for why there is no hub page and which routes are deliberately
  // unguarded. An address under `/account` that no child matches makes this route fail to match at all,
  // and the router backtracks to the rest of the table and ends at `**` — a real 404, which is what
  // `/account/bogus` should be.
  //
  // **The guard is `canActivate` and not `canMatch`**, which matters because of where this sits: a
  // `canMatch` that answered no would let the router carry on to `:department/:category`, ask the
  // catalogue about a department called "account", and land a signed-out shopper on the 404 page instead
  // of the sign-in form. `signed-in.guard.ts` records that.
  //
  // Every page under it is personal, so each answers `no-store`, `private`, `Vary: Cookie` and `noindex`
  // — the treatment `/bag` and `/checkout` get, for the same reason and more sharply. Unlike `/checkout`,
  // **no account page renders its personal data on the server**: `sd_customer_auth` is not forwarded by
  // `cartCookieInterceptor` (deliberately, see that file), so the server renders each page's shell and
  // the browser reads the data. Each page's docblock says so.
  { path: 'account', loadChildren: accountRoutes },

  // Checkout (W3-02, CHK-01..03). A single-segment route listed **ahead of `:department`**, which matches
  // any single segment, and its `UNBUILT_PATHS` entry is removed in this change.
  //
  // No `canMatch` guard: the page is valid for every visitor, including one with an empty bag, which it
  // says so rather than 404ing. It renders on the server and is per-visitor, so it answers `no-store`
  // and `noindex` — the same treatment `/bag` gets, for the same reason, and more sharply, because this
  // page's markup holds a name, an address and a phone number.
  { path: 'checkout', loadComponent: checkoutPage },

  // The order success page and a guest's order link (W3-02, CHK-04). Two segments, so it cannot collide
  // with `:department`, and the page answers 404 when the token names no order, exactly as a wrong
  // product slug does.
  //
  // **The segment is `:token`, not `:publicId` or `:id`, and that is GLB-11 being obeyed rather than
  // worked around.** `app.routes.spec.ts` requires every route to be slug-shaped with no ids in it,
  // because doc 02 GLB-11 asks for friendly URLs — and an order genuinely has no slug: there is nothing
  // human-readable to address it by, and it must not be addressable by a sequential id, because the
  // address is the only credential a guest's link can carry (doc 08 §3.5 `Orders.PublicId`). So the
  // segment is named after what it is, an unguessable token, which is both honest and lower case.
  { path: 'order/:token', loadComponent: orderPage },

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
  // about. So the list is the price of fixing #28 before GLB-10 lands; #28 itself says the catch-all
  // may become the 404 once it has. **Each of these is deleted by the task that gives the page a real
  // route** — GLB-10 for the six content pages, W3-01 for /account — and W2-07 (/search), W2-09 (/bag),
  // W3-02 (/checkout) and W2-08 (/wishlist) have already deleted theirs, which is the pattern. A
  // leftover entry cannot take effect, because a real route declared above it wins, but it is a lie
  // about what the site has, so `app.routes.spec.ts` fails on one.
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
  // footer link that is not built yet — /account, /contact, /privacy and the rest, and /bag until
  // W2-09. A guard that answers "no" lets the router carry on to `**`, which is where those went
  // before.
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
  {
    path: ':department/:category',
    canMatch: [isMissingCategory],
    loadComponent: notFoundPage,
    data: NOT_A_PAGE_VIEW,
  },
  {
    path: ':department/:category/:subcategory',
    canMatch: [isMissingCategory],
    loadComponent: notFoundPage,
    data: NOT_A_PAGE_VIEW,
  },

  // The catalogue could not be asked, so neither the listing nor the 404 above can be right. These
  // answer 503 with `Retry-After`, which a crawler retries and which keeps the URL.
  {
    path: ':department/:category',
    canMatch: [isUnconfirmedCatalogue],
    loadComponent: catalogUnavailablePage,
    data: NOT_A_PAGE_VIEW,
  },
  {
    path: ':department/:category/:subcategory',
    canMatch: [isUnconfirmedCatalogue],
    loadComponent: catalogUnavailablePage,
    data: NOT_A_PAGE_VIEW,
  },

  // The content pages (W2-11, GLB-10): About, Contact, Returns, Shipping, Privacy, Terms, and any extra
  // page the client has created.
  //
  // **One `:slug` route rather than six fixed ones**, because a page's slug is editable (doc 11 P13 step
  // 12) and a hard-coded `/returns` would be wrong the first time a client spells it differently. The
  // guard answers from the published-page index, which the footer reads anyway, so a scanner's probe
  // costs no request — the alternative, letting the component resolve every single segment, is one API
  // call per probe on a public route.
  //
  // **Declared after the department routes**, so a department listing is never looked up as a page, and
  // **before `**`**, so a published page is not swept into the 404.
  //
  // An unpublished page is absent from the index, so it falls through to `**` and answers a real 404 —
  // which is the whole of "a draft must not leak": not linked, not crawled, not openable.
  //
  // **`pathMatch: 'full'`, and it is load-bearing** — the same lesson `UNBUILT_PATHS` records having
  // measured. A route's default is `'prefix'`, so `:slug` matches `/about/x` by consuming `about` and
  // leaving `x`: the match then fails for want of a child route, but the `canMatch` guard has already
  // run, so every deeper address on the origin paid for a content-feed lookup it could not use. Caught
  // by `listing-page.spec.ts`, where `/bogus/thing` left an outstanding `GET /api/content/pages` that
  // `HttpTestingController.verify()` reported.
  { path: ':slug', pathMatch: 'full', canMatch: [isKnownContentPage], loadComponent: contentPage },

  // The mirror of the listing's own 503 route (#30), for the single-segment space. The content feed did
  // not answer, so the storefront cannot say whether this address is a published page: a 404 here would
  // drop a live policy page out of the index for the length of an API wobble, where a 503 with
  // `Retry-After` is retried and keeps the URL.
  //
  // It costs a 503 on a genuinely mistyped single segment during a content-feed outage, which is the
  // right way round: the page that exists matters more than the page that does not. The guard is bounded
  // to one segment, so `/api/...` reached directly, `/.well-known/...` and every deeper address are
  // outside it by construction — the mistake `isUnconfirmedCatalogue` records having measured.
  {
    path: ':slug',
    pathMatch: 'full',
    canMatch: [isUnconfirmedContentPage],
    loadComponent: catalogUnavailablePage,
    data: NOT_A_PAGE_VIEW,
  },

  // Anything left is an address this site does not have: a mistyped department (`/not-a-dept`), a
  // mistyped anything (`/bogus/thing`), a scanner's probe, a stale deep link, or a content page that is
  // not published.
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
  { path: '**', loadComponent: notFoundPage, data: NOT_A_PAGE_VIEW },
];
