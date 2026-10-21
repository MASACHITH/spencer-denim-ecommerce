import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { CatalogFailure, toCatalogFailure } from '../catalog/admin-catalog-api';
import {
  AdminAnnouncement,
  AdminHomeContent,
  AdminSizeGuide,
  AdminSizeGuideListItem,
  AnnouncementRequest,
  AnnouncementSaved,
  HomeItemRequest,
  HomeSaved,
  HomeSectionRequest,
  SizeGuideRequest,
  SizeGuideSaved,
  UploadedMedia,
} from './admin-site-content.model';
import { AdminMegaMenuContent, MegaMenuCardRequest, MegaMenuCardSaved } from './admin-mega-menu.model';

/**
 * The homepage blocks, the announcement bar and the size guides (ADM-17, GLB-03, PDP-04; #192).
 *
 * Every mutating call carries a freshly fetched antiforgery token through {@link AdminAuthApi}. Every
 * body is sent whole — the endpoints refuse one that leaves a key out — and a row version that travels
 * in `If-Match` is quoted, as an ETag is.
 */
@Injectable({ providedIn: 'root' })
export class AdminSiteContentApi {
  static readonly homeUrl = '/api/admin/content/home';
  static readonly announcementsUrl = '/api/admin/content/announcements';
  static readonly sizeGuidesUrl = '/api/admin/content/size-guides';
  static readonly megaMenuUrl = '/api/admin/content/mega-menu';
  static readonly imagesUrl = '/api/admin/media/images';
  static readonly videosUrl = '/api/admin/media/videos';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  // ---- homepage -----------------------------------------------------------------------------------

  home(): Observable<AdminHomeContent> {
    return this.http.get<AdminHomeContent>(AdminSiteContentApi.homeUrl);
  }

