import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { passwordChangeRequiredInterceptor } from './core/auth/password-change-required.interceptor';

/** Admin is client-side only; API calls are same-origin through the reverse proxy (/api). */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withFetch(), withInterceptors([passwordChangeRequiredInterceptor])),
  ],
};
