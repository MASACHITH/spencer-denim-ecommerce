import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  AdminDashboard,
  CustomerReportPage,
  InventoryReportPage,
  InventoryStockFilter,
  LowStock,
  ReportFile,
  SalesGrouping,
  SalesReport,
  SalesTrend,
  TrendInterval,
} from './reporting.model';

/** A period as the screens send it: Colombo days, `yyyy-MM-dd`, both included. */
export interface PeriodQuery {
  readonly from: string;
  readonly to: string;
}

/**
 * `/api/admin/dashboard` (`Dashboard.View`) and `/api/admin/reports` (`Reports.View`, CSV `Reports.Export`),
 * doc 11 P16.
 *
 * **The exports are POSTs with the antiforgery token**, because the server writes an audit row for each one
 * before the file is sent. The file name is the server's, from `Content-Disposition`.
 */
@Injectable({ providedIn: 'root' })
export class ReportingApi {
  static readonly dashboardUrl = '/api/admin/dashboard';
  static readonly reportsUrl = '/api/admin/reports';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  dashboard(period: PeriodQuery): Observable<AdminDashboard> {
    return this.http.get<AdminDashboard>(ReportingApi.dashboardUrl, { params: periodParams(period) });
  }

  trend(interval: TrendInterval, to: string): Observable<SalesTrend> {
    return this.http.get<SalesTrend>(`${ReportingApi.dashboardUrl}/trend`, {
      params: new HttpParams().set('interval', interval).set('to', to),
    });
  }

  lowStock(): Observable<LowStock> {
    return this.http.get<LowStock>(`${ReportingApi.dashboardUrl}/low-stock`);
  }

  sales(period: PeriodQuery, groupBy: SalesGrouping): Observable<SalesReport> {
    return this.http.get<SalesReport>(`${ReportingApi.reportsUrl}/sales`, {
      params: periodParams(period).set('groupBy', groupBy),
    });
  }

  inventory(status: InventoryStockFilter, search: string, page: number, pageSize: number): Observable<InventoryReportPage> {
    return this.http.get<InventoryReportPage>(`${ReportingApi.reportsUrl}/inventory`, {
      params: inventoryParams(status, search).set('page', String(page)).set('pageSize', String(pageSize)),
    });
  }

  customers(period: PeriodQuery, search: string, page: number, pageSize: number): Observable<CustomerReportPage> {
    return this.http.get<CustomerReportPage>(`${ReportingApi.reportsUrl}/customers`, {
      params: searchParam(periodParams(period), search).set('page', String(page)).set('pageSize', String(pageSize)),
    });
  }

  exportSales(period: PeriodQuery, groupBy: SalesGrouping): Observable<ReportFile> {
    return this.download('sales', periodParams(period).set('groupBy', groupBy), 'sales-report.csv');
  }

  exportInventory(status: InventoryStockFilter, search: string): Observable<ReportFile> {
    return this.download('inventory', inventoryParams(status, search), 'inventory-report.csv');
  }

  exportCustomers(period: PeriodQuery, search: string): Observable<ReportFile> {
    return this.download('customers', searchParam(periodParams(period), search), 'customers-report.csv');
  }

  private download(report: string, params: HttpParams, fallback: string): Observable<ReportFile> {
    return this.auth
      .withAntiforgeryToken((token) =>
        this.http.post(`${ReportingApi.reportsUrl}/${report}/export`, null, {
          params,
          headers: { 'X-XSRF-TOKEN': token },
          observe: 'response',
          responseType: 'blob',
        }),
      )
      .pipe(
        map((response) => ({
          blob: response.body ?? new Blob([]),
          fileName: fileNameFrom(response.headers.get('Content-Disposition'), fallback),
        })),
      );
  }
}

function periodParams(period: PeriodQuery): HttpParams {
  return new HttpParams().set('from', period.from).set('to', period.to);
}

function searchParam(params: HttpParams, search: string): HttpParams {
  return search.trim().length > 0 ? params.set('search', search.trim()) : params;
}

function inventoryParams(status: InventoryStockFilter, search: string): HttpParams {
  return searchParam(new HttpParams().set('status', status), search);
}

/** The `filename=` of a `Content-Disposition` header, or the fallback when there is none. */
export function fileNameFrom(disposition: string | null, fallback: string): string {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition ?? '');

  return match ? decodeURIComponent(match[1]) : fallback;
}
