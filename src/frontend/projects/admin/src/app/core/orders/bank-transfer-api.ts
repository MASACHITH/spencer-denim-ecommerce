import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  BankTransferDecision,
  BankTransferDecisionResult,
  BankTransferQueuePage,
  BankTransferRefusal,
} from './bank-transfer.model';

/**
 * `/api/admin/orders/bank-transfers` (PAY-01, ADM-09, doc 11 P7).
 *
 * Every mutating call carries a freshly fetched antiforgery token through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place.
 */
@Injectable({ providedIn: 'root' })
export class BankTransferApi {
  static readonly queueUrl = '/api/admin/orders/bank-transfers';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  queue(page: number, pageSize: number): Observable<BankTransferQueuePage> {
    return this.http.get<BankTransferQueuePage>(
      `${BankTransferApi.queueUrl}?page=${page}&pageSize=${pageSize}`,
    );
  }

  /**
   * Approve, reject or accept a late payment.
   *
   * The decision is a URL segment rather than a field in the body, because it decides which
   * permission-bearing action runs: a client that mistyped `"reject"` as `"approve"` in a body would take
   * the money.
   */
  decide(
    orderPublicId: string,
    decision: BankTransferDecision,
    note: string | null,
  ): Observable<BankTransferDecisionResult> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<BankTransferDecisionResult>(
        `${BankTransferApi.queueUrl}/${orderPublicId}/${decision}`,
        { note },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }

  /** Where the slip downloads from. A GET the browser follows; the route is keyed on the order. */
  slipUrl(orderPublicId: string): string {
    return `${BankTransferApi.queueUrl}/${orderPublicId}/slip`;
  }
}

/**
 * Reads a refused decision's problem document.
 *
 * **Its own classifier rather than `toCatalogFailure`, because the fields that matter here are the ones
 * that function throws away.** A 409 from these routes carries `code`, `orderStatus` and `paymentStatus`,
 * and a refused late acceptance carries `shortLines` — and doc 11 P7's business rule turns on them: a
 * screen told only "this could not be applied" cannot offer **Accept late payment**, which is the one
 * action that will work when the answer is `order_expired`.
 */
export function toBankTransferRefusal(error: unknown): BankTransferRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) {
    return null;
  }

  const body = (error.error ?? null) as Record<string, unknown> | null;

  if (body === null) {
    return null;
  }

  const text = (key: string): string | null =>
    typeof body[key] === 'string' ? (body[key] as string) : null;

  const shortLines =
    body['shortLines'] !== null && typeof body['shortLines'] === 'object'
      ? (body['shortLines'] as Record<string, number>)
      : null;

  return {
    code: text('code') ?? 'conflict',

    // The server's own sentence. It is written for the staff member and says what to do next; anything
    // this function invented would be worse.
    message: text('detail') ?? 'That could not be applied. Reload the page.',
    orderStatus: text('orderStatus'),
    paymentStatus: text('paymentStatus'),
    shortLines,
  };
}
