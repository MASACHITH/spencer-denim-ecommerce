import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';

import { AdminAuthApi } from './admin-auth-api';

export const loginPath = '/login';
export const changePasswordPath = '/change-password';

/**
 * Guards the admin pages. Asks the API who is signed in (the HttpOnly cookie is invisible to script), then:
 * not signed in → sign-in page; a forced password change pending (issue #17) → change-password page; otherwise
 * through. Convenience only: the API refuses every admin endpoint itself while the change is pending (doc 03
 * §3a — the server is the security boundary). If the API cannot be reached at all the page is shown anyway, so
 * the dashboard's API status panel can say so.
 */
export const adminSessionGuard: CanActivateFn = () => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map((session) => (session.mustChangePassword ? router.parseUrl(changePasswordPath) : true)),
    catchError((error: unknown) =>
      of(isUnauthorized(error) ? router.parseUrl(loginPath) : true),
    ),
  );
};

/** The change-password page needs a session, but not a finished password change. */
export const signedInGuard: CanActivateFn = () => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map(() => true),
    catchError((error: unknown) =>
      of(isUnauthorized(error) ? router.parseUrl(loginPath) : true),
    ),
  );
};

function isUnauthorized(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 401;
}
