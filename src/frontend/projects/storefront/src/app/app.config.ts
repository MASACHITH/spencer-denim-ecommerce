import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import {
  provideClientHydration,
  withEventReplay,
  withNoHttpTransferCache,
} from '@angular/platform-browser';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { apiBaseUrlInterceptor } from '@spencer-denim/shared';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // The HTTP transfer cache is off. It cannot serve this application — it keys a response on
    // the request URL as it sees it, and it runs after `apiBaseUrlInterceptor` has rewritten that
    // URL to the API's internal address, so the browser never finds the entry (see
    // `ServerHandover`, which hands the two catalogue calls over explicitly instead). Leaving it
    // on serialised every SSR response into the page a second time — 23.8 kB of JSON nothing
    // reads on a department listing — with `API_INTERNAL_URL` written into each entry.
    provideClientHydration(withEventReplay(), withNoHttpTransferCache()),
    provideHttpClient(withFetch(), withInterceptors([apiBaseUrlInterceptor])),
  ],
};
