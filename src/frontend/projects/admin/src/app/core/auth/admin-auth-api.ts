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
}

export type AdminLoginErrorKind = 'invalid_credentials' | 'locked_out' | 'unknown';

export interface AdminLoginError {
  kind: AdminLoginErrorKind;
  message: string;
  lockoutEndUtc?: string;
}

interface AdminLoginErrorBody {
  error?: string;
  message?: string;
  lockoutEndUtc?: string;
}

interface AntiforgeryTokenResponse {
  token: string;
}

/**
 * Admin login, logout and "who am I" (doc 03 §3a; ADM-01). Session state is the HttpOnly admin cookie itself —
 * this service does not cache anything client-side.
 */
@Injectable({ providedIn: 'root' })
export class AdminAuthApi {
  static readonly loginUrl = '/api/admin/auth/login';
  static readonly logoutUrl = '/api/admin/auth/logout';
  static readonly whoAmIUrl = '/api/admin/auth/whoami';
  static readonly antiforgeryUrl = '/api/admin/auth/antiforgery-token';

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
    return this.http.get<AntiforgeryTokenResponse>(AdminAuthApi.antiforgeryUrl).pipe(
      switchMap((tokenResponse) =>
        this.http.post<void>(AdminAuthApi.logoutUrl, null, {
          headers: { 'X-XSRF-TOKEN': tokenResponse.token },
        }),
      ),
    );
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
