import { ApplicationConfig, mergeApplicationConfig } from '@angular/core';
import { provideServerRendering, withRoutes } from '@angular/ssr';
import { API_BASE_URL } from '@spencer-denim/shared';

import { appConfig } from './app.config';
import { serverRoutes } from './app.routes.server';

/** Default for `ng serve` with the API running under `dotnet watch`. */
const DEFAULT_API_INTERNAL_URL = 'http://localhost:5080';

const serverConfig: ApplicationConfig = {
  providers: [
    provideServerRendering(withRoutes(serverRoutes)),
    // On the server, relative /api calls must go straight to the API container (API_INTERNAL_URL).
    {
      provide: API_BASE_URL,
      useFactory: () => process.env['API_INTERNAL_URL'] || DEFAULT_API_INTERNAL_URL,
    },
  ],
};

export const config = mergeApplicationConfig(appConfig, serverConfig);
