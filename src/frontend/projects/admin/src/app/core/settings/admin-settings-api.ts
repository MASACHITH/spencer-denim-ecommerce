import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import {
  AdminSettings,
  BankDetailValues,
  InvoiceAndFulfilmentValues,
  ContactAndSocialValues,
  PayPalValues,
  SmtpTestResponse,
  SmtpTextValues,
  ProductPageInfoSaveResponse,
  ProductPageInfoValues,
  TrafficValues,
  UploadedMediaAsset,
} from './admin-settings.model';

/**
 * `/api/admin/settings` and the one media call this screen makes (ADM-22, GLB-02).
 *
 * Every mutating call carries a freshly fetched antiforgery token, through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place.
 */
@Injectable({ providedIn: 'root' })
export class AdminSettingsApi {
  static readonly settingsUrl = '/api/admin/settings';
  static readonly storeLogoUrl = '/api/admin/settings/store-logo';
  static readonly contactSocialUrl = '/api/admin/settings/contact-social';
  static readonly bankUrl = '/api/admin/settings/bank';
  static readonly invoiceAndFulfilmentUrl = '/api/admin/settings/invoice-and-fulfilment';
  static readonly payPalUrl = '/api/admin/settings/paypal';
  static readonly trafficUrl = '/api/admin/settings/traffic';
  static readonly reviewsUrl = '/api/admin/settings/reviews';
  static readonly smtpUrl = '/api/admin/settings/email-smtp';
  static readonly smtpTestUrl = '/api/admin/settings/email-smtp/test';
  static readonly productPageInfoUrl = '/api/admin/settings/product-page-info';
  static readonly imagesUrl = '/api/admin/media/images';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  settings(): Observable<AdminSettings> {
    return this.http.get<AdminSettings>(AdminSettingsApi.settingsUrl);
  }

  /**
   * Uploads a logo image to the media library.
   *
   * **Two steps, not one, and the order is the point.** The file goes to the library first and the setting
   * is pointed at the result second, so a failed or refused upload changes nothing at all — rather than a
   * single endpoint that has to undo a half-applied change. It also means the logo is an ordinary library
   * asset: it appears in ADM-18, it has a checksum, and the library's delete guard protects it.
   *
   * `altText` is sent with the file because a public image needs alt text before the content using it is
   * published (doc 11 P13, GLB-12) — and for a logo the content using it is every page.
   */
  uploadLogo(file: File, altText: string): Observable<UploadedMediaAsset> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('altText', altText);
    form.append('title', file.name);

    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<UploadedMediaAsset>(AdminSettingsApi.imagesUrl, form, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /**
   * Points `Store.LogoAssetId` at an uploaded asset, or clears it.
   *
   * `logoAssetPublicId: null` is Remove — the owner's stated requirement, "let them remove the existing one
   * and add a new one". The API releases the previous asset's usage row in the same transaction, so the
   * media library will then allow the old file to be deleted.
   */
  setStoreLogo(logoAssetPublicId: string | null, rowVersion: string): Observable<void> {
    return this.put(AdminSettingsApi.storeLogoUrl, { logoAssetPublicId, rowVersion });
  }

  /** The WhatsApp number and the three social addresses. Every field, every save — PUT replaces. */
  updateContactAndSocial(values: ContactAndSocialValues, rowVersion: string): Observable<void> {
    return this.put(AdminSettingsApi.contactSocialUrl, { ...values, rowVersion });
  }

  /**
   * The bank details a customer transfers to (PAY-01, doc 11 P7's preconditions).
   *
   * **Only the fields the screen was given.** `Settings.Payments` is held by no default role, so this is
   * the first group whose boxes a given admin may genuinely not have on screen — and a `PUT` carrying a
   * field it never received would tell the API to clear the client's bank account number.
   */
  updateBankDetails(values: BankDetailValues, rowVersion: string): Observable<void> {
    return this.put(AdminSettingsApi.bankUrl, { ...values, rowVersion });
  }

  /**
   * The company's invoice details and the auto-deliver window (ADM-09, doc 08 §3.8).
   *
   * **Only the fields the screen was given**, the same rule as the bank group's: a `PUT` carrying a field
   * it never received would tell the API to clear it.
   */
  updateInvoiceAndFulfilment(
    values: InvoiceAndFulfilmentValues,
    rowVersion: string,
  ): Observable<void> {
    return this.put(AdminSettingsApi.invoiceAndFulfilmentUrl, { ...values, rowVersion });
  }

  /**
   * PayPal's credentials, exchange rate and timings (PAY-02, doc 08 §3.8).
   *
   * **Only the fields the screen was given**, the bank group's rule. Two things are particular to this one:
   *
   * - `clientSecret` is in `values` only when the admin typed one. The API never returns the secret, so the
   *   form cannot hold it, and an absent or blank `clientSecret` keeps the stored value.
   * - `confirmRate` re-stamps the rate's age without changing the rate ("today's rate is still this one").
   *   Saving some other field never does, so a stale rate cannot be made to look fresh by accident.
   */
  updatePayPal(
    values: PayPalValues & { readonly clientSecret?: string },
    rowVersion: string,
    confirmRate = false,
  ): Observable<void> {
    return this.put(AdminSettingsApi.payPalUrl, {
      ...values,
      ...(confirmRate ? { confirmRate: true } : {}),
      rowVersion,
    });
  }

  /**
   * How long the nightly purge keeps raw page views (issue #193).
   *
   * **Only the fields the screen was given**, the bank group's rule: a field this object omits is one the API
   * leaves alone.
   */
  updateTraffic(values: TrafficValues, rowVersion: string): Observable<void> {
    return this.put(AdminSettingsApi.trafficUrl, { ...values, rowVersion });
  }

  /** Whether reviews wait for approval (issue #189): a JSON true or false, never null. */
  updateReviews(requireApproval: boolean, rowVersion: string): Observable<void> {
    return this.put(AdminSettingsApi.reviewsUrl, { requireApproval, rowVersion });
  }

  /** The email account (Super Admin). A password is sent only when one was typed: absent keeps the saved one. */
  updateSmtp(
    values: SmtpTextValues & { readonly password?: string; readonly useStartTls: boolean },
    rowVersion: string,
  ): Observable<void> {
    return this.put(AdminSettingsApi.smtpUrl, { ...values, rowVersion });
  }

  /** Sends a test email to the signed-in admin through the account as typed. Saves nothing. */
  testSmtp(
    values: SmtpTextValues & { readonly password?: string; readonly useStartTls: boolean },
  ): Observable<SmtpTestResponse> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.post<SmtpTestResponse>(AdminSettingsApi.smtpTestUrl, values, {
        headers: { 'X-XSRF-TOKEN': token },
      }),
    );
  }

  /**
   * The product page's delivery and returns texts (issue #33). **Only the fields the screen was given**, the bank
   * group's rule. Answers with what the server's sanitiser removed, so the screen can say so.
   */
  updateProductPageInfo(values: ProductPageInfoValues, rowVersion: string): Observable<ProductPageInfoSaveResponse> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<ProductPageInfoSaveResponse>(
        AdminSettingsApi.productPageInfoUrl,
        { ...values, rowVersion },
        { headers: { 'X-XSRF-TOKEN': token } },
      ),
    );
  }

  private put(url: string, body: unknown): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.put<void>(url, body, { headers: { 'X-XSRF-TOKEN': token } }),
    );
  }
}
