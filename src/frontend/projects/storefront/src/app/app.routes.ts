import { Routes } from '@angular/router';

import { isKnownCategory, isKnownDepartment } from './core/navigation/catalog-route.guards';
import { ListingMode } from './pages/listing/listing-mode';

/** The listing serves a department, its category tree, New Arrivals and Sale (DEPT-01..03, PLP-01..07). */
const listingPage = () => import('./pages/listing/listing-page').then((m) => m.ListingPage);

/** The product detail page (PDP-01..07). */
const productPage = () => import('./pages/product/product-page').then((m) => m.ProductPage);

/** An address the catalogue does not have. Answers a real 404 (see the component). */
const notFoundPage = () => import('./pages/not-found/not-found-page').then((m) => m.NotFoundPage);

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

  // A department we know with a category we do not — /women/shirts names a Men's category — is a
  // page that is missing, not the department page and not the home page.
  { path: ':department/:category', canMatch: [isKnownDepartment], loadComponent: notFoundPage },
  {
    path: ':department/:category/:subcategory',
    canMatch: [isKnownDepartment],
    loadComponent: notFoundPage,
  },

  { path: '**', redirectTo: '' },
];
