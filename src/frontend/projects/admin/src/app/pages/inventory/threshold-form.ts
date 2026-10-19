import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';

import { toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { InventoryApi } from '../../core/inventory/inventory-api';
import { InventoryRow, STOCK_LIMITS } from '../../core/inventory/inventory.model';

/**
 * One variant's low-stock threshold (doc 11 P2 step 5): the shop default, or a number of its own.
 *
 * **The default is a choice of its own, not an empty box.** "Blank means the default" is true and
 * invisible; a radio that says "Use the shop default (5)" says what will happen, and it is how a threshold
 * set by mistake is put back.
 */
@Component({
  selector: 'sda-threshold-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="adm-fields" novalidate (submit)="save($event)" data-testid="threshold-form">
      <fieldset class="adm-field adm-field--wide thr">
        <legend class="adm-label">Low-stock threshold</legend>
        <label class="adm-check">
          <input type="radio" name="mode" value="default" data-testid="threshold-default" [checked]="useDefault()" (change)="useDefault.set(true)" />
          Use the shop default ({{ defaultThreshold() }})
        </label>
        <label class="adm-check">
          <input type="radio" name="mode" value="own" data-testid="threshold-own" [checked]="!useDefault()" (change)="useDefault.set(false)" />
          Its own threshold
        </label>
      </fieldset>
      @if (!useDefault()) {
        <label class="adm-field">
          <span class="adm-label">Low at this many available or fewer</span>
          <input
            class="adm-input"
            name="threshold"
            inputmode="numeric"
            autocomplete="off"
            data-testid="threshold-value"
            [attr.aria-invalid]="error() ? 'true' : null"
            [value]="value()"
            (input)="value.set(read($event))"
          />
        </label>
      }
      <p class="adm-panel__hint adm-field--wide">Out of stock is never counted as low. 0 means this variant is never shown as low.</p>
      @if (error()) {
        <p class="adm-field-error adm-field--wide" role="alert" data-testid="threshold-error">{{ error() }}</p>
      }
      <div class="adm-actions adm-field--wide">
        <button class="adm-btn--primary" type="submit" data-testid="threshold-save" [attr.aria-disabled]="saving() ? 'true' : null">
          {{ saving() ? 'Saving…' : 'Save threshold' }}
        </button>
        <button class="adm-btn" type="button" data-testid="threshold-cancel" (click)="cancelled.emit()">Cancel</button>
      </div>
    </form>
  `,
  styles: `.thr { border: 0; padding: 0; margin: 0; }`,
})
export class ThresholdForm implements OnInit {
  readonly row = input.required<InventoryRow>();
  readonly defaultThreshold = input.required<number>();

  readonly saved = output<InventoryRow>();
  readonly cancelled = output<void>();

  private readonly api = inject(InventoryApi);

  protected readonly useDefault = signal(true);
  protected readonly value = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);

  ngOnInit(): void {
    const own = this.row().ownThreshold;
    this.useDefault.set(own === null);
    this.value.set(own === null ? '' : String(own));
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | null)?.value ?? '';
  }

  protected save(event: Event): void {
    event.preventDefault();

    if (this.saving()) {
      return;
    }

    let threshold: number | null = null;

    if (!this.useDefault()) {
      const text = this.value().trim();

      if (!/^\d+$/.test(text) || Number(text) > STOCK_LIMITS.maxThreshold) {
        this.error.set('Use a whole number from 0 to 10,000, or choose the shop default.');
        return;
      }

      threshold = Number(text);
    }

    this.error.set(null);
    this.saving.set(true);

    this.api.setThreshold(this.row().variantId, threshold).subscribe({
      next: (row) => {
        this.saving.set(false);
        this.saved.emit(row);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        const failure = toCatalogFailure(error);
        this.error.set(failure.fieldErrors?.['lowStockThreshold']?.[0] ?? failure.message);
      },
    });
  }
}
