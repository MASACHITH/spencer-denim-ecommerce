import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Icon } from '@spencer-denim/shared';

import { DeliveryDistrictService } from '../../core/delivery/delivery-district.service';

let nextId = 0;

/**
 * The delivery district picker in the header (GLB-06).
 *
 * A native `<select>` on purpose: it is keyboard- and screen-reader-correct everywhere, it needs
 * no focus trap, and on a phone it opens the platform picker, which beats any custom list at
 * 320 px. The choice is remembered by {@link DeliveryDistrictService} and will drive the delivery
 * estimate and fee (PDP-05, CHK-01).
 */
@Component({
  selector: 'sd-district-selector',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-icon class="district__icon" name="truck" size="sm" />
    <label class="district__label" [attr.for]="selectId">Deliver to</label>
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
  `,
  styles: `
    :host {
      display: flex;
      align-items: center;
      gap: var(--space-0-5);
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
      max-width: 10rem;
      padding: 0 var(--space-0-5);
      border: 0;
      border-bottom: 1px solid var(--color-border-strong);
      background: transparent;
      color: var(--color-text);
      font-size: var(--text-small);
      cursor: pointer;
    }
  `,
})
export class DistrictSelector {
  private readonly delivery = inject(DeliveryDistrictService);

  protected readonly selectId = `sd-district-${nextId++}`;
  protected readonly districts = this.delivery.districts;
  protected readonly selectedCode = this.delivery.selectedCode;

  protected onChange(event: Event): void {
    const select = event.target as HTMLSelectElement | null;
    if (select) {
      this.delivery.select(select.value);
    }
  }
}
