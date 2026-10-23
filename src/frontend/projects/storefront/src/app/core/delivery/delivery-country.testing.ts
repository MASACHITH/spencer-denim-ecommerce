import { Provider } from '@angular/core';

import { DELIVERY_COUNTRIES_OVERRIDE, DeliveryCountry } from './delivery-country.service';

/** Sri Lanka, an overseas country with a postcode, one with a state, one without either, one not served. */
export const TEST_COUNTRIES: readonly DeliveryCountry[] = [
  {
    code: 'LK',
    name: 'Sri Lanka',
    isDomestic: true,
    isAvailable: true,
    postalCodeRequired: false,
    regionRequired: false,
    fee: null,
    estimatedDaysMin: null,
    estimatedDaysMax: null,
  },
  {
    code: 'GB',
    name: 'United Kingdom',
    isDomestic: false,
    isAvailable: true,
    postalCodeRequired: true,
    regionRequired: false,
    fee: 9500,
    estimatedDaysMin: 7,
    estimatedDaysMax: 14,
  },
  {
    code: 'US',
    name: 'United States',
    isDomestic: false,
    isAvailable: true,
    postalCodeRequired: true,
    regionRequired: true,
    fee: 10500,
    estimatedDaysMin: 7,
    estimatedDaysMax: 14,
  },
  {
    code: 'AE',
    name: 'United Arab Emirates',
    isDomestic: false,
    isAvailable: true,
    postalCodeRequired: false,
    regionRequired: false,
    fee: 7500,
    estimatedDaysMin: 5,
    estimatedDaysMax: 10,
  },
  {
    code: 'AQ',
    name: 'Antarctica',
    isDomestic: false,
    isAvailable: false,
    postalCodeRequired: false,
    regionRequired: false,
    fee: null,
    estimatedDaysMin: null,
    estimatedDaysMax: null,
  },
];

/** Answers the country list without HTTP, so a spec's `HttpTestingController.verify()` stays meaningful. */
export function provideTestDeliveryCountries(
  countries: readonly DeliveryCountry[] = TEST_COUNTRIES,
): Provider {
  return { provide: DELIVERY_COUNTRIES_OVERRIDE, useValue: countries };
}
