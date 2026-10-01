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

  { path: '**', redirectTo: '' },
];
