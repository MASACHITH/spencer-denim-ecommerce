import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import {
  DELIVERY_COUNTRIES_URL,
  DELIVERY_COUNTRY_STORAGE_KEY,
  DeliveryCountryService,
  SRI_LANKA_ONLY,
  estimateFor,
  toCountries,
  workingDays,
} from './delivery-country.service';
import { TEST_COUNTRIES } from './delivery-country.testing';

class StorageStub {
  readonly entries = new Map<string, string>();

  read(key: string): string | null {
    return this.entries.get(key) ?? null;
  }

  write(key: string, value: string): boolean {
    this.entries.set(key, value);
    return true;
  }

  remove(key: string): boolean {
    return this.entries.delete(key);
  }
}

describe('DeliveryCountryService (GLB-06, owner decision 2026-10-21)', () => {
  let configured: HttpTestingController | undefined;

  function setUp(storage = new StorageStub()) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SafeLocalStorage, useValue: storage },
      ],
    });

    const service = TestBed.inject(DeliveryCountryService);
    const http = TestBed.inject(HttpTestingController);
    configured = http;

    return { service, http, storage };
  }

  afterEach(() => {
    configured?.verify();
    configured = undefined;
  });

  it('starts on Sri Lanka only, then lists what the API returned', () => {
    const { service, http } = setUp();

    expect(service.countries()).toEqual(SRI_LANKA_ONLY);
    http.expectOne(DELIVERY_COUNTRIES_URL).flush(TEST_COUNTRIES);

    expect(service.countries().map((country) => country.code)).toEqual(['LK', 'GB', 'US', 'AE', 'AQ']);
    expect(service.selected().code).toBe('LK');
    expect(service.isOverseas()).toBe(false);
    expect(service.chosen()).toBeNull();
    expect(service.overseasEstimate()).toBeNull();
  });

  it('falls back to Sri Lanka only when the list cannot be read', () => {
    const { service, http } = setUp();

    http.expectOne(DELIVERY_COUNTRIES_URL).flush('boom', { status: 500, statusText: 'Server Error' });

    expect(service.countries()).toEqual(SRI_LANKA_ONLY);
  });

  it('remembers a choice and prices the overseas estimate', () => {
    const { service, http, storage } = setUp();
    http.expectOne(DELIVERY_COUNTRIES_URL).flush(TEST_COUNTRIES);

    service.select('gb');

    expect(storage.entries.get(DELIVERY_COUNTRY_STORAGE_KEY)).toBe('GB');
    expect(service.isOverseas()).toBe(true);
    expect(service.chosen()?.name).toBe('United Kingdom');
    expect(service.overseasEstimate()).toMatchObject({
      isAvailable: true,
      fee: 9500,
      estimatedDaysMin: 7,
      estimatedDaysMax: 14,
    });
  });

  it('ignores a code that is not in the list', () => {
    const { service, http, storage } = setUp();
    http.expectOne(DELIVERY_COUNTRIES_URL).flush(TEST_COUNTRIES);

    service.select('ZZ');

    expect(service.selected().code).toBe('LK');
    expect(storage.entries.has(DELIVERY_COUNTRY_STORAGE_KEY)).toBe(false);
  });

  it('restores a stored country once the list confirms it, and reads an unknown one as Sri Lanka', () => {
    const storage = new StorageStub();
    storage.entries.set(DELIVERY_COUNTRY_STORAGE_KEY, 'AE');
    const { service, http } = setUp(storage);

    // Until the list arrives the header still shows Sri Lanka, but checkout already knows the choice.
    expect(service.selected().code).toBe('LK');
    expect(service.chosenCode()).toBe('AE');

    http.expectOne(DELIVERY_COUNTRIES_URL).flush(TEST_COUNTRIES);
    expect(service.selected().code).toBe('AE');
  });

  it('treats a stored value that is not a code as no choice', () => {
    const storage = new StorageStub();
    storage.entries.set(DELIVERY_COUNTRY_STORAGE_KEY, 'not-a-country');
    const { service, http } = setUp(storage);
    http.expectOne(DELIVERY_COUNTRIES_URL).flush(TEST_COUNTRIES);

    expect(service.chosenCode()).toBeNull();
    expect(service.selected().code).toBe('LK');
  });

  it('reports a country it does not deliver to as unavailable, without figures', () => {
    const { service, http } = setUp();
    http.expectOne(DELIVERY_COUNTRIES_URL).flush(TEST_COUNTRIES);

    service.select('AQ');

    expect(service.overseasEstimate()).toMatchObject({ isAvailable: false, fee: null });
  });

  describe('toCountries', () => {
    it('puts Sri Lanka first and drops malformed rows', () => {
      const rows = [TEST_COUNTRIES[1], { code: 'x', name: 'Bad' }, TEST_COUNTRIES[0]];
      expect(toCountries(rows).map((country) => country.code)).toEqual(['LK', 'GB']);
    });

    it('answers Sri Lanka only for something that is not a list', () => {
      expect(toCountries({ nope: true })).toEqual(SRI_LANKA_ONLY);
    });

    it('keeps Sri Lanka even when the API left it out', () => {
      expect(toCountries([TEST_COUNTRIES[1]])[0].code).toBe('LK');
    });
  });

  it('never prices an estimate without a fee', () => {
    expect(estimateFor({ ...TEST_COUNTRIES[1], fee: null }).isAvailable).toBe(false);
  });

  it('words the working days', () => {
    expect(workingDays(7, 14)).toBe('7–14 working days');
    expect(workingDays(1, 1)).toBe('1 working day');
    expect(workingDays(5, 5)).toBe('5 working days');
    expect(workingDays(null, 3)).toBeNull();
  });
});
