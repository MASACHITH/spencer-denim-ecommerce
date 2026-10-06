import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  AdminProductDetail,
  AdminRelatedProduct,
  PagedProducts,
  ProductFormOptions,
  ProductFormValues,
  UploadedAsset,
} from './admin-product.model';

/** The id of a product that has just been created. */
interface CreatedProduct {
  readonly id: number;
}

/** What the list is asking for. Every field optional: the default list is "everything, newest edits first". */
export interface ProductListQuery {
  readonly term?: string | null;
  readonly departmentId?: number | null;
  readonly categoryId?: number | null;
  readonly published?: boolean | null;
  readonly sort?: string | null;
  readonly page?: number;
  readonly pageSize?: number;
}

/**
 * `/api/admin/products` — the ADM-10 product screens (W1-03).
 *
 * Every mutating call carries a freshly fetched antiforgery token, through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place.
 *
 * **Media is uploaded through the library, not through these routes.** `POST /api/admin/media/images` and
 * `/videos` already exist (W1-04) and already check the file by signature rather than by name, cap a video
 * at 50 MB, insist on a poster and refuse an MP4 with no H.264 track. The product form sends the
 * `publicId` they hand back — see {@link uploadImage} and {@link uploadVideo}.
 */
@Injectable({ providedIn: 'root' })
export class AdminProductApi {
  static readonly productsUrl = '/api/admin/products';
  static readonly mediaImagesUrl = '/api/admin/media/images';
  static readonly mediaVideosUrl = '/api/admin/media/videos';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  list(query: ProductListQuery = {}): Observable<PagedProducts> {
    const params: Record<string, string> = {};

    if (query.term?.trim()) {
      params['term'] = query.term.trim();
    }

    if (query.departmentId != null) {
      params['departmentId'] = String(query.departmentId);
    }

    if (query.categoryId != null) {
      params['categoryId'] = String(query.categoryId);
    }

    if (query.published != null) {
      params['published'] = String(query.published);
    }

    if (query.sort) {
      params['sort'] = query.sort;
    }

    if (query.page) {
      params['page'] = String(query.page);
    }

    if (query.pageSize) {
      params['pageSize'] = String(query.pageSize);
    }

    return this.http.get<PagedProducts>(AdminProductApi.productsUrl, { params });
  }

  get(productId: number): Observable<AdminProductDetail> {
    return this.http.get<AdminProductDetail>(`${AdminProductApi.productsUrl}/${productId}`);
  }

  formOptions(): Observable<ProductFormOptions> {
    return this.http.get<ProductFormOptions>(`${AdminProductApi.productsUrl}/form-options`);
  }

  searchForRelated(term: string, excludeProductId: number | null): Observable<AdminRelatedProduct[]> {
    const params: Record<string, string> = { term };

    if (excludeProductId != null) {
      params['excludeProductId'] = String(excludeProductId);
    }

    return this.http.get<AdminRelatedProduct[]>(`${AdminProductApi.productsUrl}/related-search`, {
      params,
    });
  }

  create(values: ProductFormValues): Observable<CreatedProduct> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<CreatedProduct>(AdminProductApi.productsUrl, values, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /**
   * Saves a product.
   *
   * `values.media` is passed through exactly as it arrives: **absent** when the form is not editing the
   * gallery, and an array when it is. `JSON.stringify` drops an `undefined` property, so an absent field
   * really does reach the API as "say nothing about media" — which is what stops a save from clearing the
   * photographs (the W1-12 defect).
   */
  update(productId: number, values: ProductFormValues, rowVersion: string): Observable<void> {
    const body: Record<string, unknown> = { ...values, rowVersion };

    // The key is *deleted*, not left holding `undefined`. `JSON.stringify` would drop it either way, so
    // on today's wire the two are identical — but "the property is absent" is the contract the API reads,
    // and leaving a property whose value happens to serialise away makes that contract depend on the
    // serialiser rather than on this line. A reader checking whether a save can clear the gallery should
    // be able to see the answer here.
    if (values.media === undefined) {
      delete body['media'];
    }

    return this.put(`${AdminProductApi.productsUrl}/${productId}`, body);
  }

  setPublished(productId: number, isPublished: boolean, rowVersion: string): Observable<void> {
    return this.put(`${AdminProductApi.productsUrl}/${productId}/published`, {
      isPublished,
      rowVersion,
    });
  }

  /** A soft delete. The row version travels in `If-Match`: a DELETE's precondition belongs in a header. */
  remove(productId: number, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminProductApi.productsUrl}/${productId}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': `"${rowVersion}"` },
      }),
    );
  }

  /** Uploads a photograph to the media library and returns the asset the product form will reference. */
  uploadImage(file: File, altText: string | null): Observable<UploadedAsset> {
    const form = new FormData();
    form.append('file', file, file.name);
    AdminProductApi.appendAltText(form, altText);

    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<UploadedAsset>(AdminProductApi.mediaImagesUrl, form, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /**
   * Uploads the product video and its poster.
   *
   * Both parts are required by the library, because `media.MediaAssets` cannot hold a video row without a
   * poster (doc 08 §3.3's check constraint) and nothing on this server extracts a frame — the VPS has two
   * vCPUs (doc 03 §3b). The limits are the library's: MP4 only, checked by file signature, at most 50 MB.
   */
  uploadVideo(file: File, poster: File, altText: string | null): Observable<UploadedAsset> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('poster', poster, poster.name);
    AdminProductApi.appendAltText(form, altText);

    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<UploadedAsset>(AdminProductApi.mediaVideosUrl, form, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /**
   * Sends `altText` only when there is something to send.
   *
   * The library takes the field as optional, and an empty one would store an empty string where
   * the publish checklist (doc 11 §7 G-01) reads "has alt text". A product upload therefore
   * sends none, so the asset arrives undescribed and the checklist's third rule can block.
   */
  private static appendAltText(form: FormData, altText: string | null): void {
    const described = (altText ?? '').trim();

    if (described.length > 0) {
      form.append('altText', described);
    }
  }

  private put(url: string, body: unknown): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<void>(url, body, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }
}

/** What the library takes for a product photograph (checked there by signature, not by name). */
export const IMAGE_ACCEPT = 'image/png,image/jpeg,image/webp';

/** What the library takes for a product video (doc 08 §3.3, doc 11 P1 step 11). */
export const VIDEO_ACCEPT = 'video/mp4';

/**
 * The size this screen refuses before it sends anything.
 *
 * 50 MB is `MediaLimits.MaxVideoBytes` and the ceiling of `CK_MediaAssets_SizeBytes`, so a larger file
 * would be refused by the API anyway — after the admin had uploaded it over a Sri Lankan mobile
 * connection. Checking here turns that into an instant answer.
 */
export const MAX_VIDEO_BYTES = 50 * 1024 * 1024;

/** The same argument, for a photograph. The library's own ceiling is higher. */
export const MAX_IMAGE_BYTES = 8 * 1024 * 1024;
