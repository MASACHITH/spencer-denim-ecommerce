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
 * **It drives checkout's initial district (CHK-01).** That is what this docblock promised and what
 * W3-02 at first did not do: the header said Colombo while the checkout form asked "Choose a district"
 * in three places, so the shop asked the same question twice and ignored the first answer.
 *
 * <b>{@link chosen} and {@link selected} are not the same question, and checkout needs the first one.</b>
 * `selected` is never null because the header always has to print a district, so it falls back to the
 * default. `chosen` is null until the visitor has actually picked one. A default is not a statement, and
 * checkout must not print a Colombo delivery fee to somebody in Jaffna who has said nothing — doc 08
 * §3.5 R7's fallbacks are a signed-in shopper's own district and a configured default zone, and neither
 * exists (ACC-01, ADM-22's `ops.Settings`), which is why `CheckoutSessionDto.SelectedDistrictId`
 * deliberately has no default and the page asks.
 */
@Injectable({ providedIn: 'root' })
export class DeliveryDistrictService {
  private readonly storage = inject(SafeLocalStorage);

  readonly districts: readonly District[] = DELIVERY_DISTRICTS;

  private readonly code = signal(DEFAULT_DISTRICT_CODE);

  /**
   * Whether {@link code} is the visitor's own answer rather than the fallback.
   *
   * It is always false on the server, where there is no storage to have read a choice from — so a
   * server-rendered checkout prices nothing and asks, exactly as it did before, and the browser corrects
   * it on hydration with whatever the visitor actually chose.
   */
  private readonly stated = signal(false);

  /** Never null: an unknown stored code falls back to the default district. */
  readonly selected: Signal<District> = computed(
    () => this.find(this.code()) ?? this.districts[0],
  );

  readonly selectedCode: Signal<string> = computed(() => this.selected().code);

  /**
   * The district the visitor has actually chosen, or **null** when the header is only showing the
   * default. See the class remarks for why the distinction matters to checkout.
   */
  readonly chosen: Signal<District | null> = computed(() =>
    this.stated() ? this.selected() : null,
  );

  constructor() {
    const stored = this.storage.read(DELIVERY_DISTRICT_STORAGE_KEY);
    if (stored !== null && this.find(stored)) {
      this.code.set(stored);
      this.stated.set(true);
    }
  }

  /** Ignores a code that is not a real district, so a stray value can never blank the header. */
  select(code: string): void {
    if (!this.find(code)) {
      return;
    }

    this.code.set(code);
    this.stated.set(true);
    this.storage.write(DELIVERY_DISTRICT_STORAGE_KEY, code);
  }

  private find(code: string): District | undefined {
    return this.districts.find((district) => district.code === code);
  }
}
