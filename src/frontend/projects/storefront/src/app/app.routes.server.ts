import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * Every storefront route is rendered on the server per request (SEO + live catalogue data).
 * Prerendering is not used: pages depend on the API, which is not available at build time.
 */
export const serverRoutes: ServerRoute[] = [
  {
    path: '**',
    renderMode: RenderMode.Server,
  },
];
