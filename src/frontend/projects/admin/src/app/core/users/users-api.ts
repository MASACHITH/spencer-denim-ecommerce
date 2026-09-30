import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

import {
  AssignUserRolesRequest,
  CreateStaffUserRequest,
  PagedResult,
  RoleDetail,
  RoleSummary,
  StaffUserDetail,
  StaffUserListFilters,
  StaffUserSummary,
  UpdateStaffUserProfileRequest,
  ValidationProblemDetails,
} from './users.model';

interface AntiforgeryTokenResponse {
  token: string;
}

/**
 * Users &amp; Roles management (`/api/admin/users`, `/api/admin/roles`, ADM-02, W1-08, issue #8). Every
 * mutating call fetches a fresh antiforgery token first (doc 03 §3a), the same convention
 * `AdminAuthApi.logout`/`changePassword` use — there is no global antiforgery middleware, so each admin
 * endpoint group validates it by hand.
 */
@Injectable({ providedIn: 'root' })
export class UsersApi {
  static readonly usersUrl = '/api/admin/users';
  static readonly rolesUrl = '/api/admin/roles';
  private static readonly antiforgeryUrl = '/api/admin/auth/antiforgery-token';

  private readonly http = inject(HttpClient);

  listUsers(filters: StaffUserListFilters, page: number, pageSize: number): Observable<PagedResult<StaffUserSummary>> {
    return this.http.get<PagedResult<StaffUserSummary>>(UsersApi.usersUrl, { params: toListParams(filters, page, pageSize) });
  }

  getUser(id: number): Observable<StaffUserDetail> {
    return this.http.get<StaffUserDetail>(`${UsersApi.usersUrl}/${id}`);
  }

  listRoles(): Observable<readonly RoleSummary[]> {
    return this.http.get<readonly RoleSummary[]>(UsersApi.rolesUrl);
  }

  getRole(id: number): Observable<RoleDetail> {
    return this.http.get<RoleDetail>(`${UsersApi.rolesUrl}/${id}`);
  }

  // Write methods deliberately return the *raw* HttpErrorResponse (like the read methods above), rather than
  // pre-mapping it to a StaffUserActionFailure here: every caller already needs to call
  // toStaffUserActionFailure(error) itself in its own `error` handler, and mapping twice would run the
  // second pass over an already-mapped object (which is not an HttpErrorResponse) and silently fall through
  // to the generic "unknown" failure — the exact bug this comment now guards against.

