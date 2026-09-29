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
 * Admin login, logout, "who am I" and change-password (doc 03 §3a; ADM-01; issue #17). Session state is the
 * HttpOnly admin cookie itself — this service does not cache anything client-side.
 */
@Injectable({ providedIn: 'root' })
export class AdminAuthApi {
  static readonly loginUrl = '/api/admin/auth/login';
  static readonly logoutUrl = '/api/admin/auth/logout';
  static readonly whoAmIUrl = '/api/admin/auth/whoami';
  static readonly antiforgeryUrl = '/api/admin/auth/antiforgery-token';
  static readonly changePasswordUrl = '/api/admin/auth/change-password';

  private readonly http = inject(HttpClient);

  login(email: string, password: string): Observable<AdminSession> {
    return this.http
      .post<AdminSession>(AdminAuthApi.loginUrl, { email, password })
      .pipe(catchError((error: HttpErrorResponse) => throwError(() => toLoginError(error))));
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

  private withAntiforgeryToken<T>(send: (token: string) => Observable<T>): Observable<T> {
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
