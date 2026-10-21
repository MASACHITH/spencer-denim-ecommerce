import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';

/** One term on the public list, with how many searches with results put it there (`PopularSearchRow`). */
export interface AdminPopularSearch {
  readonly term: string;
  readonly searches: number;
}

/** A term staff have hidden (`HiddenSearchTermRow`). */
export interface AdminHiddenSearchTerm {
  readonly term: string;
  readonly hiddenAtUtc: string;
}

/** `GET /api/admin/content/popular-searches` (`AdminPopularSearchesDto`). */
export interface AdminPopularSearches {
  readonly shown: readonly AdminPopularSearch[];
  readonly hidden: readonly AdminHiddenSearchTerm[];
  readonly minimumSearches: number;
}

/** What a hide or show answers (`SearchTermChangedDto`). */
export interface SearchTermChanged {
  readonly changed: boolean;
}

/**
 * The popular searches shoppers see in the search box, and the staff hide list (SRCH-01, issue #78).
 * Reading needs Content.View; hiding and showing need Content.Edit and carry a fresh antiforgery token.
 */
@Injectable({ providedIn: 'root' })
export class AdminPopularSearchesApi {
  static readonly url = '/api/admin/content/popular-searches';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  list(): Observable<AdminPopularSearches> {
    return this.http.get<AdminPopularSearches>(AdminPopularSearchesApi.url);
  }

  hide(term: string): Observable<SearchTermChanged> {
    return this.change('hide', term);
  }

  show(term: string): Observable<SearchTermChanged> {
    return this.change('show', term);
  }

  private change(action: 'hide' | 'show', term: string): Observable<SearchTermChanged> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<SearchTermChanged>(`${AdminPopularSearchesApi.url}/${action}`, { term }, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }
}
