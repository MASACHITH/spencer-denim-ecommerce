import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';

import { twoFactorEnrolmentRequiredCode } from './admin-auth-api';
import { twoFactorSetupPath } from './auth-guards';

/**
 * W1-10: when any API call is refused with 403 `two_factor_enrolment_required` (a Super Admin who has not set up
 * two-factor authentication, e.g. just given the role while this page was open), send the user to the enrolment
 * page. The same pattern as the password-change interceptor (issue #17); the error is still passed on.
 */
export const twoFactorEnrolmentRequiredInterceptor: HttpInterceptorFn = (request, next) => {
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (isTwoFactorEnrolmentRequired(error) && !router.url.startsWith(twoFactorSetupPath)) {
        void router.navigateByUrl(twoFactorSetupPath);
      }

      return throwError(() => error);
    }),
  );
};

function isTwoFactorEnrolmentRequired(error: unknown): boolean {
  if (!(error instanceof HttpErrorResponse) || error.status !== 403) {
    return false;
  }

  const body = error.error as { error?: string } | null;
  return body?.error === twoFactorEnrolmentRequiredCode;
}
