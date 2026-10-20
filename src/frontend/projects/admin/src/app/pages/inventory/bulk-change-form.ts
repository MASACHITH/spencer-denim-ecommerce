import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';

import { toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { InventoryApi } from '../../core/inventory/inventory-api';
import { InventoryRow, STOCK_LIMITS, StockChangeResult } from '../../core/inventory/inventory.model';
import { STOCK_ACTIONS, StockAction, changeRow, quantityLabel, readQuantity, readReason } from './stock-change';

/**
 * The same change to every selected variant, with one reason (ADM-12 "bulk update", doc 08 §5.8).
 *
 * **Applied row by row, and the screen says so before it is pressed**: a row whose units are held by unpaid
 * orders, or whose count moved since the page loaded, is reported and left alone while the rest apply —
 * because an order placed a minute ago is no reason to refuse the delivery on the next line. A malformed
 * request (no reason, a quantity out of range) is refused whole before anything is applied.
 *
 * A stock-take count sends each row's on-hand figure as the screen shows it, so a row that changed since is
 * answered "Changed" rather than overwritten.
 */
@Component({
  selector: 'sda-bulk-change-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="adm-fields" novalidate (submit)="apply($event)" data-testid="bulk-form">
      <label class="adm-field">
        <span class="adm-label">What happened</span>
        <select class="adm-input" name="action" data-testid="bulk-action" (change)="action.set($any(read($event)))">
          @for (option of actions; track option.value) {
            <option [value]="option.value" [selected]="action() === option.value">{{ option.label }}</option>
          }
        </select>
      </label>
      <label class="adm-field">
        <span class="adm-label">{{ label() }}, each</span>
        <input
          class="adm-input"
          name="quantity"
          inputmode="numeric"
          autocomplete="off"
          data-testid="bulk-quantity"
          [attr.aria-invalid]="quantityError() ? 'true' : null"
          [value]="quantity()"
          (input)="quantity.set(read($event))"
        />
        @if (quantityError()) {
          <span class="adm-field-error" data-testid="bulk-quantity-error">{{ quantityError() }}</span>
        }
      </label>
      <label class="adm-field adm-field--wide">
        <span class="adm-label">Reason, for every row</span>
        <input
          class="adm-input"
          name="reason"
          autocomplete="off"
          [attr.maxlength]="maxReason"
          data-testid="bulk-reason"
          [attr.aria-invalid]="reasonError() ? 'true' : null"
          [value]="reason()"
          (input)="reason.set(read($event))"
        />
        @if (reasonError()) {
          <span class="adm-field-error" data-testid="bulk-reason-error">{{ reasonError() }}</span>
        }
      </label>
      <p class="adm-panel__hint adm-field--wide">
        Each of the {{ rows().length }} selected {{ rows().length === 1 ? 'variant is' : 'variants are' }} changed on its own:
        one that would go below what unpaid orders hold, or whose count changed since this page loaded, is left as it is
        and listed, and the rest are saved.
      </p>
      @if (problem()) {
        <p class="adm-state adm-state--error adm-field--wide" role="alert" data-testid="bulk-problem">{{ problem() }}</p>
      }
      <div class="adm-actions adm-field--wide">
        <button class="adm-btn--primary" type="submit" data-testid="bulk-apply" [attr.aria-disabled]="saving() ? 'true' : null">
          {{ saving() ? 'Saving…' : 'Apply to ' + rows().length + ' selected' }}
        </button>
        <button class="adm-btn" type="button" data-testid="bulk-cancel" (click)="cancelled.emit()">Cancel</button>
      </div>
    </form>
  `,
})
export class BulkChangeForm {
  readonly rows = input.required<readonly InventoryRow[]>();

  readonly applied = output<StockChangeResult>();
  readonly cancelled = output<void>();

  private readonly api = inject(InventoryApi);

  protected readonly actions = STOCK_ACTIONS;
  protected readonly maxReason = STOCK_LIMITS.reasonMaxLength;

  protected readonly action = signal<StockAction>('receive');
  protected readonly quantity = signal('');
  protected readonly reason = signal('');
  protected readonly quantityError = signal<string | null>(null);
  protected readonly reasonError = signal<string | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly label = computed(() => quantityLabel(this.action()));

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';
  }

  protected apply(event: Event): void {
    event.preventDefault();

    if (this.saving()) {
      return;
    }

    const quantity = readQuantity(this.action(), this.quantity());
    const reason = readReason(this.reason());
    const rows = this.rows();

    this.quantityError.set('problem' in quantity ? quantity.problem : null);
    this.reasonError.set('problem' in reason ? reason.problem : null);
    this.problem.set(
      rows.length === 0
        ? 'Select at least one variant.'
        : rows.length > STOCK_LIMITS.maxBulkRows
          ? `Change at most ${STOCK_LIMITS.maxBulkRows} variants at a time.`
          : null,
    );

    if ('problem' in quantity || 'problem' in reason || this.problem()) {
      return;
    }

    this.saving.set(true);

    this.api.adjust({ reason: reason.value, rows: rows.map((row) => changeRow(this.action(), quantity.value, row)) }).subscribe({
      next: (result) => {
        this.saving.set(false);
        this.applied.emit(result);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        const failure = toCatalogFailure(error);
        const fields = failure.fieldErrors ?? {};
        const quantityField = Object.keys(fields).find((key) => key.endsWith('.quantity') || key.endsWith('.expectedOnHand'));

        this.reasonError.set(fields['reason']?.[0] ?? null);
        this.quantityError.set(quantityField ? fields[quantityField][0] : null);
        this.problem.set(fields['reason'] || quantityField ? null : failure.message);
      },
    });
  }
}
