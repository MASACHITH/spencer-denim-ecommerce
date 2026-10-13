import { PromoCodeStatus, formatAmount } from './admin-promo.model';

/**
 * What kind of discount a sale campaign gives (ADM-14, doc 08 §3.6).
 *
 * Mirrors `SpencerDenim.Domain.Marketing.SaleDiscountType`. The API sends and accepts the member name,
 * because `Program.cs` registers a global `JsonStringEnumConverter`.
 *
 * **There is no `FreeDelivery` here, unlike a promo code's type.** A campaign prices a product; delivery
 * is doc 08 §6 layer 5 and a campaign never touches it.
 */
export type SaleDiscountType = 'Percent' | 'FixedAmount';

/** What a target points at (ADM-14, doc 11 P3 step 2). Mirrors `SaleCampaignTargetType`. */
export type SaleCampaignTargetType = 'Department' | 'Category' | 'Product';

/**
 * What the list says about a campaign right now.
 *
 * **`PromoCodeStatus`, deliberately, and not a second type with the same four members.** Doc 11 line 474
 * gives campaigns and promo codes one derived status — Scheduled → Live → Ended / Inactive / Deleted —
 * and the API answers a campaign's status from `CampaignWindow.StatusOf`, which is
 * `PromoCodeSchedule.StatusOf`. Two types here would be two places for the two screens to drift apart.
 *
 * **Derived from the dates and the active flag, never stored** (doc 11 line 474), so the list cannot
 * disagree with what the shop is charging. `Deleted` is not a member: a deleted campaign is not in the
 * list at all.
 */
export type SaleCampaignStatus = PromoCodeStatus;

/** One `marketing.SaleCampaignTargets` row as the admin screen draws and edits it. */
export interface AdminSaleCampaignTarget {
  readonly targetType: SaleCampaignTargetType;
  /** Set for a Department target, null otherwise. */
  readonly departmentId: number | null;
  /** Set for a Category target, null otherwise. */
  readonly categoryId: number | null;
  /** Set for a Product target, null otherwise. */
  readonly productId: number | null;
  /**
   * Whether a Category target covers its subtree (doc 08 §3.6, default on).
   *
   * Only meaningful for a Category — a department already covers every category under it and a product
   * has no subtree — and the API forces it true for the other two rather than storing a flag that means
   * nothing.
   */
  readonly includeSubcategories: boolean;
  /**
   * What the target is called, for the list: `Women`, `Women › Shirts & Blouses`, a product's name — or
   * `(removed from the catalogue)` when the row it names has been soft-deleted since.
   *
   * **Read-only.** The screen shows it and sends back the id.
   */
  readonly name: string;
}

/** One `marketing.SaleCampaigns` row as the admin screen draws and edits it (ADM-14, doc 11 P3 A). */
export interface AdminSaleCampaign {
  readonly id: number;
  readonly name: string;
  readonly description: string | null;
  readonly discountType: SaleDiscountType;
  /** The percentage (up to 90, doc 08 §3.6's own ceiling) or the LKR amount. Never null. */
  readonly discountValue: number;
  /**
   * **The shop's own time, not UTC**, as `yyyy-MM-ddTHH:mm:ss` with no offset — which is what a
   * `datetime-local` input reads and writes. The column is UTC and the server converts at its edge, so a
   * staff member who types 23:59 gets 23:59 in Colombo rather than 05:29 the next morning.
   */
  readonly startsAt: string;
  /** The same, and **required**: doc 02 line 119 is "for a date range". */
  readonly endsAt: string;
  readonly isActive: boolean;
  /**
   * Doc 11 P3 step 1's optional banner. Stored and returned; **nothing renders it yet** and this screen
   * does not set it — see `SaleCampaignsPage`.
   */
  readonly bannerAssetId: number | null;
  readonly targets: readonly AdminSaleCampaignTarget[];
  readonly status: SaleCampaignStatus;
  /** Base64 `rowversion`, round-tripped as an ETag so two staff members cannot overwrite each other. */
  readonly rowVersion: string;
}

/** What a save answers: the row, anything to say although it worked, and what the recompute did. */
export interface AdminSaleCampaignSaved {
  readonly campaign: AdminSaleCampaign;
  /**
   * Sentences about a save that **succeeded**. The one that matters is "this campaign's targets do not
   * reach any product": a campaign that discounts nothing looks identical on the screen to one that is
   * working, and the shop would simply never go on sale. The others are an end date already in the past
   * and the 90% ceiling having been reached.
   */
  readonly warnings: readonly string[];
  /**
   * How many products doc 11 P3 step 3's recompute covered. Reported because it is the one part of the
   * save a staff member cannot see, and zero is almost always a targeting mistake.
   */
  readonly productsRepriced: number;
}

/** One target in a save body. */
export interface AdminSaleCampaignTargetRequest {
  readonly targetType: SaleCampaignTargetType;
  readonly departmentId: number | null;
  readonly categoryId: number | null;
  readonly productId: number | null;
  readonly includeSubcategories: boolean;
}

/** The body a save sends. Every field, every time — see `AdminCampaignApi.save`. */
export interface AdminSaleCampaignRequest {
  readonly name: string;
  readonly description: string | null;
  readonly discountType: SaleDiscountType;
  readonly discountValue: number | null;
  readonly startsAt: string;
  readonly endsAt: string;
  readonly isActive: boolean;
  readonly bannerAssetId: number | null;
  readonly targets: readonly AdminSaleCampaignTargetRequest[];
}

/** What a discount is worth, in words, for the list. */
export function campaignDiscountLabel(campaign: AdminSaleCampaign): string {
  // `String` already drops trailing zeros for a JS number, so 20 prints "20" and 12.5 prints "12.5".
  return campaign.discountType === 'Percent'
    ? `${campaign.discountValue}% off`
    : `${formatAmount(campaign.discountValue)} off`;
}

/**
 * What the list calls one target.
 *
 * The subtree note is only added for a Category, and only when it is **off** — a category target covers
 * its subcategories by default (doc 08 §3.6), so saying so on every row would be noise on the common
 * case and the narrowing is the thing a staff member needs to see.
 */
export function campaignTargetLabel(target: AdminSaleCampaignTarget): string {
  if (target.targetType === 'Category' && !target.includeSubcategories) {
    return `${target.name} (this category only)`;
  }

  return target.name;
}

/** Doc 11 line 474's derived status, in the words the screen uses. */
export function campaignStatusLabel(status: SaleCampaignStatus): string {
  switch (status) {
    case 'Scheduled':
      return 'Starts later';
    case 'Ended':
      return 'Ended';
    case 'Inactive':
      return 'Switched off';
    default:
      return 'Live';
  }
}
