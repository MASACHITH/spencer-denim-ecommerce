import { Routes } from '@angular/router';

/** The listing serves a department and every depth of its category tree (DEPT-01, PLP-01..07). */
const listingPage = () => import('./pages/listing/listing-page').then((m) => m.ListingPage);

export const routes: Routes = [
  {
    path: '',
    title: 'Spencer Denim',
    loadComponent: () => import('./pages/home/home-page').then((m) => m.HomePage),
  },
  // Reserved for the product detail page (W2-04, PDP-01..08). It is listed ahead of the
  // department routes so a product link is never read as a department listing; until that page
  // exists it falls back to the home page rather than to an empty one.
  { path: 'product/:slug', redirectTo: '' },
  { path: ':department', loadComponent: listingPage },
  { path: ':department/:category', loadComponent: listingPage },
  // The Kids tree is three levels deep (docs/08 §9), and the mega menu links to all of it, e.g.
  // /kids/girls/girls-dresses. The API filters on the deepest segment and returns its subtree.
  { path: ':department/:category/:subcategory', loadComponent: listingPage },
  { path: '**', redirectTo: '' },
];
