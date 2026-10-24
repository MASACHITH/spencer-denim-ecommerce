import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import {
  provideClientHydration,
  withEventReplay,
  withNoHttpTransferCache,
} from '@angular/platform-browser';
import {
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
} from '@angular/router';
import { apiBaseUrlInterceptor } from '@spencer-denim/shared';

import { cartCookieInterceptor } from './core/cart/cart-cookie.interceptor';
import { routes } from './app.routes';
import { PageViewTracker } from './core/traffic/page-view-tracker';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      withComponentInputBinding(),
      // Without this the router never scrolls: a shopper who clicked "About us" or "Returns and exchanges" in
      // the footer arrived on the new page still scrolled to the bottom of the old one, looking at a footer
      // under an empty-looking page. `enabled` takes a forward navigation to the top and puts Back and
      // Forward at the position the shopper left. `anchorScrolling` keeps a `#fragment` navigation (the
      // "Skip to content" link is one) going to its anchor: with restoration on and anchors off the router
      // would scroll to the top instead. Today the two coincide, because the header keeps scroll-padding-top
      // equal to its own height, so this is for the next anchor and not a visible fix. A navigation that only
      // changes what the same page shows (a filter, the sort, Load More, a size) passes `scroll: 'manual'` and
      // stays where it is. The router does not scroll until the freshly hydrated app is first stable (Angular's
      // own guard), so a click in the first moments after a load is left where it is.
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled', anchorScrolling: 'enabled' }),
    ),
    // The HTTP transfer cache is off. It cannot serve this application — it keys a response on
    // the request URL as it sees it, and it runs after `apiBaseUrlInterceptor` has rewritten that
    // URL to the API's internal address, so the browser never finds the entry (see
    // `ServerHandover`, which hands the two catalogue calls over explicitly instead). Leaving it
    // on serialised every SSR response into the page a second time — 23.8 kB of JSON nothing
    // reads on a department listing — with `API_INTERNAL_URL` written into each entry.
    provideClientHydration(withEventReplay(), withNoHttpTransferCache()),
    // `cartCookieInterceptor` before `apiBaseUrlInterceptor` only for readability; neither depends on
    // the other's output. It is a no-op in the browser, where the bag cookie travels by itself on a
    // same-origin request, and on the server it copies `sd_cart` (and nothing else) onto the one call
    // that needs it.
    provideHttpClient(withFetch(), withInterceptors([cartCookieInterceptor, apiBaseUrlInterceptor])),
    // Traffic recording (W4-03): in the browser, every page reached by clicking, and the first page when
    // the server did not record it. A no-op on the server. See `core/traffic/page-view-tracker.ts`.
    provideAppInitializer(() => inject(PageViewTracker).start()),
  ],
};
