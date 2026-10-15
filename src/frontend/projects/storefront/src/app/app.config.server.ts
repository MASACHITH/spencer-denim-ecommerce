import {
  ApplicationConfig,
  REQUEST,
  inject,
  mergeApplicationConfig,
  provideAppInitializer,
} from '@angular/core';
import { provideServerRendering, withRoutes } from '@angular/ssr';
import { API_BASE_URL } from '@spencer-denim/shared';

import { appConfig } from './app.config';
import { serverRoutes } from './app.routes.server';
import { apiInternalUrl, publicOriginFor } from '../server/origins';
import { SITE_ORIGIN } from './core/platform/site-origin';
import { markServerPageView } from './core/traffic/traffic-render-context';

const serverConfig: ApplicationConfig = {
  providers: [
    provideServerRendering(withRoutes(serverRoutes)),
    // On the server, relative /api calls go straight to the API (API_INTERNAL_URL), not through the browser proxy.
    // One definition of "where the API is", shared with the server's own robots.txt/sitemap.xml
    // routes, so a render and a sitemap can never be built against two different catalogues.
    { provide: API_BASE_URL, useFactory: () => apiInternalUrl() },

    // The absolute origin the Open Graph tags publish (GLB-11). In the browser the token's own factory
    // reads `location.origin`; here there is no such thing, so it comes from the deployment's
    // PUBLIC_SITE_ORIGIN or, failing that, from a request host we recognise. `src/server/origins.ts`
    // holds the decision and the reason it refuses to echo a host it does not know.
    { provide: SITE_ORIGIN, useFactory: () => publicOriginFor(inject(REQUEST)) },

    // Traffic recording (W4-03): tells `server.ts` the render ran and, when it will record this page,
    // leaves the flag that stops the browser recording it a second time. See `src/server/page-view.ts`.
    provideAppInitializer(markServerPageView),
  ],
};

export const config = mergeApplicationConfig(appConfig, serverConfig);
