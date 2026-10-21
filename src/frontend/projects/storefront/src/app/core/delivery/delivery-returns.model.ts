/**
 * The product page's delivery and returns panel, as `GET /api/content/delivery-returns` sends it (PDP-05,
 * issue #33). Mirrors `DeliveryReturnsDto` in the API; `zone` is the API's `DeliveryZone` enum name.
 *
 * **The same for every visitor.** Every zone's fee is listed rather than "your" fee, so the server-rendered
 * product page can be cached and never carries one shopper's district into another's HTML.
 */
export type DeliveryZoneName = 'Colombo' | 'Western' | 'IslandWide';

export interface DeliveryZoneDto {
  readonly zone: DeliveryZoneName;
  /** "Colombo", "Gampaha and Kalutara", "Rest of Sri Lanka": built by the API from the districts. Unique. */
  readonly label: string;
  /**
   * The active districts in the zone. Not drawn: used only in the browser, after hydration, to mark the zone of
   * the district chosen in the header, so the server-rendered HTML stays the same for every visitor.
   */
  readonly districts: readonly string[];
}

export interface DeliveryRateDto {
  readonly zone: DeliveryZoneName;
  readonly fee: number;
  /** The order value from which delivery is free, or null when it never is. */
  readonly freeAboveAmount: number | null;
  readonly daysMin: number;
  readonly daysMax: number;
}

export interface DeliveryMethodInfoDto {
  readonly code: string;
  readonly name: string;
  readonly description: string | null;
  readonly rates: readonly DeliveryRateDto[];
}

export interface PickupPointDto {
  readonly name: string;
  readonly city: string;
  readonly openingHours: string | null;
  readonly fee: number;
  readonly daysMin: number;
  readonly daysMax: number;
}

export interface DeliveryEstimateDto {
  readonly daysMin: number;
  readonly daysMax: number;
}

export interface DeliveryReturnsDto {
  readonly estimate: DeliveryEstimateDto | null;
  readonly zones: readonly DeliveryZoneDto[];
  readonly methods: readonly DeliveryMethodInfoDto[];
  /** Empty unless store pickup is enabled and an active pickup location exists. */
  readonly pickupPoints: readonly PickupPointDto[];
  /** Already-sanitised HTML; empty when the owner has set none. */
  readonly deliveryInfoHtml: string;
  readonly returnsInfoHtml: string;
}

/** "1 working day", "2–3 working days". */
export function formatWorkingDays(min: number, max: number): string {
  if (min === max) {
    return `${min} working ${min === 1 ? 'day' : 'days'}`;
  }

  return `${min}–${max} working days`;
}
