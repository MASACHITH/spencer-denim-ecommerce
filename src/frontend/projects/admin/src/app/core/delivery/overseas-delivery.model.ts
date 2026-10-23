/**
 * `GET /api/admin/delivery/overseas` (owner decision 2026-10-21: Sri Lanka plus worldwide delivery).
 *
 * A hand-written copy of `SpencerDenim.Application.Sales.Delivery.Admin.OverseasDeliveryAdminDto` and its rows.
 * Every write answers with the whole screen in this shape, so the page replaces its state from the reply
 * rather than guessing what the server now holds.
 */
export interface OverseasDeliveryScreen {
  /** Whether International delivery — every overseas order — is switched on. */
  readonly internationalEnabled: boolean;
  /** The International delivery method's concurrency token; null when the row is missing. */
  readonly internationalRowVersion: string | null;
  readonly regions: readonly DeliveryRegionRow[];
  /** Every country except Sri Lanka, by name. */
  readonly countries: readonly CountryDeliveryRow[];
}

export interface DeliveryRegionRow {
  readonly code: string;
  readonly name: string;
  /** LKR. Null means "not priced": its countries cannot be ordered to unless they set their own fee. */
  readonly fee: number | null;
  readonly estimatedDaysMin: number;
  readonly estimatedDaysMax: number;
  readonly isEnabled: boolean;
  readonly countryCount: number;
  readonly rowVersion: string;
}

/** Why a country cannot be ordered to, by name, or `None`. */
export type OverseasRefusal =
  | 'None'
  | 'MethodDisabled'
  | 'CountryDisabled'
  | 'RegionDisabled'
  | 'NotPriced';

export interface CountryDeliveryRow {
  readonly code: string;
  readonly name: string;
  readonly regionCode: string;
  readonly isShippingEnabled: boolean;
  /** The country's own fee, or null to use its region's. */
  readonly deliveryFeeOverride: number | null;
  /** The country's own estimate (both or neither), or null to use its region's. */
  readonly estimatedDaysMinOverride: number | null;
  readonly estimatedDaysMaxOverride: number | null;
  readonly postalCodeRequired: boolean;
  readonly regionRequired: boolean;
  /** What a shopper is actually offered once the region and the switches are applied. */
  readonly isAvailable: boolean;
  readonly effectiveFee: number | null;
  readonly effectiveDaysMin: number | null;
  readonly effectiveDaysMax: number | null;
  readonly refusal: OverseasRefusal | string;
  readonly rowVersion: string;
}

/** `PUT /api/admin/delivery/overseas/regions/{code}`: every field, every save. */
export interface DeliveryRegionUpdate {
  readonly fee: number | null;
  readonly estimatedDaysMin: number | null;
  readonly estimatedDaysMax: number | null;
  readonly isEnabled: boolean;
  readonly rowVersion: string;
}

/** `PUT /api/admin/delivery/overseas/countries/{code}`: every field, every save. */
export interface CountryDeliveryUpdate {
  readonly isShippingEnabled: boolean;
  readonly deliveryFeeOverride: number | null;
  readonly estimatedDaysMinOverride: number | null;
  readonly estimatedDaysMaxOverride: number | null;
  readonly rowVersion: string;
}

/**
 * The bounds the server holds a fee and an estimate to (`OverseasDeliveryRules`). Checked here only so the
 * staff member hears about a typo before a round trip; the API refuses regardless.
 */
export const OVERSEAS_RULES = {
  maxFee: 1_000_000,
  minDays: 1,
  maxDays: 90,
} as const;

/** The permissions this screen needs: `Settings.View` to look, `Settings.Edit` to change. */
export const OVERSEAS_PERMISSIONS = {
  view: 'Settings.View',
  edit: 'Settings.Edit',
} as const;

/** The sentence for a refusal, as a staff member reads it. */
export function refusalText(refusal: string): string {
  switch (refusal) {
    case 'MethodDisabled':
      return 'International delivery is switched off';
    case 'CountryDisabled':
      return 'Switched off for this country';
    case 'RegionDisabled':
      return 'Its region is switched off';
    case 'NotPriced':
      return 'No fee set for it or its region';
    default:
      return 'Not available';
  }
}

/**
 * Problems with a fee box, or null. Blank is allowed only where `allowBlank` (a region's "not priced", or a
 * country's "use the region's fee").
 */
export function feeProblem(text: string, allowBlank: boolean): string | null {
  const trimmed = text.trim();
  if (trimmed === '') {
    return allowBlank ? null : 'Enter a fee.';
  }

  if (!/^\d+(\.\d{1,2})?$/.test(trimmed)) {
    return 'Enter a fee in rupees, with at most two decimal places.';
  }

  return Number(trimmed) > OVERSEAS_RULES.maxFee ? 'Enter a fee from Rs. 0 to Rs. 1,000,000.' : null;
}

/** Problems with a pair of day boxes, or null. Both blank is allowed only where `allowBlank`. */
export function daysProblem(minText: string, maxText: string, allowBlank: boolean): string | null {
  const min = minText.trim();
  const max = maxText.trim();

  if (min === '' && max === '') {
    return allowBlank ? null : 'Enter the estimated delivery days.';
  }

  if (min === '' || max === '') {
    return 'Enter both the fewest and the most days, or neither.';
  }

  if (!/^\d+$/.test(min) || !/^\d+$/.test(max)) {
    return 'Enter whole days.';
  }

  const low = Number(min);
  const high = Number(max);

  return low < OVERSEAS_RULES.minDays || high > OVERSEAS_RULES.maxDays || low > high
    ? `Enter days from ${OVERSEAS_RULES.minDays} to ${OVERSEAS_RULES.maxDays}, with the fewest no more than the most.`
    : null;
}

/** A box's text as the API wants it: null for blank, the number otherwise. */
export function numberOrNull(text: string): number | null {
  const trimmed = text.trim();
  return trimmed === '' ? null : Number(trimmed);
}
