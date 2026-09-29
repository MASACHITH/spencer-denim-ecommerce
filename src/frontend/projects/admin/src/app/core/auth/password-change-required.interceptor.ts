import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';

import { passwordChangeRequiredCode } from './admin-auth-api';
import { changePasswordPath } from './auth-guards';

/**
 * Issue #17: when any API call is refused with 403 `password_change_required` (the flag was set while this
 * page was open, e.g. by a Super Admin reset), send the user to the change-password page. The error is still
 * passed on, so the caller's own error handling runs as usual.
 */
export const passwordChangeRequiredInterceptor: HttpInterceptorFn = (request, next) => {
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (isPasswordChangeRequired(error) && !router.url.startsWith(changePasswordPath)) {
        void router.navigateByUrl(changePasswordPath);
      }

      return throwError(() => error);
    }),
  );
};

function isPasswordChangeRequired(error: unknown): boolean {
  if (!(error instanceof HttpErrorResponse) || error.status !== 403) {
    return false;
  }

  const body = error.error as { error?: string } | null;
  return body?.error === passwordChangeRequiredCode;
}
