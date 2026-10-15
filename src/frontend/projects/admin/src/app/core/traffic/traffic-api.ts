import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PeriodQuery } from '../reporting/reporting-api';
import { TrafficReport, TrafficSources } from './traffic.model';

/** `GET /api/admin/traffic` and `GET /api/admin/dashboard/traffic-sources`, both `Traffic.View` (doc 11 P16). */
@Injectable({ providedIn: 'root' })
export class TrafficApi {
  static readonly trafficUrl = '/api/admin/traffic';
  static readonly sourcesUrl = '/api/admin/dashboard/traffic-sources';

  private readonly http = inject(HttpClient);

  report(period: PeriodQuery): Observable<TrafficReport> {
    return this.http.get<TrafficReport>(TrafficApi.trafficUrl, { params: params(period) });
  }

  sources(period: PeriodQuery): Observable<TrafficSources> {
    return this.http.get<TrafficSources>(TrafficApi.sourcesUrl, { params: params(period) });
  }
}

function params(period: PeriodQuery): HttpParams {
  return new HttpParams().set('from', period.from).set('to', period.to);
}
