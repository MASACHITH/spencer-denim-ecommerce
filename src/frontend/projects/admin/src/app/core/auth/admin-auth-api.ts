import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, switchMap, throwError } from 'rxjs';

/** Response of `POST /api/admin/auth/login` and `GET /api/admin/auth/whoami` (ADM-01). */
export interface AdminSession {
  userId: number;
  email: string;
  firstName: string;
  lastName: string | null;
  grantsAllPermissions: boolean;
  permissions: string[];
  /** Issue #17: the API refuses every other admin endpoint until the password is changed. */
  mustChangePassword: boolean;
  /** W1-10: an authenticator app is set up for this account. */
  twoFactorEnabled: boolean;
  /**
   * W1-10: a Super Admin without two-factor authentication. The API refuses every other admin endpoint until
   * they set it up (after any forced password change).
   */
  twoFactorEnrolmentRequired: boolean;
  /** Only on the sign-in reply: always false for a completed sign-in. */
  twoFactorRequired?: false;
}

/** W1-10: the password was right, and a code from the authenticator app is needed to finish signing in. */
export interface TwoFactorChallenge {
  twoFactorRequired: true;
}

export type AdminLoginReply = AdminSession | TwoFactorChallenge;

export type TwoFactorLoginErrorKind =
  | 'invalid_two_factor_code'
  | 'two_factor_session_expired'
  | 'locked_out'
  | 'unknown';

export interface TwoFactorLoginError {
  kind: TwoFactorLoginErrorKind;
  message: string;
  lockoutEndUtc?: string;
}

export type AdminLoginErrorKind = 'invalid_credentials' | 'locked_out' | 'unknown';

export interface AdminLoginError {
  kind: AdminLoginErrorKind;
  message: string;
  lockoutEndUtc?: string;
}

export type ChangePasswordErrorKind =
  | 'invalid_current_password'
  | 'new_password_rejected'
  | 'locked_out'
  | 'session_expired'
  | 'unknown';

export interface ChangePasswordError {
  kind: ChangePasswordErrorKind;
  message: string;
  /** The password rules the new password broke, one message per rule. */
  rules?: string[];
  lockoutEndUtc?: string;
}

/** The `error` code the API puts on a 403 while a forced password change is pending (issue #17). */
export const passwordChangeRequiredCode = 'password_change_required';

/** The `error` code the API puts on a 403 while a Super Admin has not set up two-factor authentication (W1-10). */
export const twoFactorEnrolmentRequiredCode = 'two_factor_enrolment_required';

interface AdminLoginErrorBody {
  error?: string;
  message?: string;
  lockoutEndUtc?: string;
}

/** Problem-details body of the change-password endpoint's errors. */
interface ChangePasswordErrorBody {
  error?: string;
  detail?: string;
  lockoutEndUtc?: string;
  errors?: Record<string, string[]>;
}

interface AntiforgeryTokenResponse {
  token: string;
}

/**
 * Admin login (both steps when the account uses two-factor authentication, W1-10), logout, "who am I" and
 * change-password (doc 03 §3a; ADM-01; issue #17). Session state is the HttpOnly admin cookie itself — this
 * service does not cache anything client-side.
 */
@Injectable({ providedIn: 'root' })
export class AdminAuthApi {
  static readonly loginUrl = '/api/admin/auth/login';
  static readonly loginTwoFactorUrl = '/api/admin/auth/login/two-factor';
  static readonly logoutUrl = '/api/admin/auth/logout';
  static readonly whoAmIUrl = '/api/admin/auth/whoami';
  static readonly antiforgeryUrl = '/api/admin/auth/antiforgery-token';
  static readonly changePasswordUrl = '/api/admin/auth/change-password';

  private readonly http = inject(HttpClient);

  /**
   * The password step. For an account with two-factor authentication the reply is a {@link TwoFactorChallenge}
   * and no session exists yet: finish with {@link completeTwoFactorLogin}.
   */
  login(email: string, password: string): Observable<AdminLoginReply> {
    return this.http
      .post<AdminLoginReply>(AdminAuthApi.loginUrl, { email, password })
      .pipe(catchError((error: HttpErrorResponse) => throwError(() => toLoginError(error))));
  }

