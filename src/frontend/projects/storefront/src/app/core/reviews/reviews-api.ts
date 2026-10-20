import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { MyProductReview, ProductReviewPage, ReviewDraft, ReviewSubmission, toRequest } from './reviews.model';

/**
 * The review routes (PDP-08).
 *
 * **Two kinds of call.** `page` is public and safe during a server render (no cookie, the same answer for every
 * visitor). `mine`, `create` and `edit` need the customer session cookie, which the SSR process never forwards,
 * so the product page makes them only in the browser, after `AccountStore` has said the shopper is signed in.
 */
@Injectable({ providedIn: 'root' })
export class ReviewsApi {
  private readonly http = inject(HttpClient);

  page(slug: string, page: number, pageSize: number): Observable<ProductReviewPage> {
    return this.http.get<ProductReviewPage>(`/api/catalog/products/${encodeURIComponent(slug)}/reviews`, {
      params: new HttpParams().set('page', page).set('pageSize', pageSize),
    });
  }

  mine(productId: number): Observable<MyProductReview> {
    return this.http.get<MyProductReview>(`/api/account/reviews/${productId}`);
  }

  create(productId: number, draft: ReviewDraft): Observable<ReviewSubmission> {
    return this.http.post<ReviewSubmission>(`/api/account/reviews/${productId}`, toRequest(draft));
  }

  edit(productId: number, draft: ReviewDraft): Observable<ReviewSubmission> {
    return this.http.put<ReviewSubmission>(`/api/account/reviews/${productId}`, toRequest(draft));
  }
}
