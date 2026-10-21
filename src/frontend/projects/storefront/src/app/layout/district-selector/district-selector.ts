import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Icon } from '@spencer-denim/shared';

import { DeliveryCountryService } from '../../core/delivery/delivery-country.service';
import { DeliveryDistrictService } from '../../core/delivery/delivery-district.service';

let nextId = 0;

/**
 * The delivery destination picker in the header (GLB-06): the country, and for Sri Lanka the district
 * (owner decision 2026-10-21: Sri Lanka plus worldwide delivery).
 *
 * Native `<select>`s on purpose: they are keyboard- and screen-reader-correct everywhere, need no focus trap,
 * and on a phone open the platform picker, which beats any custom list at 320 px. The country is remembered
 * by {@link DeliveryCountryService} and the district by {@link DeliveryDistrictService}; the district picker
 * is shown only while the country is Sri Lanka, because only a Sri Lankan address has a district.
 */
@Component({
  selector: 'sd-district-selector',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-icon class="district__icon" name="truck" size="sm" />
    <label class="district__label" [attr.for]="countryId">Deliver to</label>
    <select
      class="district__select district__select--country"
      [attr.id]="countryId"
      data-testid="country-select"
      (change)="onCountryChange($event)"
    >
      @for (country of countries(); track country.code) {
        <option
          [attr.value]="country.code"
          [attr.selected]="country.code === selectedCountryCode() ? '' : null"
        >
          {{ country.name }}
        </option>
      }
    </select>
    @if (!isOverseas()) {
      <label class="sr-only" [attr.for]="selectId">District</label>
      <select
        class="district__select"
        [attr.id]="selectId"
        data-testid="district-select"
        (change)="onChange($event)"
      >
        @for (district of districts; track district.code) {
          <option
            [attr.value]="district.code"
            [attr.selected]="district.code === selectedCode() ? '' : null"
          >
            {{ district.name }}
          </option>
        }
      </select>
    }
  `,
  styles: `
    :host {
      display: flex;
      align-items: center;
      gap: var(--space-0-5);
      min-width: 0;
      min-height: var(--tap);
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }
    .district__icon {
      flex: none;
    }
    .district__label {
      white-space: nowrap;
    }
    .district__select {
      min-width: 0;
      min-height: var(--tap);
      max-width: 8rem;
      padding: 0 var(--space-0-5);
      border: 0;
      border-bottom: 1px solid var(--color-border-strong);
      background: transparent;
      color: var(--color-text);
      font-size: var(--text-small);
      cursor: pointer;
    }
    .district__select--country {
      max-width: 7.5rem;
    }
  `,
})
export class DistrictSelector {
  private readonly delivery = inject(DeliveryDistrictService);
  private readonly destination = inject(DeliveryCountryService);

  private readonly id = nextId++;
  protected readonly selectId = `sd-district-${this.id}`;
  protected readonly countryId = `sd-country-${this.id}`;
  protected readonly districts = this.delivery.districts;
  protected readonly selectedCode = this.delivery.selectedCode;
  protected readonly countries = this.destination.countries;
  protected readonly selectedCountryCode = this.destination.selectedCode;
  protected readonly isOverseas = this.destination.isOverseas;

  protected onChange(event: Event): void {
    const select = event.target as HTMLSelectElement | null;
    if (select) {
      this.delivery.select(select.value);
    }
  }

  protected onCountryChange(event: Event): void {
    const select = event.target as HTMLSelectElement | null;
    if (select) {
      this.destination.select(select.value);
    }
  }
}
