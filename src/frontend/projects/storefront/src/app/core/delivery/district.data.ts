import { DeliveryZone, District } from './district.model';

/**
 * Placeholder district list for GLB-06, in the shape of `ref.Districts` (docs/08 §3.2):
 * the 25 districts of Sri Lanka with their ISO 3166-2 codes.
 *
 * TODO(W1-03): read this from the reference-data API once it exists. The zone each district
 * sits in is admin-editable (`Settings.Edit`) and drives delivery fees and estimates, so the
 * assignment below is a starting point, not a decision.
 */
export const DELIVERY_DISTRICTS: readonly District[] = [
  { id: 1, code: 'LK-11', name: 'Colombo', deliveryZone: DeliveryZone.Colombo },
  { id: 2, code: 'LK-12', name: 'Gampaha', deliveryZone: DeliveryZone.Western },
  { id: 3, code: 'LK-13', name: 'Kalutara', deliveryZone: DeliveryZone.Western },
  { id: 4, code: 'LK-21', name: 'Kandy', deliveryZone: DeliveryZone.IslandWide },
  { id: 5, code: 'LK-22', name: 'Matale', deliveryZone: DeliveryZone.IslandWide },
  { id: 6, code: 'LK-23', name: 'Nuwara Eliya', deliveryZone: DeliveryZone.IslandWide },
  { id: 7, code: 'LK-31', name: 'Galle', deliveryZone: DeliveryZone.IslandWide },
  { id: 8, code: 'LK-32', name: 'Matara', deliveryZone: DeliveryZone.IslandWide },
  { id: 9, code: 'LK-33', name: 'Hambantota', deliveryZone: DeliveryZone.IslandWide },
  { id: 10, code: 'LK-41', name: 'Jaffna', deliveryZone: DeliveryZone.IslandWide },
  { id: 11, code: 'LK-42', name: 'Kilinochchi', deliveryZone: DeliveryZone.IslandWide },
  { id: 12, code: 'LK-43', name: 'Mannar', deliveryZone: DeliveryZone.IslandWide },
  { id: 13, code: 'LK-44', name: 'Vavuniya', deliveryZone: DeliveryZone.IslandWide },
  { id: 14, code: 'LK-45', name: 'Mullaitivu', deliveryZone: DeliveryZone.IslandWide },
  { id: 15, code: 'LK-51', name: 'Batticaloa', deliveryZone: DeliveryZone.IslandWide },
  { id: 16, code: 'LK-52', name: 'Ampara', deliveryZone: DeliveryZone.IslandWide },
  { id: 17, code: 'LK-53', name: 'Trincomalee', deliveryZone: DeliveryZone.IslandWide },
  { id: 18, code: 'LK-61', name: 'Kurunegala', deliveryZone: DeliveryZone.IslandWide },
  { id: 19, code: 'LK-62', name: 'Puttalam', deliveryZone: DeliveryZone.IslandWide },
  { id: 20, code: 'LK-71', name: 'Anuradhapura', deliveryZone: DeliveryZone.IslandWide },
  { id: 21, code: 'LK-72', name: 'Polonnaruwa', deliveryZone: DeliveryZone.IslandWide },
  { id: 22, code: 'LK-81', name: 'Badulla', deliveryZone: DeliveryZone.IslandWide },
  { id: 23, code: 'LK-82', name: 'Monaragala', deliveryZone: DeliveryZone.IslandWide },
  { id: 24, code: 'LK-91', name: 'Ratnapura', deliveryZone: DeliveryZone.IslandWide },
  { id: 25, code: 'LK-92', name: 'Kegalle', deliveryZone: DeliveryZone.IslandWide },
];

/** Shown until the visitor chooses, and when a stored choice is no longer a real district. */
export const DEFAULT_DISTRICT_CODE = 'LK-11';
