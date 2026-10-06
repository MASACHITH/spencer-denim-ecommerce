import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { DistrictSelector } from './district-selector';
import {
  DELIVERY_DISTRICT_STORAGE_KEY,
  DeliveryDistrictService,
} from '../../core/delivery/delivery-district.service';
import { DELIVERY_DISTRICTS } from '../../core/delivery/district.data';

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
      providers: [{ provide: SafeLocalStorage, useValue: storage }],
    });

    const fixture = TestBed.createComponent(DistrictSelector);
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
    const label = (fixture.nativeElement as HTMLElement).querySelector('label');
    expect(label?.textContent?.trim()).toBe('Deliver to');
    expect(label?.getAttribute('for')).toBe(select.id);
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
