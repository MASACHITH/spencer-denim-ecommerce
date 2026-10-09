import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminAlertAcknowledgement, AdminAlertBanner } from './admin-alert.model';

/**
 * `/api/admin/alerts` (doc 08 §3.8 N2, doc 11 §7 G-37).
 *
 * The acknowledge carries a freshly fetched antiforgery token through {@link AdminAuthApi}, so the header
 * name and the token endpoint live in one place.
 *
 * **Nothing here knows doc 08 R10's visibility rule, deliberately.** The server decides what this user
 * may see and answers 403 when R10 gives them no path at all; a client-side copy of the rule would be a
 * second place for it to be wrong, and the one that cannot be the security boundary (doc 03 §3a).
 */
@Injectable({ providedIn: 'root' })
export class AdminAlertsApi {
  static readonly alertsUrl = '/api/admin/alerts';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  open(): Observable<AdminAlertBanner> {
    return this.http.get<AdminAlertBanner>(AdminAlertsApi.alertsUrl);
  }

  /**
   * Records that this user has seen the alert. It resolves nothing (doc 08 §3.8).
   *
   * **The body is empty, and the route takes no fields.** Doc 08 §3.8 gives the table an
   * `AcknowledgementNote` column and nothing in this build writes it (owner's decision, 2026-10-09);
   * the route refuses a note rather than dropping one, so there is nothing for this method to send. An
   * empty object rather than no body at all, because the route reads JSON and answers a named 400 for an
   * absent one.
   */
  acknowledge(alertId: number): Observable<AdminAlertAcknowledgement> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<AdminAlertAcknowledgement>(
        `${AdminAlertsApi.alertsUrl}/${alertId}/acknowledge`,
        {},
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }
}
