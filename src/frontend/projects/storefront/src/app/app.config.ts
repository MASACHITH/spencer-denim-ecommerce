import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideClientHydration, withEventReplay } from '@angular/platform-browser';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { apiBaseUrlInterceptor } from '@spencer-denim/shared';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // Hydration includes the HTTP transfer cache: data fetched during SSR is not fetched again in the browser.
    provideClientHydration(withEventReplay()),
    provideHttpClient(withFetch(), withInterceptors([apiBaseUrlInterceptor])),
  ],
};