  /** W1-10: the second sign-in step, with a code from the authenticator app or one of the recovery codes. */
  completeTwoFactorLogin(code: string, isRecoveryCode: boolean): Observable<AdminSession> {
    return this.postWithAntiforgeryToken<AdminSession>(AdminAuthApi.loginTwoFactorUrl, { code, isRecoveryCode }).pipe(
      catchError((error: HttpErrorResponse) => throwError(() => toTwoFactorLoginError(error))),
    );
  }

  whoAmI(): Observable<AdminSession> {
    return this.http.get<AdminSession>(AdminAuthApi.whoAmIUrl);
  }

  /** Fetches a fresh antiforgery token first (doc 03 §3a "antiforgery"), then signs out. */
  logout(): Observable<void> {
    return this.withAntiforgeryToken((token) =>
      this.http.post<void>(AdminAuthApi.logoutUrl, null, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }

  /**
   * Changes the signed-in user's own password. On success the API re-issues this session's cookie, so the
   * caller stays signed in; every other session for the account ends within a minute.
   */
  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.withAntiforgeryToken((token) =>
      this.http.post<void>(
        AdminAuthApi.changePasswordUrl,
        { currentPassword, newPassword },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    ).pipe(catchError((error: HttpErrorResponse) => throwError(() => toChangePasswordError(error))));
  }

  /** A POST with a freshly fetched antiforgery token, as every mutating admin call needs. */
  postWithAntiforgeryToken<T>(url: string, body: unknown): Observable<T> {
    return this.withAntiforgeryToken((token) => this.http.post<T>(url, body, { headers: { 'X-XSRF-TOKEN': token } }));
  }

  /**
   * Runs a request with a freshly fetched antiforgery token.
   *
   * Public since W1-12: every mutating admin call needs the token, and ADM-11's writes are PUTs and a
   * DELETE rather than POSTs, so `postWithAntiforgeryToken` does not cover them. A second service fetching
   * its own token would be a second place for the header name and the token endpoint to drift.
   */
  withAntiforgeryToken<T>(send: (token: string) => Observable<T>): Observable<T> {
    return this.http
      .get<AntiforgeryTokenResponse>(AdminAuthApi.antiforgeryUrl)
      .pipe(switchMap((tokenResponse) => send(tokenResponse.token)));
  }
}

function toLoginError(error: HttpErrorResponse): AdminLoginError {
  const body = error.error as AdminLoginErrorBody | null;

  if (error.status === 423) {
    return {
      kind: 'locked_out',
      message: body?.message ?? 'This account is temporarily locked. Try again later.',
      lockoutEndUtc: body?.lockoutEndUtc,
    };
  }

  if (error.status === 401) {
    return { kind: 'invalid_credentials', message: body?.message ?? 'Invalid email or password.' };
  }

  return { kind: 'unknown', message: 'Something went wrong. Please try again.' };
}

function toTwoFactorLoginError(error: HttpErrorResponse): TwoFactorLoginError {
  const body = error.error as AdminLoginErrorBody | null;

  if (error.status === 423) {
    return {
      kind: 'locked_out',
      message: body?.message ?? 'This account is temporarily locked. Try again later.',
      lockoutEndUtc: body?.lockoutEndUtc,
    };
  }

  if (error.status === 401 && body?.error === 'invalid_two_factor_code') {
    return { kind: 'invalid_two_factor_code', message: 'That code is not valid. Check it and try again.' };
  }

  if (error.status === 401) {
    return {
      kind: 'two_factor_session_expired',
      message: 'Your sign-in has expired. Enter your email and password again.',
    };
  }

  return { kind: 'unknown', message: 'Something went wrong. Please try again.' };
}

function toChangePasswordError(error: HttpErrorResponse): ChangePasswordError {
  const body = error.error as ChangePasswordErrorBody | null;

  if (error.status === 401) {
    return { kind: 'session_expired', message: 'Your session has ended. Please sign in again.' };
  }

  if (error.status === 423) {
    return {
      kind: 'locked_out',
      message: body?.detail ?? 'This account is temporarily locked. Try again later.',
      lockoutEndUtc: body?.lockoutEndUtc,
    };
  }

  if (error.status === 400 && body?.error === 'invalid_current_password') {
    return { kind: 'invalid_current_password', message: 'The current password is incorrect.' };
  }

  if (error.status === 400 && body?.error === 'new_password_rejected') {
    return {
      kind: 'new_password_rejected',
      message: 'The new password does not meet the password rules.',
      rules: body.errors?.['newPassword'] ?? [],
    };
  }

  return { kind: 'unknown', message: 'Something went wrong. Please try again.' };
}
