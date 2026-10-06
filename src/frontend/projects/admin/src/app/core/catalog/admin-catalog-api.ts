import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  AdminCategoryTree,
  AdminDepartment,
  CategoryDeactivationImpact,
  CategoryFormOptions,
  CategoryFormValues,
  DepartmentFormValues,
  ValidationProblemDetails,
} from './admin-catalog.model';

/** The id of a category that has just been created. */
interface CreatedCategory {
  readonly id: number;
}

/**
 * `/api/admin/catalog` — the departments and the category tree (ADM-11, W1-12).
 *
 * Every mutating call carries a freshly fetched antiforgery token, through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place.
 */
@Injectable({ providedIn: 'root' })
export class AdminCatalogApi {
  static readonly departmentsUrl = '/api/admin/catalog/departments';
  static readonly categoriesUrl = '/api/admin/catalog/categories';
  static readonly formOptionsUrl = '/api/admin/catalog/category-form-options';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  departments(): Observable<AdminDepartment[]> {
    return this.http.get<AdminDepartment[]>(AdminCatalogApi.departmentsUrl);
  }

  categoryTree(departmentId: number): Observable<AdminCategoryTree> {
    return this.http.get<AdminCategoryTree>(
      `${AdminCatalogApi.departmentsUrl}/${departmentId}/categories`,
    );
  }

  /** The age groups and size guides a category form may choose from (doc 11 P1 step 2). */
  categoryFormOptions(): Observable<CategoryFormOptions> {
    return this.http.get<CategoryFormOptions>(AdminCatalogApi.formOptionsUrl);
  }

  deactivationImpact(categoryId: number): Observable<CategoryDeactivationImpact> {
    return this.http.get<CategoryDeactivationImpact>(
      `${AdminCatalogApi.categoriesUrl}/${categoryId}/deactivation-impact`,
    );
  }

  updateDepartment(
    departmentId: number,
    values: DepartmentFormValues,
    rowVersion: string,
  ): Observable<void> {
    return this.put(`${AdminCatalogApi.departmentsUrl}/${departmentId}`, { ...values, rowVersion });
  }

  setDepartmentActive(
    departmentId: number,
    isActive: boolean,
    rowVersion: string,
  ): Observable<void> {
    return this.put(`${AdminCatalogApi.departmentsUrl}/${departmentId}/active`, {
      isActive,
      rowVersion,
    });
  }

  /** The whole list, in order. A partial list is refused by the API (`stale_order`). */
  reorderDepartments(orderedDepartmentIds: readonly number[]): Observable<void> {
    return this.put(`${AdminCatalogApi.departmentsUrl}/order`, { orderedDepartmentIds });
  }

  createCategory(departmentId: number, values: CategoryFormValues): Observable<CreatedCategory> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<CreatedCategory>(
        AdminCatalogApi.categoriesUrl,
        { departmentId, ...values },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }

  /** Changing `parentCategoryId` moves the category and re-paths its subtree (doc 11 P1 step 4). */
  updateCategory(
    categoryId: number,
    values: CategoryFormValues,
    rowVersion: string,
  ): Observable<void> {
    return this.put(`${AdminCatalogApi.categoriesUrl}/${categoryId}`, { ...values, rowVersion });
  }

  setCategoryActive(categoryId: number, isActive: boolean, rowVersion: string): Observable<void> {
    return this.put(`${AdminCatalogApi.categoriesUrl}/${categoryId}/active`, {
      isActive,
      rowVersion,
    });
  }

  /** One parent's children, all of them, in order. `parentCategoryId` is null for the top level. */
  reorderCategories(
    departmentId: number,
    parentCategoryId: number | null,
    orderedCategoryIds: readonly number[],
  ): Observable<void> {
    return this.put(`${AdminCatalogApi.categoriesUrl}/order`, {
      departmentId,
      parentCategoryId,
      orderedCategoryIds,
    });
  }

