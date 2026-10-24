import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, Signal, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { SafeLocalStorage } from '@spencer-denim/shared';
import { Observable, catchError, map, of, tap, timeout } from 'rxjs';

import { ServerHandover } from '../platform/server-handover';

/**
 * One row of `GET /api/delivery/countries` (owner decision 2026-10-21: Sri Lanka plus worldwide delivery).
 * Mirrors `SpencerDenim.Application.Sales.Delivery.DeliveryCountryDto`.
 */
export interface DeliveryCountry {
  /** ISO 3166-1 alpha-2. */
  readonly code: string;
  readonly name: string;
  /** True for Sri Lanka only, which keeps the district selector and is priced by district. */
  readonly isDomestic: boolean;
  /** Whether an order can be sent here right now. Always true for Sri Lanka. */
  readonly isAvailable: boolean;
  readonly postalCodeRequired: boolean;
  readonly regionRequired: boolean;
  /** Standard delivery fee in LKR; null for Sri Lanka and for a country without standard delivery. */
  readonly fee: number | null;
  readonly estimatedDaysMin: number | null;
  readonly estimatedDaysMax: number | null;
  /** Explicit standard fee (same as `fee`); null when not available. */
  readonly standardFee: number | null;
  readonly standardEstimatedDaysMin: number | null;
  readonly standardEstimatedDaysMax: number | null;
  /** Express delivery fee in LKR; null when express is not configured for this country. */
  readonly expressFee: number | null;
  readonly expressEstimatedDaysMin: number | null;
  readonly expressEstimatedDaysMax: number | null;
}

/** What a shopper is told about delivery to one overseas country. */
export interface OverseasEstimate {
  readonly country: DeliveryCountry;
  /** False when the shop does not deliver there at the moment; the figures are then null. */
  readonly isAvailable: boolean;
  readonly fee: number | null;
  readonly estimatedDaysMin: number | null;
  readonly estimatedDaysMax: number | null;
}

export const SRI_LANKA_CODE = 'LK';

/** Where the chosen country is remembered between visits. */
export const DELIVERY_COUNTRY_STORAGE_KEY = 'sd.delivery-country';

export const DELIVERY_COUNTRIES_URL = '/api/delivery/countries';

export const DELIVERY_COUNTRIES_TIMEOUT_MS = 3_000;

const COUNTRIES_HANDOVER = 'delivery:countries';

/**
 * A fixed country list in place of the API, for unit tests of components that render the header or a
 * country selector and would otherwise each have to answer `GET /api/delivery/countries`.
 */
export const DELIVERY_COUNTRIES_OVERRIDE = new InjectionToken<readonly DeliveryCountry[]>(
  'DELIVERY_COUNTRIES_OVERRIDE',
);

/** Used until the list arrives, and if it cannot be read: the shop always delivers in Sri Lanka. */
export const SRI_LANKA_ONLY: readonly DeliveryCountry[] = [
  {
    code: SRI_LANKA_CODE,
    name: 'Sri Lanka',
    isDomestic: true,
    isAvailable: true,
    postalCodeRequired: false,
    regionRequired: false,
    fee: null,
    estimatedDaysMin: null,
    estimatedDaysMax: null,
    standardFee: null,
    standardEstimatedDaysMin: null,
    standardEstimatedDaysMax: null,
    expressFee: null,
    expressEstimatedDaysMin: null,
    expressEstimatedDaysMax: null,
  },
];

/**
 * The delivery country (GLB-06, owner decision 2026-10-21), beside {@link DeliveryDistrictService}'s
 * district: Sri Lanka keeps the district selector, every other country is priced by country.
 *
 * <b>For the product-page delivery panel</b> (built on `feature/33-delivery-returns`): inject this service
 * and read {@link isOverseas}; when true, print "Delivery to {@link selected}().name" with
 * {@link overseasEstimate} (fee and working days, or "not at the moment" when `isAvailable` is false);
 * when false, keep the Sri Lankan district estimate. Nothing on the product page itself changes here.
 *
 * The choice is remembered in `localStorage` through {@link SafeLocalStorage}; on the server there is no
 * storage, so server-rendered HTML always shows Sri Lanka and the browser corrects it on hydration. The list
 * is handed from the server render to the browser so it is fetched once per visit.
 */
@Injectable({ providedIn: 'root' })
export class DeliveryCountryService {
  private readonly http = inject(HttpClient);
  private readonly storage = inject(SafeLocalStorage);
  private readonly handover = inject(ServerHandover);
  private readonly override = inject(DELIVERY_COUNTRIES_OVERRIDE, { optional: true });

