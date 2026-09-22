/** Delivery zone, as stored on `ref.Districts.DeliveryZone` (docs/08 §3.5). */
export enum DeliveryZone {
  Colombo = 1,
  Western = 2,
  IslandWide = 3,
}

/** Mirrors a row of `ref.Districts` (docs/08 §3.2), which an API will serve for GLB-06/CHK-01. */
export interface District {
  readonly id: number;
  readonly code: string;
  readonly name: string;
  readonly deliveryZone: DeliveryZone;
}
