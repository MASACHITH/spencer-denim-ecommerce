import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { DELIVERY_DISTRICTS, DEFAULT_DISTRICT_CODE } from './district.data';
import { District } from './district.model';

/** Where the chosen district is remembered between visits. */
export const DELIVERY_DISTRICT_STORAGE_KEY = 'sd.delivery-district';

/**
 * The delivery district shown in the header (GLB-06).
 *
 * The choice is remembered in `localStorage` through {@link SafeLocalStorage}, which never throws
 * (lesson L1): a private window, blocked site data or a stale value all fall back to the default
 * district instead of breaking the header. On the server there is no storage, so server-rendered
 * HTML always shows the default.
 *
 * Later this drives the delivery estimate and fee (PDP-05, CHK-01); for now it only persists.
 */
@Injectable({ providedIn: 'root' })
export class DeliveryDistrictService {
  private readonly storage = inject(SafeLocalStorage);

  readonly districts: readonly District[] = DELIVERY_DISTRICTS;

  private readonly code = signal(DEFAULT_DISTRICT_CODE);

  /** Never null: an unknown stored code falls back to the default district. */
  readonly selected: Signal<District> = computed(
    () => this.find(this.code()) ?? this.districts[0],
  );

  readonly selectedCode: Signal<string> = computed(() => this.selected().code);

  constructor() {
    const stored = this.storage.read(DELIVERY_DISTRICT_STORAGE_KEY);
    if (stored !== null && this.find(stored)) {
      this.code.set(stored);
    }
  }

  /** Ignores a code that is not a real district, so a stray value can never blank the header. */
  select(code: string): void {
    if (!this.find(code)) {
      return;
    }

    this.code.set(code);
    this.storage.write(DELIVERY_DISTRICT_STORAGE_KEY, code);
  }

  private find(code: string): District | undefined {
    return this.districts.find((district) => district.code === code);
  }
}
