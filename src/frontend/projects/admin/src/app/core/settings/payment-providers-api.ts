import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';

/**
 * One payment method on the settings screen's Payment methods panel (owner decision 2026-10-21: bank transfer
 * switched off, PayPal the only payment method, "block only for now"). A copy of `PaymentProviderAdminDto`.
 */
export interface PaymentProviderRow {
  readonly code: string;
  readonly displayName: string;
  /** The shop's switch. */
  readonly isEnabled: boolean;
  /** Whether it could take a payment right now if switched on (PayPal: credentials and a fresh rate). */
  readonly isReady: boolean;
  /** Switched on and ready: what checkout lists. */
  readonly isOffered: boolean;
  readonly rowVersion: string;
}

export interface PaymentProvidersPanel {
  readonly providers: readonly PaymentProviderRow[];
  /** False when no method is offered: checkout then refuses every order. */
  readonly anyOffered: boolean;
}

/** `/api/admin/settings/payment-providers`: `Settings.Payments` only (Super Admin). */
@Injectable({ providedIn: 'root' })
export class PaymentProvidersApi {
  static readonly url = '/api/admin/settings/payment-providers';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  panel(): Observable<PaymentProvidersPanel> {
    return this.http.get<PaymentProvidersPanel>(PaymentProvidersApi.url);
  }

  setEnabled(code: string, isEnabled: boolean, rowVersion: string): Observable<PaymentProvidersPanel> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<PaymentProvidersPanel>(
        `${PaymentProvidersApi.url}/${encodeURIComponent(code)}`,
        { isEnabled, rowVersion },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }
}
