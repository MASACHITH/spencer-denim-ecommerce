import { HttpInterceptorFn } from '@angular/common/http';
import { InjectionToken, inject } from '@angular/core';

/**
 * Base URL prepended to relative `/api/...` requests.
 * Empty in the browser (same origin through the reverse proxy).
 * The SSR server sets it to the internal API address, e.g. `http://api:8080`.
 */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => '',
});

/** Prefixes relative `/api/` and `/health` URLs with {@link API_BASE_URL} when one is configured. */
export const apiBaseUrlInterceptor: HttpInterceptorFn = (request, next) => {
  const baseUrl = inject(API_BASE_URL).replace(/\/+$/, '');
  const isRelativeApiUrl = request.url.startsWith('/api/') || request.url.startsWith('/health');

  if (!baseUrl || !isRelativeApiUrl) {
    return next(request);
  }

  return next(request.clone({ url: `${baseUrl}${request.url}` }));
};
