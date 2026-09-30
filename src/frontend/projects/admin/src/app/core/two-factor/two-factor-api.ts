import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';

/** `GET /api/admin/auth/two-factor` (W1-10). */
export interface TwoFactorStatus {
  enabled: boolean;
  /** Mandatory for this account (a Super Admin). */
  required: boolean;
  /** The user may turn it off themselves (it is on, and not mandatory). */
  canDisable: boolean;
  recoveryCodesLeft: number;
}

/** What the enrolment page shows: the key to type in by hand, and the URI its QR code encodes. */
export interface TwoFactorSetup {
  sharedKey: string;
  authenticatorUri: string;
}

export type TwoFactorErrorKind =
  | 'invalid_code'
  | 'invalid_current_password'
  | 'locked_out'
  | 'two_factor_already_enabled'
  | 'two_factor_not_enabled'
  | 'two_factor_not_set_up'
  | 'two_factor_required'
  | 'session_expired'
  | 'unknown';

export interface TwoFactorError {
  kind: TwoFactorErrorKind;
  message: string;
  lockoutEndUtc?: string;
}

interface ProblemBody {
  error?: string;
  detail?: string;
  lockoutEndUtc?: string;
}

const knownKinds: readonly TwoFactorErrorKind[] = [
  'invalid_code',
  'invalid_current_password',
  'locked_out',
  'two_factor_already_enabled',
  'two_factor_not_enabled',
  'two_factor_not_set_up',
  'two_factor_required',
];

/**
 * The signed-in admin's own two-factor authentication (W1-10): status, enrolment with an authenticator app, new
 * recovery codes and turning it off. Every change is a POST with the antiforgery token. Keys and recovery codes
 * are only ever held in the page that shows them; nothing here stores them.
 */
@Injectable({ providedIn: 'root' })
export class TwoFactorApi {
  static readonly statusUrl = '/api/admin/auth/two-factor';
  static readonly setupUrl = '/api/admin/auth/two-factor/setup';
  static readonly enableUrl = '/api/admin/auth/two-factor/enable';
  static readonly recoveryCodesUrl = '/api/admin/auth/two-factor/recovery-codes';
  static readonly disableUrl = '/api/admin/auth/two-factor/disable';

  private readonly http = inject(HttpClient);
  private readonly authApi = inject(AdminAuthApi);

  status(): Observable<TwoFactorStatus> {
    return this.http.get<TwoFactorStatus>(TwoFactorApi.statusUrl).pipe(catchError(rethrowAsTwoFactorError));
  }

  /** Starts, or resumes, enrolment: the same key comes back until it is confirmed. */
  setup(): Observable<TwoFactorSetup> {
    return this.authApi.postWithAntiforgeryToken<TwoFactorSetup>(TwoFactorApi.setupUrl, null).pipe(catchError(rethrowAsTwoFactorError));
  }

  /** Confirms enrolment with a code from the app. Returns the recovery codes, which are never shown again. */
  enable(code: string): Observable<string[]> {
    return this.authApi
      .postWithAntiforgeryToken<{ recoveryCodes: string[] }>(TwoFactorApi.enableUrl, { code })
      .pipe(
        map((response) => response.recoveryCodes),
        catchError(rethrowAsTwoFactorError),
      );
  }

  regenerateRecoveryCodes(currentPassword: string): Observable<string[]> {
    return this.authApi
      .postWithAntiforgeryToken<{ recoveryCodes: string[] }>(TwoFactorApi.recoveryCodesUrl, { currentPassword })
      .pipe(
        map((response) => response.recoveryCodes),
        catchError(rethrowAsTwoFactorError),
      );
  }

  disable(currentPassword: string): Observable<void> {
    return this.authApi
      .postWithAntiforgeryToken<void>(TwoFactorApi.disableUrl, { currentPassword })
      .pipe(catchError(rethrowAsTwoFactorError));
  }
}

function rethrowAsTwoFactorError(error: unknown): Observable<never> {
  return throwError(() => toTwoFactorError(error));
}

export function toTwoFactorError(error: unknown): TwoFactorError {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'unknown', message: 'Something went wrong. Please try again.' };
  }

  if (error.status === 401) {
    return { kind: 'session_expired', message: 'Your session has ended. Please sign in again.' };
  }

  const body = error.error as ProblemBody | null;
  const kind = knownKinds.find((known) => known === body?.error);
  if (kind) {
    return { kind, message: body?.detail ?? 'Something went wrong. Please try again.', lockoutEndUtc: body?.lockoutEndUtc };
  }

  return { kind: 'unknown', message: 'Something went wrong. Please try again.' };
}