  createUser(request: CreateStaffUserRequest): Observable<StaffUserDetail> {
    return this.withAntiforgeryToken((token) =>
      this.http.post<StaffUserDetail>(UsersApi.usersUrl, request, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }

  updateProfile(id: number, request: UpdateStaffUserProfileRequest): Observable<StaffUserDetail> {
    return this.withAntiforgeryToken((token) =>
      this.http.put<StaffUserDetail>(`${UsersApi.usersUrl}/${id}`, request, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }

  assignRoles(id: number, request: AssignUserRolesRequest): Observable<unknown> {
    return this.withAntiforgeryToken((token) =>
      this.http.put(`${UsersApi.usersUrl}/${id}/roles`, request, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }

  deactivate(id: number): Observable<unknown> {
    return this.setActive(id, 'deactivate');
  }

  reactivate(id: number): Observable<unknown> {
    return this.setActive(id, 'reactivate');
  }

  private setActive(id: number, action: 'deactivate' | 'reactivate'): Observable<unknown> {
    return this.withAntiforgeryToken((token) =>
      this.http.post(`${UsersApi.usersUrl}/${id}/${action}`, null, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }

  private withAntiforgeryToken<T>(send: (token: string) => Observable<T>): Observable<T> {
    return this.http
      .get<AntiforgeryTokenResponse>(UsersApi.antiforgeryUrl)
      .pipe(switchMap((tokenResponse) => send(tokenResponse.token)));
  }
}

function toListParams(filters: StaffUserListFilters, page: number, pageSize: number): HttpParams {
  let params = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));

  if (filters.search.trim()) {
    params = params.set('search', filters.search.trim());
  }
  if (filters.roleId.trim()) {
    params = params.set('roleId', filters.roleId.trim());
  }
  if (filters.isActive.trim()) {
    params = params.set('isActive', filters.isActive.trim());
  }

  return params;
}

/**
 * What went wrong with a Users &amp; Roles write, as the page has to treat it — not as a raw status code.
 *
 * - `validation`: a field the API rejected outright (the messages are in `fieldErrors`).
 * - `forbidden`: the doc 08 §3.1 DB4 RBAC safety rules refused this specific change; `reason` is the API's
 *   `RbacDenialReason` code (e.g. `NotSubsetOfActorPermissions`), for copy more specific than the generic message.
 * - `not_found`: the target user, or one of the requested roles, does not exist.
 * - `conflict`: `email_already_in_use` or `last_super_admin_guard` — `reason` tells them apart.
 * - `offline` / `unknown`: as elsewhere in this app.
 */
export type StaffUserActionFailureKind = 'validation' | 'forbidden' | 'not_found' | 'conflict' | 'offline' | 'unknown';

export interface StaffUserActionFailure {
  readonly kind: StaffUserActionFailureKind;
  readonly message: string;
  readonly fieldErrors?: Record<string, string[]>;
  readonly reason?: string;
}

interface ProblemDetailsBody extends ValidationProblemDetails {
  readonly error?: string;
  readonly detail?: string;
  readonly unknownRoleIds?: readonly number[];
}

const GENERIC_ERROR = 'Something went wrong. Please try again.';

const FORBIDDEN_MESSAGES: Record<string, string> = {
  NotAuthorized: 'You do not have permission to manage staff users.',
  SelfEditNotAllowed: 'You cannot change your own role assignments or deactivate your own account.',
  TargetIsOrWouldBecomeGrantsAll: 'Only a Super Admin can manage a Super Admin account or grant the Super Admin role.',
  NotSubsetOfActorPermissions: 'That action grants or affects permissions you do not hold yourself.',
  EmailChangeRequiresGrantsAll: "Only a Super Admin can change a staff user's email address.",
};

export function toStaffUserActionFailure(error: unknown): StaffUserActionFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'unknown', message: GENERIC_ERROR };
  }

  const body = error.error as ProblemDetailsBody | null;

  if (error.status === 400 && body?.errors) {
    return { kind: 'validation', message: body.title ?? 'One or more fields are invalid.', fieldErrors: body.errors };
  }

  if (error.status === 400 && body?.error === 'role_not_found') {
    return { kind: 'not_found', message: 'One or more of the selected roles no longer exists.', reason: body.error };
  }

  if (error.status === 401) {
    return { kind: 'forbidden', message: 'Your session has ended. Sign in again.' };
  }

  if (error.status === 403) {
    const reason = body?.error;
    return {
      kind: 'forbidden',
      message: (reason && FORBIDDEN_MESSAGES[reason]) ?? body?.detail ?? 'You are not allowed to do that.',
      reason,
    };
  }

  if (error.status === 404) {
    return { kind: 'not_found', message: 'That staff user no longer exists.' };
  }

  if (error.status === 409 && body?.error === 'email_already_in_use') {
    return { kind: 'conflict', message: 'Another account already uses this email address.', reason: body.error };
  }

  if (error.status === 409 && body?.error === 'last_super_admin_guard') {
    return {
      kind: 'conflict',
      message: 'This would leave no active Super Admin. Assign the Super Admin role to another active user first.',
      reason: body.error,
    };
  }

  if (error.status === 0) {
    return { kind: 'offline', message: 'We could not reach the server. Check your connection and try again.' };
  }

  return { kind: 'unknown', message: GENERIC_ERROR };
}
