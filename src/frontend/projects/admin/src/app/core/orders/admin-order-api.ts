import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  AdminCourier,
  AdminOrderDetail,
  AdminOrderListPage,
  AdminOrderSort,
  OrderInvoice,
  OrderStatusName,
  OrderTransitionRefusal,
  OrderTransitionResult,
  OrderTransitionSegment,
} from './admin-order.model';

/**
 * `/api/admin/orders` and `/api/admin/couriers` (ADM-09, doc 11 P9).
 *
 * Every mutating call carries a freshly fetched antiforgery token through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place.
 */
@Injectable({ providedIn: 'root' })
export class AdminOrderApi {
  static readonly ordersUrl = '/api/admin/orders';
  static readonly couriersUrl = '/api/admin/couriers';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  /**
   * One page of the list.
   *
   * **`sort` is omitted when the screen has no opinion**, which is how doc 11 P9 step 1's "Paid, oldest
   * first" is honoured: the rule lives on the server, the answer says which order it used, and the screen
   * labels the list from that. Sending a default from here would put the business rule in the client,
   * where the next client would have to remember it.
   */
  list(options: {
    search?: string | null;
    status?: OrderStatusName | null;
    sort?: AdminOrderSort | null;
    page: number;
    pageSize: number;
  }): Observable<AdminOrderListPage> {
    let params = new HttpParams()
      .set('page', String(options.page))
      .set('pageSize', String(options.pageSize));

    if (options.search) {
      params = params.set('search', options.search);
    }

    if (options.status) {
      params = params.set('status', options.status);
    }

    if (options.sort) {
      params = params.set('sort', options.sort);
    }

    return this.http.get<AdminOrderListPage>(AdminOrderApi.ordersUrl, { params });
  }

  order(orderPublicId: string): Observable<AdminOrderDetail> {
    return this.http.get<AdminOrderDetail>(`${AdminOrderApi.ordersUrl}/${orderPublicId}`);
  }

  invoice(orderPublicId: string): Observable<OrderInvoice> {
    return this.http.get<OrderInvoice>(`${AdminOrderApi.ordersUrl}/${orderPublicId}/invoice`);
  }

  /**
   * Applies one transition.
   *
   * The act is a URL segment rather than a field in the body, because it decides which
   * permission-bearing action runs: a client that mistyped one value for another in a body would ship an
   * order instead of noting it.
   */
  transition(
    orderPublicId: string,
    segment: OrderTransitionSegment,
    body: Record<string, unknown>,
  ): Observable<OrderTransitionResult> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<OrderTransitionResult>(
        `${AdminOrderApi.ordersUrl}/${orderPublicId}/${segment}`,
        body,
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }

  couriers(): Observable<readonly AdminCourier[]> {
    return this.http.get<readonly AdminCourier[]>(AdminOrderApi.couriersUrl);
  }

  /**
   * Creates or replaces a courier.
   *
   * **Every field is sent, every time, including the blank ones.** The endpoint refuses a body that leaves
   * one out rather than reading it as null, because a save that did not mention a field has silently
   * cleared it four times in this project (W1-12, W1-03, GLB-10). Sending them all is the client's half of
   * that guarantee.
   */
  saveCourier(
    courierId: number | null,
    courier: {
      name: string;
      trackingUrlTemplate: string | null;
      phone: string | null;
      isActive: boolean;
      sortOrder: number;
    },
  ): Observable<AdminCourier> {
    return this.auth.withAntiforgeryToken((token) =>
      courierId === null
        ? this.http.post<AdminCourier>(AdminOrderApi.couriersUrl, courier, {
            headers: { 'X-XSRF-TOKEN': token },
          })
        : this.http.put<AdminCourier>(`${AdminOrderApi.couriersUrl}/${courierId}`, courier, {
            headers: { 'X-XSRF-TOKEN': token },
          }),
    );
  }

  /** Where a payment slip downloads from. The route is keyed on the order, never on the asset. */
  slipUrl(orderPublicId: string): string {
    return `/api/admin/orders/bank-transfers/${orderPublicId}/slip`;
  }

  /**
   * Accepts a late bank transfer on an expired or cancelled order (doc 11 P7 step 8, issue #124).
   *
   * **It posts to the bank-transfer route rather than a new one**, because that transaction is already
   * built, tested and verified: `AcceptLateAsync` re-checks stock for every line and names every short
   * one. What #124 was missing was a *screen* that could reach it, and that is the order detail.
   */
  acceptLatePayment(orderPublicId: string, note: string | null): Observable<unknown> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post(
        `/api/admin/orders/bank-transfers/${orderPublicId}/accept-late`,
        { note },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }
}

/**
 * Reads a refused transition's problem document.
 *
 * **Its own classifier rather than `toCatalogFailure`, because the fields that matter here are the ones
 * that function throws away.** A 409 from these routes carries `code`, `orderStatus` and
 * `orderStatusLabel`, and what the screen has to do turns on them: with the current status in hand it can
 * redraw the action that will work, which is the thing W3-03's `ShortStock` defect was about.
 */
export function toOrderTransitionRefusal(error: unknown): OrderTransitionRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) {
    return null;
  }

  const body = (error.error ?? null) as Record<string, unknown> | null;

  if (body === null) {
    return null;
  }

  const text = (key: string): string | null =>
    typeof body[key] === 'string' ? (body[key] as string) : null;

  return {
    code: text('code') ?? 'conflict',

    // The server's own sentence. It is written for the staff member and says what to do next; anything
    // this function invented would be worse.
    message: text('detail') ?? 'That could not be applied. Reload the page.',
    orderStatus: text('orderStatus'),
    orderStatusLabel: text('orderStatusLabel'),
  };
}
