import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AdminContactEnquiries,
  AdminContactEnquiry,
  AdminContentPage,
  AdminContentPageListItem,
  ContactEnquiryStatus,
  ContentPageSaved,
  ContentPageValues,
} from './admin-content.model';
import { AdminAuthApi } from '../auth/admin-auth-api';

/**
 * `/api/admin/content/pages` and `/api/admin/contact-enquiries` (ADM-17, GLB-10).
 *
 * Every mutating call carries a freshly fetched antiforgery token, through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place — the arrangement `AdminSettingsApi` established.
 */
@Injectable({ providedIn: 'root' })
export class AdminContentApi {
  static readonly pagesUrl = '/api/admin/content/pages';
  static readonly enquiriesUrl = '/api/admin/contact-enquiries';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  pages(): Observable<readonly AdminContentPageListItem[]> {
    // No trailing slash, matching `AdminSettingsApi`. The API maps the collection root as `MapGet("/")`
    // on the group, which ASP.NET Core matches with or without it; using the same spelling as every
    // other admin client keeps one form in the network tab and one form in the logs.
    return this.http.get<readonly AdminContentPageListItem[]>(AdminContentApi.pagesUrl);
  }

  page(pageId: number): Observable<AdminContentPage> {
    return this.http.get<AdminContentPage>(`${AdminContentApi.pagesUrl}/${pageId}`);
  }

  /** Creates a custom page. It is always a draft; there is no way to ask for a system key. */
  createPage(values: ContentPageValues): Observable<ContentPageSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<ContentPageSaved>(AdminContentApi.pagesUrl, values, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /**
   * Edits a page.
   *
   * **Send only the fields you are changing.** A field this body omits is left exactly as it is, and a
   * field it carries as `null` is cleared — two different instructions, which is why
   * {@link ContentPageValues} has every field optional. The editor sends the whole form because it has
   * read the whole page, and a caller that has not read it should not be sending one at all.
   */
  updatePage(pageId: number, values: ContentPageValues, rowVersion: string): Observable<ContentPageSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<ContentPageSaved>(
        `${AdminContentApi.pagesUrl}/${pageId}`,
        { ...values, rowVersion },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }

  /** Puts a page on the shop or takes it off. Its own call, with its own audit row. */
  setPublished(pageId: number, isPublished: boolean, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<void>(
        `${AdminContentApi.pagesUrl}/${pageId}/published`,
        { isPublished, rowVersion },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }

  /** Removes a custom page. Refused with 409 for one of the six system pages. */
  deletePage(pageId: number, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminContentApi.pagesUrl}/${pageId}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': rowVersion },
      }),
    );
  }

  enquiries(status: ContactEnquiryStatus | null, skip: number, take: number): Observable<AdminContactEnquiries> {
    const query = new URLSearchParams({ skip: String(skip), take: String(take) });
    if (status !== null) {
      query.set('status', status);
    }

    return this.http.get<AdminContactEnquiries>(`${AdminContentApi.enquiriesUrl}?${query}`);
  }

  enquiry(submissionId: number): Observable<AdminContactEnquiry> {
    return this.http.get<AdminContactEnquiry>(`${AdminContentApi.enquiriesUrl}/${submissionId}`);
  }

  /**
   * Marks a New enquiry Read (doc 11 P15 step 7: reading it is what moves it).
   *
   * A POST rather than riding on the GET above, because a GET that changes a row is a row a link
   * checker, a prefetch or a browser's back button can change. The screen sends both as it opens one.
   */
  markRead(submissionId: number): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<void>(`${AdminContentApi.enquiriesUrl}/${submissionId}/read`, null, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  setStatus(submissionId: number, status: ContactEnquiryStatus): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<void>(
        `${AdminContentApi.enquiriesUrl}/${submissionId}/status`,
        { status },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }

  /**
   * Deletes an enquiry for good, with the reason doc 08 §3.7 requires the audit row to hold.
   *
   * POST rather than DELETE because the reason travels in a body, and a DELETE with a body is not
   * reliably forwarded by proxies.
   */
  deleteEnquiry(submissionId: number, reason: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<void>(
        `${AdminContentApi.enquiriesUrl}/${submissionId}/delete`,
        { reason },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }
}
