import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminSaleCampaign, AdminSaleCampaignRequest, AdminSaleCampaignSaved } from './admin-campaign.model';

/**
 * `/api/admin/marketing/sale-campaigns` (ADM-14, doc 11 P3 section A).
 *
 * A service of its own rather than a method on `AdminPromoApi`, which is the split that service's own
 * docblock asked for: doc 02 line 119 is a separate feature, and the two share nothing but a URL prefix
 * and four permissions.
 *
 * Every mutating call carries a freshly fetched antiforgery token through {@link AdminAuthApi}, so the
 * header name and the token endpoint live in one place.
 */
@Injectable({ providedIn: 'root' })
export class AdminCampaignApi {
  static readonly campaignsUrl = '/api/admin/marketing/sale-campaigns';

  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthApi);

  /** Every campaign that has not been removed, newest first — inactive, scheduled and ended included. */
  list(): Observable<readonly AdminSaleCampaign[]> {
    return this.http.get<readonly AdminSaleCampaign[]>(AdminCampaignApi.campaignsUrl);
  }

  /**
   * Creates a campaign, or replaces one.
   *
   * **Every field is sent, including `targets` and the empty ones**, and the endpoint refuses a body that
   * leaves one out rather than reading its absence as "leave it alone". For a campaign the stakes are
   * higher than the usual version of this argument: a save that dropped `targets` and kept the old ones
   * would leave a staff member believing they had narrowed a sale they had not, and the shop discounting
   * products they thought they had taken off it.
   *
   * The row version travels in `If-Match` on an edit, so an edit from a stale list is refused with 409
   * rather than overwriting somebody else's change.
   */
  save(
    campaignId: number | null,
    campaign: AdminSaleCampaignRequest,
    rowVersion: string | null,
  ): Observable<AdminSaleCampaignSaved> {
    return this.auth.withAntiforgeryToken((token) =>
      campaignId === null
        ? this.http.post<AdminSaleCampaignSaved>(AdminCampaignApi.campaignsUrl, campaign, {
            headers: { 'X-XSRF-TOKEN': token },
          })
        : this.http.put<AdminSaleCampaignSaved>(
            `${AdminCampaignApi.campaignsUrl}/${campaignId}`,
            campaign,
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
   * Removes a campaign — a soft delete, and doc 11 P3 step 5's third way to end a sale.
   *
   * The orders it priced keep their `saleCampaignId`, which is why the foreign key is NO ACTION and
   * nothing is hard-deleted. The affected products' stored summaries are recomputed in the same request,
   * so the Sale page stops advertising it without waiting for the five-minute job.
   */
  remove(campaignId: number, rowVersion: string): Observable<void> {
    return this.auth.withAntiforgeryToken((token) =>
      this.http.delete<void>(`${AdminCampaignApi.campaignsUrl}/${campaignId}`, {
        headers: { 'X-XSRF-TOKEN': token, 'If-Match': `"${rowVersion}"` },
      }),
    );
  }
}