  updateSection(sectionId: number, body: HomeSectionRequest): Observable<HomeSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<HomeSaved>(`${AdminSiteContentApi.homeUrl}/sections/${sectionId}`, body, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /** Adds an item; `body.rowVersion` is the section's, and the answer carries the section's new one. */
  createItem(sectionId: number, body: HomeItemRequest): Observable<HomeSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<HomeSaved>(`${AdminSiteContentApi.homeUrl}/sections/${sectionId}/items`, body, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  updateItem(itemId: number, body: HomeItemRequest): Observable<HomeSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<HomeSaved>(`${AdminSiteContentApi.homeUrl}/items/${itemId}`, body, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /** Removes an item. The SECTION's row version goes in `If-Match`. */
  deleteItem(itemId: number, sectionRowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminSiteContentApi.homeUrl}/items/${itemId}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': quoted(sectionRowVersion) },
      }),
    );
  }

  // ---- announcement bar ---------------------------------------------------------------------------

  announcements(): Observable<readonly AdminAnnouncement[]> {
    return this.http.get<readonly AdminAnnouncement[]>(AdminSiteContentApi.announcementsUrl);
  }

  /** Adds (id null) or replaces a message. The version rides in the body on an edit. */
  saveAnnouncement(
    announcementId: number | null,
    body: AnnouncementRequest,
    version: string | null,
  ): Observable<AnnouncementSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      announcementId === null
        ? this.http.post<AnnouncementSaved>(AdminSiteContentApi.announcementsUrl, body, {
            headers: { 'X-XSRF-TOKEN': token },
          })
        : this.http.put<AnnouncementSaved>(
            `${AdminSiteContentApi.announcementsUrl}/${announcementId}`,
            { ...body, version },
            { headers: { 'X-XSRF-TOKEN': token } },
          ),
    );
  }

  deleteAnnouncement(announcementId: number, version: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminSiteContentApi.announcementsUrl}/${announcementId}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': quoted(version) },
      }),
    );
  }

  // ---- size guides --------------------------------------------------------------------------------

  sizeGuides(): Observable<readonly AdminSizeGuideListItem[]> {
    return this.http.get<readonly AdminSizeGuideListItem[]>(AdminSiteContentApi.sizeGuidesUrl);
  }

  sizeGuide(id: number): Observable<AdminSizeGuide> {
    return this.http.get<AdminSizeGuide>(`${AdminSiteContentApi.sizeGuidesUrl}/${id}`);
  }

  /** Creates (id null) or replaces a size guide. The row version rides in the body on an edit. */
  saveSizeGuide(id: number | null, body: SizeGuideRequest, rowVersion: string | null): Observable<SizeGuideSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      id === null
        ? this.http.post<SizeGuideSaved>(AdminSiteContentApi.sizeGuidesUrl, body, {
            headers: { 'X-XSRF-TOKEN': token },
          })
        : this.http.put<SizeGuideSaved>(
            `${AdminSiteContentApi.sizeGuidesUrl}/${id}`,
            { ...body, rowVersion },
            { headers: { 'X-XSRF-TOKEN': token } },
          ),
    );
  }

  deleteSizeGuide(id: number, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminSiteContentApi.sizeGuidesUrl}/${id}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': quoted(rowVersion) },
      }),
    );
  }

  // ---- mega-menu cards (#215) ---------------------------------------------------------------------

  megaMenu(): Observable<AdminMegaMenuContent> {
    return this.http.get<AdminMegaMenuContent>(AdminSiteContentApi.megaMenuUrl);
  }

  /** Adds (id null) or replaces a card. The row version rides in the body on an edit. */
  saveMegaMenuCard(id: number | null, body: MegaMenuCardRequest, rowVersion: string | null): Observable<MegaMenuCardSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      id === null
        ? this.http.post<MegaMenuCardSaved>(`${AdminSiteContentApi.megaMenuUrl}/cards`, body, {
            headers: { 'X-XSRF-TOKEN': token },
          })
        : this.http.put<MegaMenuCardSaved>(
            `${AdminSiteContentApi.megaMenuUrl}/cards/${id}`,
            { ...body, rowVersion },
            { headers: { 'X-XSRF-TOKEN': token } },
          ),
    );
  }

  deleteMegaMenuCard(id: number, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminSiteContentApi.megaMenuUrl}/cards/${id}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': quoted(rowVersion) },
      }),
    );
  }

  // ---- media --------------------------------------------------------------------------------------

  /** Uploads an image to the media library. Alt text is required on this screen, so it is always sent. */
  uploadImage(file: File, altText: string): Observable<UploadedMedia> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('altText', altText);
    form.append('title', file.name);

    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<UploadedMedia>(AdminSiteContentApi.imagesUrl, form, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /** Uploads an MP4 and its poster (the library cannot hold a video without one). */
  uploadVideo(file: File, poster: File, altText: string): Observable<UploadedMedia> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('poster', poster, poster.name);
    form.append('altText', altText);

    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<UploadedMedia>(AdminSiteContentApi.videosUrl, form, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }
}

function quoted(rowVersion: string): string {
  return `"${rowVersion}"`;
}

/** A failure from one of these routes, with the messages the content screens show. */
export interface ContentFailure extends CatalogFailure {
  /** Per-field messages, keyed by the request's camelCase field names, joined into one sentence each. */
  readonly fields: Readonly<Record<string, string>>;
  /** True for a 409 `stale_row_version`: the screen offers a reload. */
  readonly stale: boolean;
}

/**
 * Classifies a failed call, turning the codes these screens can meet into sentences a staff member can
 * act on. `what` names the thing, e.g. "this block", so the stale message says what changed.
 */
export function toContentFailure(error: unknown, what = 'this'): ContentFailure {
  const failure = toCatalogFailure(error);
  const fields: Record<string, string> = {};

  for (const [key, messages] of Object.entries(failure.fieldErrors ?? {})) {
    // The server uses camelCase; a PascalCase key (model binding) is normalised so it still lands.
    const field = key.length > 0 ? key[0].toLowerCase() + key.slice(1) : key;
    fields[field] = field in fields ? `${fields[field]} ${messages.join(' ')}` : messages.join(' ');
  }

  if (failure.kind === 'forbidden') {
    return {
      ...failure,
      fields,
      stale: false,
      message:
        'Your role does not allow this. Ask an administrator for the Content permission it needs.',
    };
  }

  if (failure.code === 'stale_row_version') {
    return {
      ...failure,
      fields,
      stale: true,
      message: `Someone else changed ${what} while you were editing it. Reload to see their change, then make yours again.`,
    };
  }

  return { ...failure, fields, stale: false };
}
