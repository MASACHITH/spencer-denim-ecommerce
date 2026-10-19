import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { InventoryApi } from '../../core/inventory/inventory-api';
import { InventoryRow, STOCK_LIMITS, StockChangeRowResult } from '../../core/inventory/inventory.model';
import {
  STOCK_ACTIONS,
  StockAction,
  changeRow,
  floorProblem,
  onHandAfter,
  quantityLabel,
  readQuantity,
  readReason,
} from './stock-change';

/**
 * One variant's stock change (doc 11 P2 step 2): receive, add, remove or a stock-take count, with a reason.
 *
 * **Every answer is passed up, applied or not**, because a refusal still carries the figures as they now
 * are — a count against a figure that moved answers with the new one, and the row has to show it so the
 * next save is against the right number. Only an applied change closes the form.
 */
@Component({
  selector: 'sda-stock-change-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="adm-fields" novalidate (submit)="save($event)" data-testid="stock-form">
      <label class="adm-field">
        <span class="adm-label">What happened</span>
        <select class="adm-input" name="action" data-testid="stock-action" (change)="choose($event)">
          @for (option of actions; track option.value) {
            <option [value]="option.value" [selected]="action() === option.value">{{ option.label }}</option>
          }
        </select>
      </label>
      <label class="adm-field">
        <span class="adm-label">{{ label() }}</span>
        <input
          class="adm-input"
          name="quantity"
          inputmode="numeric"
          autocomplete="off"
          data-testid="stock-quantity"
          aria-describedby="stock-preview"
          [attr.aria-invalid]="quantityError() ? 'true' : null"
          [value]="quantity()"
          (input)="quantity.set(read($event))"
        />
        @if (quantityError()) {
          <span class="adm-field-error" data-testid="stock-quantity-error">{{ quantityError() }}</span>
        }
      </label>
      <label class="adm-field adm-field--wide">
        <span class="adm-label">Reason</span>
        <input
          class="adm-input"
          name="reason"
          autocomplete="off"
          [attr.maxlength]="maxReason"
          data-testid="stock-reason"
          [attr.aria-invalid]="reasonError() ? 'true' : null"
          [value]="reason()"
          (input)="reason.set(read($event))"
        />
        @if (reasonError()) {
          <span class="adm-field-error" data-testid="stock-reason-error">{{ reasonError() }}</span>
        }
      </label>
      <p class="adm-panel__hint adm-field--wide" id="stock-preview" data-testid="stock-preview">{{ preview() }}</p>
      @if (refusal(); as said) {
        <p class="adm-state adm-state--warning adm-field--wide" role="alert" data-testid="stock-refusal">{{ said }}</p>
      }
      <div class="adm-actions adm-field--wide">
        <button class="adm-btn--primary" type="submit" data-testid="stock-save" [attr.aria-disabled]="saving() ? 'true' : null">
          {{ saving() ? 'Saving…' : 'Save' }}
        </button>
        <button class="adm-btn" type="button" data-testid="stock-cancel" (click)="cancelled.emit()">Cancel</button>
      </div>
    </form>
  `,
})
export class StockChangeForm {
  readonly row = input.required<InventoryRow>();

  /** Every answer for this row: applied, refused, changed or not found. */
  readonly answered = output<StockChangeRowResult>();
  readonly cancelled = output<void>();

  private readonly api = inject(InventoryApi);

  protected readonly actions = STOCK_ACTIONS;
  protected readonly maxReason = STOCK_LIMITS.reasonMaxLength;

  protected readonly action = signal<StockAction>('receive');
  protected readonly quantity = signal('');
  protected readonly reason = signal('');
  protected readonly quantityError = signal<string | null>(null);
  protected readonly reasonError = signal<string | null>(null);
  protected readonly refusal = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly label = computed(() => quantityLabel(this.action()));

  protected readonly preview = computed(() => {
    const row = this.row();
    const now = `On hand now ${row.onHand}, of which ${row.reserved} ${row.reserved === 1 ? 'is' : 'are'} held by unpaid orders.`;
    const typed = readQuantity(this.action(), this.quantity());

    if (!('value' in typed)) {
      return now;
    }

    const after = onHandAfter(this.action(), typed.value, row.onHand);
    const floor = floorProblem(after, row.reserved);

    // A warning, not a block: the figures are from when the page loaded, and the shop checks them again.
    return floor
      ? `${now} After saving: ${after} on hand. When this page loaded, ${floor} The shop will refuse it if that is still so.`
      : `${now} After saving: ${after} on hand.`;
  });

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';
  }

  protected choose(event: Event): void {
    this.action.set(this.read(event) as StockAction);
    this.quantityError.set(null);
    this.refusal.set(null);
  }

  protected save(event: Event): void {
    event.preventDefault();

    // aria-disabled plus this refusal, never [disabled] (#109).
    if (this.saving()) {
      return;
    }

    const row = this.row();
    const quantity = readQuantity(this.action(), this.quantity());
    const reason = readReason(this.reason());

    // The reserved floor is **not** checked here (tester #190, D4b). `row.reserved` is the figure from when
    // the page loaded, and an order paid or expired since may have freed those units; a stale figure must
    // never stop a change the server would accept. The server decides, under the row lock, and its
    // Refused or Changed answer is shown below. The preview still warns.
    this.quantityError.set('problem' in quantity ? quantity.problem : null);
    this.reasonError.set('problem' in reason ? reason.problem : null);
    this.refusal.set(null);

    if ('problem' in quantity || 'problem' in reason) {
      return;
    }

    this.saving.set(true);

    this.api.adjust({ reason: reason.value, rows: [changeRow(this.action(), quantity.value, row)] }).subscribe({
      next: (result) => {
        this.saving.set(false);
        const answer = result.rows[0];

        if (answer.outcome !== 'Applied') {
          this.refusal.set(answer.message ?? 'Nothing was changed.');
        }

        this.answered.emit(answer);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.show(toCatalogFailure(error));
      },
    });
  }

  private show(failure: CatalogFailure): void {
    const fields = failure.fieldErrors ?? {};
    const quantity = fields['rows[0].quantity'] ?? fields['rows[0].expectedOnHand'];

    this.quantityError.set(quantity?.[0] ?? null);
    this.reasonError.set(fields['reason']?.[0] ?? null);

    if (!quantity && !fields['reason']) {
      this.refusal.set(failure.message);
    }
  }
}
