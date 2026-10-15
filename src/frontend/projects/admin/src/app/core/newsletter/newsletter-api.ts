import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { NewsletterExport, NewsletterFilter, NewsletterSubscriberPage } from './newsletter.model';

/**
 * `/api/admin/newsletter/subscribers` (ADM-21, doc 11 P15 step 4): the list (`Newsletter.View`) and the
 * export (`Newsletter.Export`).
 */
@Injectable({ providedIn: 'root' })
export class NewsletterApi {
  static readonly url = '/api/admin/newsletter/subscribers';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  list(filter: NewsletterFilter, page: number, pageSize: number): Observable<NewsletterSubscriberPage> {
    let params = NewsletterApi.filterParams(filter)
      .set('page', String(page))
      .set('pageSize', String(pageSize));

    if (filter.status) {
      params = params.set('status', filter.status);
    }

    return this.http.get<NewsletterSubscriberPage>(NewsletterApi.url, { params });
  }

  /**
   * Downloads the subscribed addresses as CSV.
   *
   * **A POST with the antiforgery token**, because the server writes an audit row for every export.
   * **No status is sent**: the export is subscribed addresses whatever the list is showing, and the server
   * does not read one. The file name is the server's, from `Content-Disposition`.
   */
  export(filter: NewsletterFilter): Observable<NewsletterExport> {
    return this.auth
      .withAntiforgeryToken((token) =>
        this.http.post(`${NewsletterApi.url}/export`, null, {
          params: NewsletterApi.filterParams(filter),
          headers: { 'X-XSRF-TOKEN': token },
          observe: 'response',
          responseType: 'blob',
        }),
      )
      .pipe(
        map((response) => ({
          blob: response.body ?? new Blob([]),
          fileName: fileNameFrom(response.headers.get('Content-Disposition')),
        })),
      );
  }

  private static filterParams(filter: NewsletterFilter): HttpParams {
    let params = new HttpParams();

    if (filter.search.trim().length > 0) {
      params = params.set('search', filter.search.trim());
    }

    if (filter.from) {
      params = params.set('from', filter.from);
    }

    if (filter.to) {
      params = params.set('to', filter.to);
    }

    return params;
  }
}

/** The `filename=` of a `Content-Disposition` header, or a sensible default when there is none. */
export function fileNameFrom(disposition: string | null): string {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition ?? '');

  return match ? decodeURIComponent(match[1]) : 'newsletter-subscribers.csv';
}