  /**
   * Removes a category (a soft delete). The row version travels in `If-Match`, because a DELETE's
   * precondition belongs in a header rather than a body.
   */
  removeCategory(categoryId: number, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminCatalogApi.categoriesUrl}/${categoryId}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': `"${rowVersion}"` },
      }),
    );
  }

  private put(url: string, body: unknown): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<void>(url, body, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }
}

/**
 * What went wrong, as the screen has to treat it rather than as a status code.
 *
 * - `forbidden`: signed in without the permission for this action.
 * - `validation`: a field the API rejected; the messages are in `fieldErrors`, keyed by field name.
 * - `conflict`: well-formed and still impossible — a stale row version, a stale order, a category
 *   something still points at. The screen must reload rather than ask the admin to fix a field.
 * - `unprocessable`: the right kind of request about a file the server cannot use (422) or one over its
 *   size ceiling (413). The server's sentence names what to do about it, so it is shown verbatim.
 * - `notFound`: the row has gone since the page was loaded.
 * - `offline`: the browser could not reach the API (status 0).
 * - `unknown`: anything else.
 */
export type CatalogFailureKind =
  | 'forbidden'
  | 'validation'
  | 'conflict'
  | 'unprocessable'
  | 'notFound'
  | 'offline'
  | 'unknown';

export interface CatalogFailure {
  readonly kind: CatalogFailureKind;
  readonly message: string;
  /** The machine-readable `code` the API puts beside the sentence, when there is one. */
  readonly code?: string;
  readonly fieldErrors?: Record<string, string[]>;
  /** Extra facts from a conflict body, e.g. the counts behind a refused removal. */
  readonly details?: Record<string, unknown>;
}

const GENERIC = 'Something went wrong. Please try again in a moment.';

/** Classifies a failed call to `/api/admin/catalog`. */
export function toCatalogFailure(error: unknown): CatalogFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'unknown', message: GENERIC };
  }

  const body = (error.error ?? null) as (ValidationProblemDetails & Record<string, unknown>) | null;
  const code = typeof body?.['code'] === 'string' ? (body['code'] as string) : undefined;
  const detail = typeof body?.['detail'] === 'string' ? (body['detail'] as string) : undefined;

  if (error.status === 403) {
    // Not "to make this change": this classifier is used for reads as well, and a viewer who lacks
    // Products.View was being told they could not change something they were only looking at.
    return { kind: 'forbidden', message: 'You do not have permission to do that.' };
  }

  if (error.status === 400 && body?.errors) {
    return {
      kind: 'validation',
      message: body.title ?? 'One or more fields need correcting.',
      fieldErrors: body.errors,
      code,
    };
  }

  if (error.status === 400 || error.status === 415) {
    return { kind: 'unknown', message: detail ?? GENERIC, code };
  }

  if (error.status === 404) {
    return {
      kind: 'notFound',
      message: detail ?? 'That item is no longer there. Reload the page.',
      code,
    };
  }

  if (error.status === 409) {
    return { kind: 'conflict', message: detail ?? GENERIC, code, details: body ?? undefined };
  }

  // 422 and 413 are the media library's two most useful refusals, and both were being thrown away.
  //
  // `POST /api/admin/media/videos` answers **422** with a sentence an admin can act on — "The MP4's video
  // track is 'hvc1', not H.264 (avc1)… Export it as H.264." **iPhone records HEVC by default**, so for a
  // client shooting product video on a phone this is the first thing that happens; without the branch it
  // read "Something went wrong" and gave no hint the file needed re-encoding. **413** is the same story
  // for a file over the ceiling. The server's own words are better than anything this function could
  // invent, so they are passed straight through.
  if (error.status === 422 || error.status === 413) {
    return { kind: 'unprocessable', message: detail ?? GENERIC, code };
  }

  if (error.status === 0) {
    return {
      kind: 'offline',
      message: 'We could not reach the server. Check your connection and try again.',
    };
  }

  return { kind: 'unknown', message: GENERIC };
}
