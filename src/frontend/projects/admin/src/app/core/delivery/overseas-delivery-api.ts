import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  CountryDeliveryUpdate,
  DeliveryRegionUpdate,
  OverseasDeliveryScreen,
} from './overseas-delivery.model';

/**
 * `/api/admin/delivery/overseas` (owner decision 2026-10-21). Every write carries a freshly fetched
 * antiforgery token and the row's `rowVersion`, and answers with the whole screen.
 */
@Injectable({ providedIn: 'root' })
export class OverseasDeliveryApi {
  static readonly url = '/api/admin/delivery/overseas';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  screen(): Observable<OverseasDeliveryScreen> {
    return this.http.get<OverseasDeliveryScreen>(OverseasDeliveryApi.url);
  }

  setInternational(isEnabled: boolean, rowVersion: string | null): Observable<OverseasDeliveryScreen> {
    return this.put(`${OverseasDeliveryApi.url}/international`, { isEnabled, rowVersion });
  }

  updateRegion(code: string, update: DeliveryRegionUpdate): Observable<OverseasDeliveryScreen> {
    return this.put(`${OverseasDeliveryApi.url}/regions/${encodeURIComponent(code)}`, update);
  }

  updateCountry(code: string, update: CountryDeliveryUpdate): Observable<OverseasDeliveryScreen> {
    return this.put(`${OverseasDeliveryApi.url}/countries/${encodeURIComponent(code)}`, update);
  }

  private put(url: string, body: unknown): Observable<OverseasDeliveryScreen> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<OverseasDeliveryScreen>(url, body, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }
}
