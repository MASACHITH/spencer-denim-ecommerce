import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Params } from '@angular/router';
import { Observable } from 'rxjs';

import {
  PagedResult,
  ProductCardDto,
  ProductDetailDto,
  ValidationProblemDetails,
} from './catalog.model';
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

  /**
   * One product page (PDP-01..06).
   *
   * The slug is encoded rather than trusted: it arrives from the address bar, and a segment
   * carrying a `?` or a `#` would otherwise change which resource is asked for. The API rejects
   * anything that is not a slug with a 400 naming `slug`, which {@link productFailure} reads as
   * "this address is not a product" rather than as a fault.
   */
  product(slug: string): Observable<ProductDetailDto> {
    return this.http.get<ProductDetailDto>(`/api/catalog/products/${encodeURIComponent(slug)}`);
  }
}

/**
 * What went wrong with a product request, as the page has to treat it (not as a status code).
 *
 * - `missing`: there is no such product. The API answers **404** for a well-formed slug it does
 *   not know and **400** for something that is not a slug at all, and both mean the same thing to
 *   a shopper who followed a stale link: this address is not a product. Both become a real 404.
 * - `offline`: the browser could not reach the store (status 0).
 * - `failed`: the store answered, badly — a 500, a 502 from the proxy, an HTML error page. The
 *   product may well exist, so the page says "try again" and the server answers 503, which a
 *   crawler retries instead of dropping the URL.
 */
export type ProductFailure = 'missing' | 'offline' | 'failed';

/** Classifies a failed `GET /api/catalog/products/{slug}`. */
export function productFailure(error: unknown): ProductFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return 'failed';
  }

  if (error.status === 400 || error.status === 404) {
    return 'missing';
  }

  return error.status === 0 ? 'offline' : 'failed';
}

/** A sentence for the shopper. `missing` has no message: it is a page, not a banner. */
export function productErrorMessage(failure: ProductFailure): string {
  return failure === 'offline'
    ? 'We could not reach the store. Check your connection and try again.'
    : 'We could not load this product just now. Please try again in a moment.';
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
