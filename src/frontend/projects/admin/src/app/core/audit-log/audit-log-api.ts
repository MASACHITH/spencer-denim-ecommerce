import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AuditLogEntry, AuditLogFilters, PagedResult, ValidationProblemDetails } from './audit-log.model';

/** The read-only audit log API (`/api/admin/audit-logs`, ADM-03, W1-09). */
@Injectable({ providedIn: 'root' })
export class AuditLogApi {
  static readonly url = '/api/admin/audit-logs';

  private readonly http = inject(HttpClient);

  /** One page of the audit log, filtered server-side and sorted `OccurredAtUtc DESC`. */
  list(filters: AuditLogFilters, page: number, pageSize: number): Observable<PagedResult<AuditLogEntry>> {
    return this.http.get<PagedResult<AuditLogEntry>>(AuditLogApi.url, {
      params: toParams(filters, page, pageSize),
    });
  }
}

/** Builds the query string, leaving blank filters out entirely rather than sending an empty value. */
function toParams(filters: AuditLogFilters, page: number, pageSize: number): HttpParams {
  let params = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));

  for (const [key, value] of Object.entries(filters)) {
    const trimmed = value.trim();
    if (trimmed) {
      params = params.set(key, trimmed);
    }
  }

  return params;
}

/**
 * What went wrong with a request, as the page has to treat it (not as a status code).
 *
 * - `forbidden`: signed in, but without `AuditLog.View`.
 * - `validation`: a filter value the API rejected (the field messages are in `fieldErrors`).
 * - `offline`: the browser could not reach the API (status 0).
 * - `unknown`: anything else (a 500, a proxy's HTML error page).
 */
export type AuditLogFailureKind = 'forbidden' | 'validation' | 'offline' | 'unknown';

export interface AuditLogFailure {
  readonly kind: AuditLogFailureKind;
  readonly message: string;
  readonly fieldErrors?: Record<string, string[]>;
}

const GENERIC_ERROR = 'We could not load the audit log just now. Please try again in a moment.';

/** Classifies a failed `GET /api/admin/audit-logs`. */
export function toAuditLogFailure(error: unknown): AuditLogFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'unknown', message: GENERIC_ERROR };
  }

  if (error.status === 403) {
    return {
      kind: 'forbidden',
      message: 'You do not have permission to view the audit log.',
    };
  }

  if (error.status === 400) {
    const body = error.error as ValidationProblemDetails | null;
    return {
      kind: 'validation',
      message: body?.title ?? 'One or more filters are invalid.',
      fieldErrors: body?.errors,
    };
  }

  if (error.status === 0) {
    return { kind: 'offline', message: 'We could not reach the server. Check your connection and try again.' };
  }

  return { kind: 'unknown', message: GENERIC_ERROR };
}
