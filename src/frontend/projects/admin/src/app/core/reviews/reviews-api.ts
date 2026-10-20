import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminModerationResponse, AdminReviewListPage, ModerationAction, ReviewQuery } from './reviews.model';

/** `/api/admin/reviews` (ADM-16). Writes carry the antiforgery token and the review's rowVersion. */
@Injectable({ providedIn: 'root' })
export class ReviewsApi {
  static readonly url = '/api/admin/reviews';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  list(query: ReviewQuery): Observable<AdminReviewListPage> {
    let params = new HttpParams()
      .set('status', query.status)
      .set('page', query.page)
      .set('pageSize', query.pageSize);

    if (query.verified !== 'Any') {
      params = params.set('verified', query.verified === 'Verified' ? 'true' : 'false');
    }

    if (query.search.trim() !== '') {
      params = params.set('search', query.search.trim());
    }

    return this.http.get<AdminReviewListPage>(ReviewsApi.url, { params });
  }

  moderate(reviewId: number, action: ModerationAction, rowVersion: string, note: string | null): Observable<AdminModerationResponse> {
    return this.auth.postWithAntiforgeryToken<AdminModerationResponse>(`${ReviewsApi.url}/${reviewId}/${action}`, {
      rowVersion,
      note,
    });
  }
}
