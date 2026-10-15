import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AdminCustomerDetail,
  AdminCustomerListPage,
  AdminCustomerOrderPage,
} from './admin-customer.model';

/**
 * `/api/admin/customers` (ADM-15, doc 11 P11 step 16; `Customers.View`). Reads only: the screen edits
 * nothing, so no call here carries an antiforgery token.
 */
@Injectable({ providedIn: 'root' })
export class AdminCustomerApi {
  static readonly url = '/api/admin/customers';

  private readonly http = inject(HttpClient);

  list(options: {
    search?: string | null;
    districtId?: number | null;
    page: number;
    pageSize: number;
  }): Observable<AdminCustomerListPage> {
    let params = new HttpParams()
      .set('page', String(options.page))
      .set('pageSize', String(options.pageSize));

    if (options.search) {
      params = params.set('search', options.search);
    }

    if (options.districtId !== null && options.districtId !== undefined) {
      params = params.set('districtId', String(options.districtId));
    }

    return this.http.get<AdminCustomerListPage>(AdminCustomerApi.url, { params });
  }

  customer(customerId: number): Observable<AdminCustomerDetail> {
    return this.http.get<AdminCustomerDetail>(`${AdminCustomerApi.url}/${customerId}`);
  }

  orders(customerId: number, page: number, pageSize: number): Observable<AdminCustomerOrderPage> {
    const params = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));

    return this.http.get<AdminCustomerOrderPage>(`${AdminCustomerApi.url}/${customerId}/orders`, {
      params,
    });
  }
}
