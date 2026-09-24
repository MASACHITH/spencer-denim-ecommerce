import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Params } from '@angular/router';
import { Observable } from 'rxjs';

import { PagedResult, ProductCardDto, ValidationProblemDetails } from './catalog.model';
import { DepartmentDto } from '../navigation/catalog-departments.model';

/**
 * The read-only catalogue API (`/api/catalog`). One place that knows the endpoint names, so a
 * component never builds a URL.
 *
 * The base URL is supplied by `apiBaseUrlInterceptor`: empty in the browser (same origin behind
 * the reverse proxy), the internal API address during server-side rendering.
 */
@Injectable({ providedIn: 'root' })
export class CatalogApi {
  private readonly http = inject(HttpClient);

  /** Departments with their category tree (GLB-05 mega menu, PLP-01 breadcrumb, DEPT-01). */
  departments(): Observable<readonly DepartmentDto[]> {
    return this.http.get<readonly DepartmentDto[]>('/api/catalog/departments');
  }

  /** One page of the listing (PLP-01..07). `params` comes from `toRequestParams`. */
  products(params: Params): Observable<PagedResult<ProductCardDto>> {
    return this.http.get<PagedResult<ProductCardDto>>('/api/catalog/products', { params });
  }
}

/**
 * Turns a failed listing request into something a shopper can act on.
 *
 * The API answers a bad query string with RFC 9457 validation problems keyed by parameter name —
 * an ambiguous category slug is a real one: `catalog.Categories.Slug` is unique per department
 * and parent, so "jeans" on its own names two categories and the API refuses to guess. The
 * message it writes already says what to add, so it is shown as-is rather than replaced with a
 * generic apology. Anything else (offline, 500, a proxy's HTML error page) gets a plain sentence,
 * never a status code or a stack.
 */
export function listingErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return GENERIC_ERROR;
  }

  if (error.status === 400) {
    const detail = firstValidationMessage(error.error);
    if (detail) {
      return detail;
    }

    return 'That link asks for something this catalogue cannot show. Clear the filters to start again.';
  }

  if (error.status === 0) {
    return 'We could not reach the store. Check your connection and try again.';
  }

  return GENERIC_ERROR;
}

const GENERIC_ERROR =
  'We could not load these products just now. Please refresh the page or try again in a moment.';

function firstValidationMessage(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const errors = (body as ValidationProblemDetails).errors;
  if (!errors) {
    return null;
  }

  for (const messages of Object.values(errors)) {
    const message = messages?.find((candidate) => typeof candidate === 'string' && candidate.trim());
    if (message) {
      return message;
    }
  }

  return null;
}
