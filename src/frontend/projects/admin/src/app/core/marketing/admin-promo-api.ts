import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminPromoCode, AdminPromoCodeRequest, AdminPromoCodeSaved } from './admin-promo.model';

/**
 * `/api/admin/marketing/promo-codes` (ADM-13, doc 11 P3 part B).
 *
 * Every mutating call carries a freshly fetched antiforgery token through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place.
 *
 * **Sale campaigns (ADM-14) are not here.** Doc 02 line 119 is a separate feature; this service is named
 * for the promo codes it serves rather than for the module, so adding campaigns adds a service and
 * changes nothing here.
 */
@Injectable({ providedIn: 'root' })
export class AdminPromoApi {
  static readonly promoCodesUrl = '/api/admin/marketing/promo-codes';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  /** Every code that has not been removed, newest first. */
  list(): Observable<readonly AdminPromoCode[]> {
    return this.http.get<readonly AdminPromoCode[]>(AdminPromoApi.promoCodesUrl);
  }

  /**
   * Creates a code, or replaces one.
   *
   * **Every field is sent, including the blank ones**, and the endpoint refuses a body that leaves one
   * out rather than reading its absence as "leave it alone". A save that did not mention a field has
   * silently cleared it four times in this project, and here the stakes are money: dropping
   * `totalUsageLimit` would turn a fifty-use code into an unlimited one. An empty box becomes `null`,
   * which is how a staff member says "no limit".
   *
   * The row version travels in `If-Match` on an edit, as the catalogue's delete does, so an edit from a
   * stale list is refused with 409 rather than overwriting somebody else's change.
   */
  save(
    promoCodeId: number | null,
    promoCode: AdminPromoCodeRequest,
    rowVersion: string | null,
  ): Observable<AdminPromoCodeSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      promoCodeId === null
        ? this.http.post<AdminPromoCodeSaved>(AdminPromoApi.promoCodesUrl, promoCode, {
            headers: { 'X-XSRF-TOKEN': token },
          })
        : this.http.put<AdminPromoCodeSaved>(
            `${AdminPromoApi.promoCodesUrl}/${promoCodeId}`,
            promoCode,
            {
              headers: {
                'X-XSRF-TOKEN': token,
                ...(rowVersion === null ? {} : { 'If-Match': `"${rowVersion}"` }),
              },
            },
          ),
    );
  }

  /**
   * Removes a code — a soft delete, which releases the string for reuse.
   *
   * Orders already placed are untouched and still print it, because an order keeps its own
   * `promoCodeText` (doc 08 §3.6 DB19); a pending order still gives its use back when it expires or is
   * cancelled.
   */
  remove(promoCodeId: number, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminPromoApi.promoCodesUrl}/${promoCodeId}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': `"${rowVersion}"` },
      }),
    );
  }
}
