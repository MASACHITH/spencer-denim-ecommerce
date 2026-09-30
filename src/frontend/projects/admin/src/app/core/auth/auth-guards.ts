import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { catchError, map, of } from 'rxjs';

import { AdminAuthApi } from './admin-auth-api';

export const loginPath = '/login';
export const changePasswordPath = '/change-password';

/**
 * Query param a redirect-to-login/change-password carries the visitor's intended destination in, so the login
 * (or change-password) page can send them on to where they meant to go instead of always landing on '/'
 * (W1-11, #8). Read back with {@link sanitizeReturnUrl}, never trusted as-is.
 */
export const returnUrlParam = 'returnUrl';

/**
 * Guards the admin pages. Asks the API who is signed in (the HttpOnly cookie is invisible to script), then:
 * not signed in → sign-in page; a forced password change pending (issue #17) → change-password page; otherwise
 * through. Convenience only: the API refuses every admin endpoint itself while the change is pending (doc 03
 * §3a — the server is the security boundary). If the API cannot be reached at all the page is shown anyway, so
 * the dashboard's API status panel can say so.
 */
export const adminSessionGuard: CanActivateFn = (_route, state) => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map((session) =>
      session.mustChangePassword ? redirectWithReturnUrl(router, changePasswordPath, state) : true,
    ),
    catchError((error: unknown) =>
      of(isUnauthorized(error) ? redirectWithReturnUrl(router, loginPath, state) : true),
    ),
  );
};

/** The change-password page needs a session, but not a finished password change. */
export const signedInGuard: CanActivateFn = (_route, state) => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map(() => true),
    catchError((error: unknown) =>
      of(isUnauthorized(error) ? redirectWithReturnUrl(router, loginPath, state) : true),
    ),
  );
};

/**
 * Only ever accepts a same-origin, in-app path (starts with a single `/`) — never a scheme-relative or
 * protocol-carrying value (`//evil.example`, `https://…`) that could turn "take me back where I was" into an
 * open redirect. Anything else, including a missing or empty param, falls back to the caller's default.
 */
export function sanitizeReturnUrl(value: string | null | undefined): string | null {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return null;
  }

  return value;
}

/**
 * Builds the redirect target, tacking the attempted destination on as `returnUrl` — unless it IS that target,
 * or it's just the dashboard root, in which case landing there is already the default and the param would add
 * nothing but noise to the address bar.
 */
function redirectWithReturnUrl(router: Router, path: string, state: RouterStateSnapshot): UrlTree {
  const attempted = sanitizeReturnUrl(state.url);
  const worthKeeping = attempted !== null && attempted !== path && attempted !== '/';

  return router.createUrlTree([path], {
    queryParams: worthKeeping ? { [returnUrlParam]: attempted } : undefined,
  });
}

function isUnauthorized(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 401;
}