  /** Every country, Sri Lanka first. Never empty. */
  readonly countries: Signal<readonly DeliveryCountry[]> = toSignal(this.load(), {
    initialValue: SRI_LANKA_ONLY,
  });

  private readonly code = signal(SRI_LANKA_CODE);

  /** Whether {@link code} is the visitor's own answer rather than the default. */
  private readonly stated = signal(false);

  /**
   * Never null. An unknown stored code reads as Sri Lanka — but only once the list is known, so a stored
   * country is not thrown away while the list is still on its way.
   */
  readonly selected: Signal<DeliveryCountry> = computed(
    () => this.find(this.code()) ?? this.countries()[0] ?? SRI_LANKA_ONLY[0],
  );

  readonly selectedCode: Signal<string> = computed(() => this.selected().code);

  /** The country the visitor actually chose, or null while only the default is showing. */
  readonly chosen: Signal<DeliveryCountry | null> = computed(() =>
    this.stated() && this.find(this.code()) ? this.selected() : null,
  );

  /**
   * The code the visitor chose, even before the list has arrived to confirm it, or null for no choice.
   * Checkout asks the server with it: the server knows every country and says so if it does not.
   */
  readonly chosenCode: Signal<string | null> = computed(() => (this.stated() ? this.code() : null));

  readonly isOverseas: Signal<boolean> = computed(() => !this.selected().isDomestic);

  /** Fee and days for the selected overseas country, or null for Sri Lanka. */
  readonly overseasEstimate: Signal<OverseasEstimate | null> = computed(() => {
    const country = this.selected();
    return country.isDomestic ? null : estimateFor(country);
  });

  constructor() {
    const stored = this.storage.read(DELIVERY_COUNTRY_STORAGE_KEY);
    if (stored !== null && /^[A-Za-z]{2}$/.test(stored)) {
      this.code.set(stored.toUpperCase());
      this.stated.set(true);
    }
  }

  /** Ignores a code that is not in the list, so a stray value can never blank the header. */
  select(code: string): void {
    const upper = code.trim().toUpperCase();
    if (!this.find(upper)) {
      return;
    }

    this.code.set(upper);
    this.stated.set(true);
    this.storage.write(DELIVERY_COUNTRY_STORAGE_KEY, upper);
  }

  /** One country by code, or undefined. */
  find(code: string): DeliveryCountry | undefined {
    const upper = code.toUpperCase();
    return this.countries().find((country) => country.code === upper);
  }

  private load(): Observable<readonly DeliveryCountry[]> {
    if (this.override) {
      return of(toCountries(this.override));
    }

    const handedOver = this.handover.take<readonly DeliveryCountry[]>(COUNTRIES_HANDOVER);
    if (handedOver && handedOver.length > 0) {
      return of(handedOver);
    }

    return this.http.get<readonly DeliveryCountry[]>(DELIVERY_COUNTRIES_URL).pipe(
      timeout(DELIVERY_COUNTRIES_TIMEOUT_MS),
      map(toCountries),
      tap((countries) => this.handover.give(COUNTRIES_HANDOVER, countries)),
      catchError(() => of(SRI_LANKA_ONLY)),
    );
  }
}

/** The usable rows of the API's answer, Sri Lanka guaranteed first; Sri-Lanka-only when unusable. */
export function toCountries(rows: unknown): readonly DeliveryCountry[] {
  if (!Array.isArray(rows)) {
    return SRI_LANKA_ONLY;
  }

  const usable = rows.filter(
    (row): row is DeliveryCountry =>
      typeof row === 'object' &&
      row !== null &&
      typeof (row as DeliveryCountry).code === 'string' &&
      /^[A-Z]{2}$/.test((row as DeliveryCountry).code) &&
      typeof (row as DeliveryCountry).name === 'string' &&
      (row as DeliveryCountry).name.trim().length > 0,
  );

  const domestic = usable.find((row) => row.code === SRI_LANKA_CODE) ?? SRI_LANKA_ONLY[0];
  return [domestic, ...usable.filter((row) => row.code !== SRI_LANKA_CODE)];
}

export function estimateFor(country: DeliveryCountry): OverseasEstimate {
  const available = country.isAvailable && country.fee !== null;
  return {
    country,
    isAvailable: available,
    fee: available ? country.fee : null,
    estimatedDaysMin: available ? country.estimatedDaysMin : null,
    estimatedDaysMax: available ? country.estimatedDaysMax : null,
  };
}

/** "5–10 working days", "7 working days", or null when there is no estimate. */
export function workingDays(min: number | null, max: number | null): string | null {
  if (min === null || max === null) {
    return null;
  }

  return min === max
    ? `${min} working ${min === 1 ? 'day' : 'days'}`
    : `${min}–${max} working days`;
}
