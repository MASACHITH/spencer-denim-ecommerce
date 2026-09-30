import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
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
 * Query param a redirect-to-login/change-password carries the visitor's intended destination in, so the login
 * (or change-password) page can send them on to where they meant to go instead of always landing on '/'
 * (W1-11, #8). Read back with {@link sanitizeReturnUrl}, never trusted as-is.
 */
export const returnUrlParam = 'returnUrl';

/**
 * Guards the admin pages. Asks the API who is signed in (the HttpOnly cookie is invisible to script), then:
 * not signed in → sign-in page; a pending account step → that page; otherwise through. Convenience only: the API
 * refuses every admin endpoint itself while a step is pending (doc 03 §3a — the server is the security boundary).
 * If the API cannot be reached at all the page is shown anyway, so the dashboard's API status panel can say so.
 */
export const adminSessionGuard: CanActivateFn = (_route, state) => {
  const authApi = inject(AdminAuthApi);
  const router = inject(Router);

  return authApi.whoAmI().pipe(
    map((session) => {
      const step = pendingAccountStep(session);
      return step ? redirectWithReturnUrl(router, step, state) : true;
    }),
    catchError((error: unknown) =>
      of(isUnauthorized(error) ? redirectWithReturnUrl(router, loginPath, state) : true),
    ),
  );
};

/**
 * The two-factor setup page needs a session and a finished password change, but not finished enrolment —
 * otherwise the page that exists to complete enrolment would redirect away from itself (W1-10).
 */
export const twoFactorSetupGuard: CanActivateFn = (_route, state) => {
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

/** True if any character in `value` is an ASCII control character (TAB, CR, LF among them). The WHATWG URL
 * parser strips these anywhere in a URL before resolving it, so `/` + TAB + `/evil.example` collapses to
 * `//evil.example` (a protocol-relative redirect) in any context that does real browser URL resolution, even
 * though it doesn't start with a second literal `/` or `\` as written (tester finding, W1-11, 2026-09-30).
 * Checked by character code, not a regex, so the control characters themselves never appear literally in
 * source (a bare control-character regex is also an ESLint `no-control-regex` violation). */
function hasControlCharacter(value: string): boolean {
  return Array.from(value).some((char) => char.charCodeAt(0) <= 0x1f);
}

/**
 * Only ever accepts a same-origin, in-app path (starts with a single `/`) — never a scheme-relative or
 * protocol-carrying value (`//evil.example`, `https://…`, or one hiding a second slash/backslash behind an
 * embedded control character) that could turn "take me back where I was" into an open redirect. Anything
 * else, including a missing or empty param, falls back to the caller's default.
 */
export function sanitizeReturnUrl(value: string | null | undefined): string | null {
  if (!value || hasControlCharacter(value) || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
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
