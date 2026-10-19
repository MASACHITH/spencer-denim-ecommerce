import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  InventoryFilterOptions,
  InventoryHistoryPage,
  InventoryListPage,
  InventoryQuery,
  InventoryRow,
  StockChangeRequest,
  StockChangeResult,
} from './inventory.model';

/**
 * `/api/admin/inventory` (ADM-12, #190; doc 11 P2). Reads need `Inventory.View`, writes `Inventory.Edit`;
 * every write carries a freshly fetched antiforgery token through {@link AdminAuthApi}.
 */
@Injectable({ providedIn: 'root' })
export class InventoryApi {
  static readonly url = '/api/admin/inventory';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  list(query: InventoryQuery): Observable<InventoryListPage> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);

    if (query.search.trim() !== '') {
      params = params.set('search', query.search.trim());
    }

    if (query.status !== 'All') {
      params = params.set('status', query.status);
    }

    if (query.departmentId !== null) {
      params = params.set('departmentId', query.departmentId);
    }

    if (query.categoryId !== null) {
      params = params.set('categoryId', query.categoryId);
    }

    if (query.sort !== 'Product' || query.direction !== 'Asc') {
      params = params.set('sort', query.sort).set('direction', query.direction);
    }

    return this.http.get<InventoryListPage>(InventoryApi.url, { params });
  }

  filters(): Observable<InventoryFilterOptions> {
    return this.http.get<InventoryFilterOptions>(`${InventoryApi.url}/filters`);
  }

  history(variantId: number, page: number, pageSize: number): Observable<InventoryHistoryPage> {
    return this.http.get<InventoryHistoryPage>(`${InventoryApi.url}/${variantId}/movements`, {
      params: new HttpParams().set('page', page).set('pageSize', pageSize),
    });
  }

  /**
   * One or more changes with one reason. **The answer is 200 even when a row was not applied**: each row
   * says Applied, Refused, Changed or NotFound, because a bulk update is applied row by row (doc 08 §5.8).
   * A 400 means the request itself was malformed and nothing was applied.
   */
  adjust(request: StockChangeRequest): Observable<StockChangeResult> {
    return this.auth.postWithAntiforgeryToken<StockChangeResult>(`${InventoryApi.url}/adjustments`, request);
  }

  /** Sets the variant's own threshold, or clears it (null) to use the shop default. */
  setThreshold(variantId: number, lowStockThreshold: number | null): Observable<InventoryRow> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<InventoryRow>(
        `${InventoryApi.url}/${variantId}/threshold`,
        { lowStockThreshold },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }
}
