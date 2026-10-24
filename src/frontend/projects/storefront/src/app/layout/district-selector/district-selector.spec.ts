import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { DistrictSelector } from './district-selector';
import {
  DELIVERY_DISTRICT_STORAGE_KEY,
  DeliveryDistrictService,
} from '../../core/delivery/delivery-district.service';
import { DELIVERY_DISTRICTS } from '../../core/delivery/district.data';
import {
  DELIVERY_COUNTRIES_URL,
  DELIVERY_COUNTRY_STORAGE_KEY,
  DeliveryCountry,
  DeliveryCountryService,
} from '../../core/delivery/delivery-country.service';

const COUNTRIES: readonly DeliveryCountry[] = [
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
    standardFee: null,
    standardEstimatedDaysMin: null,
    standardEstimatedDaysMax: null,
    expressFee: null,
    expressEstimatedDaysMin: null,
    expressEstimatedDaysMax: null,
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
    standardFee: 9500,
    standardEstimatedDaysMin: 7,
    standardEstimatedDaysMax: 14,
    expressFee: null,
    expressEstimatedDaysMin: null,
    expressEstimatedDaysMax: null,
  },
];

/** An in-memory stand-in for `localStorage`, so a test never depends on the real one. */
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

/** Storage that refuses everything, the way a private window can (lesson L1). */
class BlockedStorageStub extends StorageStub {
  override read(): null {
    return null;
  }

  override write(): boolean {
    return false;
  }
}

describe('DistrictSelector (GLB-06)', () => {
  function setUp(storage: StorageStub) {
    TestBed.configureTestingModule({
      imports: [DistrictSelector],
      providers: [
        { provide: SafeLocalStorage, useValue: storage },
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    const fixture = TestBed.createComponent(DistrictSelector);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne(DELIVERY_COUNTRIES_URL).flush(COUNTRIES);
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="district-select"]',
    ) as HTMLSelectElement;

    return { fixture, select, service: TestBed.inject(DeliveryDistrictService), storage };
  }

  function choose(select: HTMLSelectElement, code: string): void {
    select.value = code;
    select.dispatchEvent(new Event('change', { bubbles: true }));
  }

  it('lists every district and labels the control', () => {
    const { fixture, select } = setUp(new StorageStub());

    expect(select.options).toHaveLength(DELIVERY_DISTRICTS.length);
    const host = fixture.nativeElement as HTMLElement;
    const district = host.querySelector(`label[for="${select.id}"]`);
    expect(district?.textContent?.trim()).toBe('District');

    const country = host.querySelector('[data-testid="country-select"]') as HTMLSelectElement;
    const label = host.querySelector(`label[for="${country.id}"]`);
    expect(label?.textContent?.trim()).toBe('Deliver to');
  });

  describe('the country (owner decision 2026-10-21)', () => {
    function countrySelect(fixture: { nativeElement: unknown }): HTMLSelectElement {
      return (fixture.nativeElement as HTMLElement).querySelector(
        '[data-testid="country-select"]',
      ) as HTMLSelectElement;
    }

    it('lists the countries the API returned, Sri Lanka selected by default', () => {
      const { fixture } = setUp(new StorageStub());

      const select = countrySelect(fixture);
      expect(Array.from(select.options).map((option) => option.value)).toEqual(['LK', 'GB']);
      expect(select.value).toBe('LK');
    });

    it('hides the district picker for an overseas country and remembers the choice', () => {
      const storage = new StorageStub();
      const { fixture } = setUp(storage);

      choose(countrySelect(fixture), 'GB');
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      expect(host.querySelector('[data-testid="district-select"]')).toBeNull();
      expect(storage.entries.get(DELIVERY_COUNTRY_STORAGE_KEY)).toBe('GB');
      expect(TestBed.inject(DeliveryCountryService).overseasEstimate()?.fee).toBe(9500);
    });

    it('shows the district picker again for Sri Lanka', () => {
      const storage = new StorageStub();
      storage.entries.set(DELIVERY_COUNTRY_STORAGE_KEY, 'GB');
      const { fixture } = setUp(storage);
      expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="district-select"]')).toBeNull();

      choose(countrySelect(fixture), 'LK');
      fixture.detectChanges();

      expect(
        (fixture.nativeElement as HTMLElement).querySelector('[data-testid="district-select"]'),
      ).not.toBeNull();
    });
  });

  it('starts on the default district when nothing has been chosen', () => {
    const { select, service } = setUp(new StorageStub());

    expect(service.selected().name).toBe('Colombo');
    expect(select.value).toBe('LK-11');
  });

  it('remembers the choice', () => {
    const { select, service, storage } = setUp(new StorageStub());

    choose(select, 'LK-21');

    expect(service.selected().name).toBe('Kandy');
    expect(storage.entries.get(DELIVERY_DISTRICT_STORAGE_KEY)).toBe('LK-21');
  });

  it('restores the district stored on an earlier visit', () => {
    const storage = new StorageStub();
    storage.entries.set(DELIVERY_DISTRICT_STORAGE_KEY, 'LK-31');

    const { select, service } = setUp(storage);

    expect(service.selected().name).toBe('Galle');
    expect(select.value).toBe('LK-31');
  });

  it('falls back to the default when the stored value is not a district', () => {
    const storage = new StorageStub();
    storage.entries.set(DELIVERY_DISTRICT_STORAGE_KEY, 'not-a-district');

    const { service } = setUp(storage);

    expect(service.selected().name).toBe('Colombo');
  });

  it('ignores a change to a code that is not a district', () => {
    const { service } = setUp(new StorageStub());

    service.select('LK-99');

    expect(service.selected().name).toBe('Colombo');
  });

  it('still works when storage is blocked, it simply does not persist', () => {
    const { select, service } = setUp(new BlockedStorageStub());

    expect(() => choose(select, 'LK-21')).not.toThrow();
    expect(service.selected().name).toBe('Kandy');
  });

  /**
   * **`chosen` and `selected` are not the same question, and checkout needs the first one.** The header
   * is never blank, so `selected` falls back to Colombo; pre-selecting that on checkout would print a
   * Colombo delivery fee to somebody in Jaffna who has said nothing, which is exactly what
   * `CheckoutSessionDto.SelectedDistrictId` has no default in order to avoid.
   */
  describe('chosen, which is what CHK-01 reads', () => {
    it('is null until the visitor has actually chosen', () => {
      const { service } = setUp(new StorageStub());

      expect(service.selected().name).toBe('Colombo');
      expect(service.chosen()).toBeNull();
    });

    it('is the choice once one has been made', () => {
      const { select, service } = setUp(new StorageStub());

      choose(select, 'LK-21');

      expect(service.chosen()?.name).toBe('Kandy');
    });

    it('is the stored choice on a later visit', () => {
      const storage = new StorageStub();
      storage.entries.set(DELIVERY_DISTRICT_STORAGE_KEY, 'LK-31');

      const { service } = setUp(storage);

      expect(service.chosen()?.name).toBe('Galle');
    });

    /** A stored value that is not a district is not a choice either. */
    it('stays null when the stored value is not a district', () => {
      const storage = new StorageStub();
      storage.entries.set(DELIVERY_DISTRICT_STORAGE_KEY, 'not-a-district');

      const { service } = setUp(storage);

      expect(service.chosen()).toBeNull();
    });
  });
});
