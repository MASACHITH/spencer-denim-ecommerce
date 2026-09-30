import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { catchError, map, of } from 'rxjs';

import { AdminAuthApi, type AdminSession } from './admin-auth-api';

export const loginPath = '/login';
export const twoFactorLoginPath = '/login/two-factor';
export const changePasswordPath = '/change-password';
export const twoFactorSetupPath = '/two-factor/setup';

/**
 * Where a signed-in user has to go before anything else, in the API's own order (doc 11 P14 step 3): a forced
 * password change first (issue #17), then two-factor enrolment for a Super Admin (W1-10). Null when neither.
 */
export function pendingAccountStep(session: AdminSession): string | null {
  if (session.mustChangePassword) {
    return changePasswordPath;
  }

  return session.twoFactorEnrolmentRequired ? twoFactorSetupPath : null;
}

/**
 * Guards the admin pages. Asks the API who is signed in (the HttpOnly cookie is invisible to script), then:
 * not signed in → sign-in page; a pending account step → that page; otherwise through. Convenience only: the API
 * refuses every admin endpoint itself while a step is pending (doc 03 §3a — the server is the security boundary).
 * If the API cannot be reached at all the page is shown anyway, so the dashboard's API status panel can say so.
 */
export const adminSessionGuard: CanActivateFn = () => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map((session) => {
      const step = pendingAccountStep(session);
      return step ? router.parseUrl(step) : true;
    }),
    catchError((error: unknown) => of(signedOutOrThrough(error, router))),
  );
};

/** The change-password page needs a session, but not a finished password change or 2FA enrolment. */
export const signedInGuard: CanActivateFn = () => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map(() => true),
    catchError((error: unknown) => of(signedOutOrThrough(error, router))),
  );
};

/**
 * The 2FA enrolment page (W1-10) needs a session with the password change done: the API refuses enrolment until
 * then, so a user who still has to change it is sent there first.
 */
export const twoFactorSetupGuard: CanActivateFn = () => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map((session) => (session.mustChangePassword ? router.parseUrl(changePasswordPath) : true)),
    catchError((error: unknown) => of(signedOutOrThrough(error, router))),
  );
};

/** Signed out → the sign-in page; any other failure (API unreachable) → let the page show and report it. */
function signedOutOrThrough(error: unknown, router: Router): UrlTree | true {
  return error instanceof HttpErrorResponse && error.status === 401 ? router.parseUrl(loginPath) : true;
}
