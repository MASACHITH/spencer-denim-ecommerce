import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { AdminPromoApi } from '../../core/marketing/admin-promo-api';
import {
  AdminPromoCode,
  PromoDiscountType,
  promoDiscountLabel,
  promoLimitLabel,
} from '../../core/marketing/admin-promo.model';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/**
 * Promo codes (ADM-13, doc 11 P3 part B).
 *
 * **Nothing is seeded and no example code is shown.** Doc 02 line 167 replaces the old demo's hard-coded
 * `TUASL10` and `FREESHIP` with admin-managed codes, and a plausible-looking code anywhere here — in a
 * seeder, as a default, or as a form placeholder that renders — would be a discount the client never
 * agreed to. W3-03 shipped a real bank's name as placeholder text and it was a Major finding; this screen
 * ships nothing of the kind.
 *
 * **The dates are the shop's own time.** The two inputs are `datetime-local`, which has no offset, and
 * the API's contract is Colombo wall-clock for exactly that reason: a staff member who types 23:59 means
 * 23:59 in the shop. The server converts; nothing here does arithmetic on a date.
 *
 * **"Exclude sale items" is on this form as of W3-05c.** W3-05 left it off because it could only exclude
 * a line a sale campaign priced and `marketing.SaleCampaigns` did not exist (ADM-14) — a checkbox that
 * could not change any total is a promise this shop would not keep. ADM-14 merged, so the flag changes a
 * real total and the shop was applying a rule no staff member could set. **The label has to say which
 * items**: doc 11 §7 G-07 and doc 11 line 464 are explicit that it removes lines a *campaign* priced and
 * keeps lines that only carry a markdown, and that distinction is the whole point of the field — a
 * checkbox reading only "exclude sale items" would be read as covering both.
 *
 * **There is a remove, and it is a soft delete.** Doc 11 P3's permission table has `Promotions.Delete` for
 * "remove campaign or code". It releases the code string for reuse and leaves every placed order alone,
 * because an order keeps `promoCodeText` as its own snapshot — so an old invoice still prints the code.
 */
@Component({
  selector: 'sda-promo-codes-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Promo codes</h1>
      <p class="adm-panel__hint">
        Codes a shopper can type at checkout. Nothing is set up in advance — which codes this shop
        offers, and what they are worth, is yours to decide.
      </p>
      <p class="adm-panel__hint">
        Dates and times are this shop's own time. A code works from its start until its end, and the end
        is the moment it stops: a code ending at midnight works all through the evening before.
      </p>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
        <div class="adm-actions">
          <button class="adm-btn" type="button" (click)="reload()">Try again</button>
        </div>
      </div>
    }

    <!-- tabindex="-1" so focus can land here when a save replaces the row that was being edited. -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="promo-said">{{ said() }}</p>

    <section class="adm-panel">
      <h2 class="adm-panel__title">
        {{ editingId() === null ? 'Add a promo code' : 'Edit this promo code' }}
      </h2>
      <form (submit)="save($event)">
        <div class="adm-fields">
          <label class="adm-field">
            <span class="adm-label">Code</span>
            <input
              class="adm-input promo-code-input"
              type="text"
              name="code"
              maxlength="40"
              autocomplete="off"
              spellcheck="false"
              [value]="code()"
              [attr.aria-describedby]="'promo-code-hint'"
              [attr.aria-invalid]="errorFor('code') ? 'true' : null"
              data-testid="promo-code"
              (input)="type($event, code)"
            />
            <span class="adm-panel__hint" id="promo-code-hint">
              Letters, numbers, hyphens and underscores. Stored and matched in capitals, so shoppers can
              type it either way.
            </span>
            @if (errorFor('code'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-code">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Type</span>
            <select
              class="adm-input"
              name="discountType"
              [value]="discountType()"
              data-testid="promo-type"
              (change)="chooseType($event)"
            >
              <option value="Percent">Percentage off the items</option>
              <option value="FixedAmount">Fixed amount off the items</option>
              <option value="FreeDelivery">Free delivery</option>
            </select>
            @if (errorFor('discountType'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-type">{{ message }}</span>
            }
          </label>

          <!-- Not rendered for a free-delivery code, because it has no amount: the server refuses a value
               sent with one rather than ignoring it, so a box that could be filled in would be a box
               whose contents refuse the save. "chooseType" clears both figures when the type changes, so
               a value typed under one type cannot be submitted under another. -->
          @if (discountType() !== 'FreeDelivery') {
            <label class="adm-field">
              <span class="adm-label">
                {{ discountType() === 'Percent' ? 'Percentage' : 'Amount off (Rs.)' }}
              </span>
              <input
                class="adm-input"
                type="number"
                name="discountValue"
                step="0.01"
                min="0"
                [value]="discountValue()"
                [attr.aria-invalid]="errorFor('discountValue') ? 'true' : null"
                data-testid="promo-value"
                (input)="type($event, discountValue)"
              />
              @if (errorFor('discountValue'); as message) {
                <span class="adm-field-error" role="alert" data-testid="error-value">{{ message }}</span>
              }
            </label>
          }

          @if (discountType() === 'Percent') {
            <label class="adm-field">
              <span class="adm-label">Most it can take off (Rs.)</span>
              <input
                class="adm-input"
                type="number"
                name="maxDiscountAmount"
                step="0.01"
                min="0"
                [value]="maxDiscountAmount()"
                [attr.aria-invalid]="errorFor('maxDiscountAmount') ? 'true' : null"
                data-testid="promo-cap"
                (input)="type($event, maxDiscountAmount)"
              />
              @if (errorFor('maxDiscountAmount'); as message) {
                <span class="adm-field-error" role="alert" data-testid="error-cap">{{ message }}</span>
              }
            </label>
          }

          <label class="adm-field">
            <span class="adm-label">Smallest order it works on (Rs.)</span>
            <input
              class="adm-input"
              type="number"
              name="minSpendAmount"
              step="0.01"
              min="0"
              [value]="minSpendAmount()"
              [attr.aria-invalid]="errorFor('minSpendAmount') ? 'true' : null"
              data-testid="promo-min-spend"
              (input)="type($event, minSpendAmount)"
            />
            @if (errorFor('minSpendAmount'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-min-spend">
                {{ message }}
              </span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Starts</span>
            <input
              class="adm-input"
              type="datetime-local"
              name="startsAt"
              [value]="startsAt()"
              [attr.aria-invalid]="errorFor('startsAt') ? 'true' : null"
              data-testid="promo-starts"
              (input)="type($event, startsAt)"
            />
            @if (errorFor('startsAt'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-starts">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Ends <span class="adm-muted">(optional)</span></span>
            <input
              class="adm-input"
              type="datetime-local"
              name="endsAt"
              [value]="endsAt()"
              [attr.aria-invalid]="errorFor('endsAt') ? 'true' : null"
              data-testid="promo-ends"
              (input)="type($event, endsAt)"
            />
            @if (errorFor('endsAt'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-ends">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Total uses <span class="adm-muted">(blank for no limit)</span></span>
            <input
              class="adm-input"
              type="number"
              name="totalUsageLimit"
              step="1"
              min="1"
              [value]="totalUsageLimit()"
              [attr.aria-invalid]="errorFor('totalUsageLimit') ? 'true' : null"
              data-testid="promo-total-limit"
              (input)="type($event, totalUsageLimit)"
            />
            @if (errorFor('totalUsageLimit'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-total-limit">
                {{ message }}
              </span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">
              Uses per customer <span class="adm-muted">(blank for no limit)</span>
            </span>
            <input
              class="adm-input"
              type="number"
              name="perCustomerLimit"
              step="1"
              min="1"
              [value]="perCustomerLimit()"
              [attr.aria-describedby]="'promo-per-customer-hint'"
              [attr.aria-invalid]="errorFor('perCustomerLimit') ? 'true' : null"
              data-testid="promo-per-customer"
              (input)="type($event, perCustomerLimit)"
            />
            <span class="adm-panel__hint" id="promo-per-customer-hint">
              Counted by email address, so the same shopper using a different address counts separately.
            </span>
            @if (errorFor('perCustomerLimit'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-per-customer">
                {{ message }}
              </span>
            }
          </label>

          <label class="adm-field adm-field--wide">
            <span class="adm-label">Note to yourselves <span class="adm-muted">(optional)</span></span>
            <input
              class="adm-input"
              type="text"
              name="description"
              maxlength="250"
              autocomplete="off"
              [value]="description()"
              [attr.aria-invalid]="errorFor('description') ? 'true' : null"
              data-testid="promo-description"
              (input)="type($event, description)"
            />
            @if (errorFor('description'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-description">
                {{ message }}
              </span>
            }
          </label>

          <!-- Doc 11 P3 step 7's "exclude sale items", with the distinction doc 11 §7 G-07 draws spelled
               out underneath rather than left to the two words. The two readings it separates — a
               campaign price and a markdown — are both "on sale" in a shopper's words, and only one of
               them is excluded. -->
          <span class="promo-check promo-check--wide">
            <input
              type="checkbox"
              id="promo-exclude-sale"
              aria-describedby="promo-exclude-sale-hint"
              [checked]="excludeSaleItems()"
              data-testid="promo-exclude-sale"
              (change)="toggleExcludeSaleItems($event)"
            />
            <label for="promo-exclude-sale" data-testid="promo-exclude-sale-field">
              Skip items a sale campaign has discounted
            </label>
          </span>
          <p class="adm-panel__hint promo-check__hint" id="promo-exclude-sale-hint">
            Lines a sale campaign is pricing are left out of what this code takes off. A product whose
            own price has simply been marked down still counts.
          </p>

          <!-- A 44 px row around a 24 px box, and the label is part of the control, which is the shape
               CouriersPage settled on: the shared 20 px checkbox fails GLB-09's floor and a 44 px native
               one renders as an outsized glyph. -->
          <span class="promo-check">
            <input
              type="checkbox"
              id="promo-active"
              [checked]="isActive()"
              data-testid="promo-active"
              (change)="toggleActive($event)"
            />
            <label for="promo-active" data-testid="promo-active-field">
              Shoppers can use this code
            </label>
          </span>
        </div>

        <div class="adm-actions">
          <button
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="isBusy() ? 'true' : null"
            data-testid="promo-save"
          >
            {{ editingId() === null ? 'Add the code' : 'Save the code' }}
          </button>
          @if (editingId() !== null) {
            <button class="adm-btn" type="button" data-testid="promo-cancel" (click)="startNew()">
              Cancel
            </button>
          }
        </div>
      </form>
    </section>

    @if (isLoading()) {
      <p class="adm-muted">Loading promo codes…</p>
    } @else if (promoCodes().length === 0) {
      <p class="adm-muted" data-testid="promo-empty">
        No promo code has been added yet, so nothing a shopper types at checkout will work. Add the first
        one above.
      </p>
    } @else {
      <ul class="adm-list" data-testid="promo-codes">
        @for (promoCode of promoCodes(); track promoCode.id) {
          <li class="promo-row" [attr.data-testid]="'promo-' + promoCode.id">
            <p class="promo-row__code">
              {{ promoCode.code }}
              @if (promoCode.status !== 'Live') {
                <span class="adm-pill adm-pill--hidden">{{ statusLabel(promoCode.status) }}</span>
              }
            </p>
            <dl class="promo-row__facts">
              <dt>Discount</dt>
              <dd>{{ discountLabel(promoCode) }}</dd>
              <dt>Uses</dt>
              <dd>{{ limitLabel(promoCode) }}</dd>
              <dt>Window</dt>
              <dd>
                {{ promoCode.startsAt }}
                @if (promoCode.endsAt) {
                  &ndash; {{ promoCode.endsAt }}
                } @else {
                  &ndash; no end
                }
              </dd>
              @if (promoCode.minSpendAmount !== null) {
                <dt>Smallest order</dt>
                <dd>{{ promoCode.minSpendAmount }}</dd>
              }
              <!-- Shown only when it is on. A row reading "Sale items: included" on every code would be
                   noise on the common case, and the common case is the column's default. -->
              @if (promoCode.excludeSaleItems) {
                <dt>Sale items</dt>
                <dd [attr.data-testid]="'excludes-sale-' + promoCode.id">
                  Skipped when a sale campaign is pricing them
                </dd>
              }
              @if (promoCode.description) {
                <dt>Note</dt>
                <dd>{{ promoCode.description }}</dd>
              }
            </dl>
            <div class="adm-actions">
              <button
                class="adm-btn"
                type="button"
                [attr.data-testid]="'edit-' + promoCode.id"
                (click)="edit(promoCode)"
              >
                Edit
              </button>
              <!-- Two presses, because this cannot be undone from the screen. The first turns the button
                   into a confirmation and the second removes the row; "aria-disabled" is not used here
                   because nothing is ever disabled. The confirmation replaces the label on the same
                   button, so focus stays where the staff member put it. -->
              <button
                class="adm-btn"
                type="button"
                [attr.data-testid]="'remove-' + promoCode.id"
                (click)="remove(promoCode)"
              >
                {{ removingId() === promoCode.id ? 'Yes, remove it' : 'Remove' }}
              </button>
              @if (removingId() === promoCode.id) {
                <button
                  class="adm-btn"
                  type="button"
                  [attr.data-testid]="'remove-cancel-' + promoCode.id"
                  (click)="cancelRemove()"
                >
                  Keep it
                </button>
              }
            </div>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .adm-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .promo-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .promo-row__code {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }
    .promo-row__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .promo-row__facts dt {
      color: var(--color-text-muted);
    }
    .promo-row__facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .promo-code-input {
      text-transform: uppercase;
    }
    .promo-check {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      min-width: 0;
    }
    .promo-check input[type='checkbox'] {
      width: 24px;
      height: 24px;
      flex: 0 0 auto;
      margin: 0;
      cursor: pointer;
    }
    .promo-check label {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      cursor: pointer;
    }
    /* The two long labels get the whole row rather than a grid column, so neither wraps to one word per
       line at 320 px. .adm-fields is the grid; this is the same escape .adm-field--wide takes. No
       backticks in here: the styles block is a template literal. */
    .promo-check--wide {
      grid-column: 1 / -1;
    }
    .promo-check__hint {
      grid-column: 1 / -1;
      margin: 0;
    }
  `,
})
export class PromoCodesPage {
  private readonly api = inject(AdminPromoApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly promoCodes = signal<readonly AdminPromoCode[]>([]);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  protected readonly editingId = signal<number | null>(null);

  /** Which row's Remove has been pressed once. Null when nothing is awaiting confirmation. */
  protected readonly removingId = signal<number | null>(null);

  protected readonly code = signal('');
  protected readonly description = signal('');
  protected readonly discountType = signal<PromoDiscountType>('Percent');
  protected readonly discountValue = signal('');
  protected readonly maxDiscountAmount = signal('');
  protected readonly minSpendAmount = signal('');
  protected readonly startsAt = signal('');
  protected readonly endsAt = signal('');
  protected readonly excludeSaleItems = signal(false);
  protected readonly totalUsageLimit = signal('');
  protected readonly perCustomerLimit = signal('');
  protected readonly isActive = signal(true);

  private readonly fieldErrors = signal<Readonly<Record<string, string>>>({});

  /** The row version of the code being edited, round-tripped as an ETag. */
  private rowVersion: string | null = null;

  /**
   * The server's message for one field, or null.
   *
   * A method rather than a computed returning a closure, for the reason `CouriersPage.errorFor` gives: a
   * signal read inside the method body still makes the template track `fieldErrors`, and the closure form
   * confused the template type-checker into reporting "expected 0 arguments" at every call site.
   */
  protected errorFor(field: string): string | null {
    return this.fieldErrors()[field] ?? null;
  }

  protected readonly discountLabel = promoDiscountLabel;

  protected readonly limitLabel = promoLimitLabel;

  constructor() {
    this.startNew();
    this.load();
  }

  protected type(event: Event, target: { set(value: string): void }): void {
    target.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected toggleActive(event: Event): void {
    this.isActive.set((event.target as HTMLInputElement | null)?.checked ?? false);
  }

  protected toggleExcludeSaleItems(event: Event): void {
    this.excludeSaleItems.set((event.target as HTMLInputElement | null)?.checked ?? false);
  }

  /**
   * Changes the discount type, **clearing the two figures that belong to the old one**.
   *
   * Carrying them over is how a staff member submits a percentage as a fixed amount: the server refuses a
   * value on a free-delivery code and caps a percentage at 100, so the save would fail — but a figure
   * left in a box that has changed meaning is a figure they will not notice has changed meaning.
   */
  protected chooseType(event: Event): void {
    const value = (event.target as HTMLSelectElement | null)?.value ?? 'Percent';

    this.discountType.set(value as PromoDiscountType);
    this.discountValue.set('');
    this.maxDiscountAmount.set('');
    this.fieldErrors.set({});
  }

  protected statusLabel(status: AdminPromoCode['status']): string {
    switch (status) {
      case 'Scheduled':
        return 'Starts later';
      case 'Ended':
        return 'Ended';
      case 'Inactive':
        return 'Switched off';
      default:
        return 'Live';
    }
  }

  protected startNew(): void {
    this.editingId.set(null);
    this.rowVersion = null;
    this.code.set('');
    this.description.set('');
    this.discountType.set('Percent');
    this.discountValue.set('');
    this.maxDiscountAmount.set('');
    this.minSpendAmount.set('');

    // Deliberately blank rather than "now": a start time this screen invented would be a window the staff
    // member did not choose, and the server refuses a missing start with a message against the control.
    this.startsAt.set('');
    this.endsAt.set('');

    // Off, which is the column's default and the reading doc 08 §6 step 7 is written in: a new code
    // discounts every line until somebody says otherwise.
    this.excludeSaleItems.set(false);
    this.totalUsageLimit.set('');
    this.perCustomerLimit.set('');
    this.isActive.set(true);
    this.fieldErrors.set({});
  }

  protected edit(promoCode: AdminPromoCode): void {
    this.editingId.set(promoCode.id);
    this.rowVersion = promoCode.rowVersion;
    this.removingId.set(null);
    this.code.set(promoCode.code);
    this.description.set(promoCode.description ?? '');
    this.discountType.set(promoCode.discountType);
    this.discountValue.set(promoCode.discountValue === null ? '' : String(promoCode.discountValue));
    this.maxDiscountAmount.set(
      promoCode.maxDiscountAmount === null ? '' : String(promoCode.maxDiscountAmount),
    );
    this.minSpendAmount.set(
      promoCode.minSpendAmount === null ? '' : String(promoCode.minSpendAmount),
    );

    // The server sends Colombo wall-clock, and `datetime-local` wants exactly that with no offset — so
    // the only transformation is trimming the seconds the input does not show.
    this.startsAt.set(toLocalInput(promoCode.startsAt));
    this.endsAt.set(promoCode.endsAt === null ? '' : toLocalInput(promoCode.endsAt));
    this.excludeSaleItems.set(promoCode.excludeSaleItems);
    this.totalUsageLimit.set(
      promoCode.totalUsageLimit === null ? '' : String(promoCode.totalUsageLimit),
    );
    this.perCustomerLimit.set(
      promoCode.perCustomerLimit === null ? '' : String(promoCode.perCustomerLimit),
    );
    this.isActive.set(promoCode.isActive);
    this.fieldErrors.set({});
    this.announce(`Editing ${promoCode.code}.`);
  }

  /**
   * Saves the form.
   *
   * **Every field is sent, including the blank ones**, because the endpoint refuses a body that leaves
   * one out rather than reading its absence as null — and the figure that would be dropped here is a
   * usage limit, which is money. An empty box becomes `null`, which is how a staff member says "no
   * limit".
   *
   * **Nothing is validated here that the server validates**, with one exception: an empty code is caught
   * before the request, because that is the one mistake a staff member makes by pressing Save on a form
   * they have not started. Every other rule — the percentage range, "end after start", "per-customer not
   * above total" — is the server's, and its sentences land against their own controls.
   */
  protected save(event: Event): void {
    event.preventDefault();

    // The guard `disabled` would have provided, which is why the button is never disabled: `[disabled]`
    // on a control whose state changes under the user throws focus to `<body>` (#109).
    if (this.isBusy()) {
      return;
    }

    const code = this.code().trim();

    if (code.length === 0) {
      this.fieldErrors.set({ code: 'Enter the code shoppers will type.' });
      this.announce('Enter the code shoppers will type.');
      return;
    }

    const type = this.discountType();

    this.isBusy.set(true);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.announce(this.editingId() === null ? 'Adding the code…' : 'Saving the code…');

    const focused = this.document.activeElement as HTMLElement | null;

    this.api
      .save(
        this.editingId(),
        {
          code,
          description: blankToNull(this.description()),

          discountType: type,

          // Null for a free-delivery code, which is what the constraint requires — and the two figures
          // are cleared when the type changes, so neither can be a leftover from another type.
          discountValue: type === 'FreeDelivery' ? null : toNumberOrNull(this.discountValue()),
          maxDiscountAmount: type === 'Percent' ? toNumberOrNull(this.maxDiscountAmount()) : null,
          minSpendAmount: toNumberOrNull(this.minSpendAmount()),
          startsAt: this.startsAt().trim(),
          endsAt: blankToNull(this.endsAt()),
          excludeSaleItems: this.excludeSaleItems(),
          totalUsageLimit: toNumberOrNull(this.totalUsageLimit()),
          perCustomerLimit: toNumberOrNull(this.perCustomerLimit()),
          isActive: this.isActive(),
        },
        this.rowVersion,
      )
      .subscribe({
        next: (saved) => {
          // The warnings come first, because they are about the save that just happened and the shortest
          // sentence is the one a staff member reads. Doc 08 §3.6 DB19's lowered-limit warning is the one
          // this exists for: the save worked, and the code is now blocked for new customers.
          const warnings = saved.warnings.join(' ');

          this.announce(
            warnings.length === 0
              ? `${saved.promoCode.code} is saved.`
              : `${saved.promoCode.code} is saved. ${warnings}`,
          );

          this.startNew();
          this.load(() => this.rehomeFocusIfLost(focused));
        },
        error: (error: unknown) => this.fail(error),
      });
  }

  /**
   * Removes a code, on the second press.
   *
   * Two presses because it cannot be undone from this screen, and the confirmation replaces the label on
   * the same button so that focus stays where the staff member put it — a dialog or a new button would
   * move it, which is the #109 shape by another route.
   */
  protected remove(promoCode: AdminPromoCode): void {
    if (this.isBusy()) {
      return;
    }

    if (this.removingId() !== promoCode.id) {
      this.removingId.set(promoCode.id);
      this.announce(`Press Remove again to delete ${promoCode.code}.`);
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.announce(`Removing ${promoCode.code}…`);

    const focused = this.document.activeElement as HTMLElement | null;

    this.api.remove(promoCode.id, promoCode.rowVersion).subscribe({
      next: () => {
        this.removingId.set(null);

        if (this.editingId() === promoCode.id) {
          this.startNew();
        }

        this.announce(`${promoCode.code} is removed. The code can be used again for a new offer.`);
        this.load(() => this.rehomeFocusIfLost(focused));
      },
      error: (error: unknown) => {
        this.removingId.set(null);
        this.fail(error);
      },
    });
  }

  protected cancelRemove(): void {
    this.removingId.set(null);
    this.announce('Nothing was removed.');
  }

  protected reload(): void {
    this.load();
  }

  private load(then?: () => void): void {
    this.api.list().subscribe({
      next: (list) => {
        this.promoCodes.set(list);
        this.isLoading.set(false);
        this.isBusy.set(false);
        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.fail(error);
      },
    });
  }

  /**
   * Turns a failure into something the staff member can act on, **keeping the server's per-field
   * messages**.
   *
   * A duplicate code, a percentage above 100 and "the end must be after the start" all arrive as a 400
   * naming their own field, and those sentences are written for the staff member. Collapsing them into
   * one banner would throw away the field, which is the only thing that says where to look.
   */
  private fail(error: unknown): void {
    this.isBusy.set(false);

    const failure = toCatalogFailure(error);
    const fields = failure.fieldErrors;

    if (fields !== undefined && Object.keys(fields).length > 0) {
      this.fieldErrors.set(
        Object.fromEntries(
          Object.entries(fields).map(([field, messages]) => [field, messages.join(' ')]),
        ),
      );

      this.announce(Object.values(this.fieldErrors()).join(' '));
      return;
    }

    this.problem.set(failure);
    this.announce(failure.message);
  }

  /** @see CouriersPage.rehomeFocusIfLost — the same rule, and the same reason. */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="promo-said"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /** @see CouriersPage.announce — a repeated identical announcement has to re-fire (ADM-22). */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}

/** `''` for a blank box, which is how a staff member says "not set". */
function blankToNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length === 0 ? null : trimmed;
}

/**
 * A number, or null for a blank box.
 *
 * **An unparseable box is null rather than zero or a refusal.** Zero would be a figure the staff member
 * did not type — and for a usage limit a zero is a code nobody can use — while refusing would put a
 * second validator in front of the server's, which answers with a sentence against the control.
 */
function toNumberOrNull(value: string): number | null {
  const trimmed = value.trim();

  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number(trimmed);

  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * The shop's wall-clock time as a `datetime-local` input wants it: `yyyy-MM-ddTHH:mm`.
 *
 * The server sends `yyyy-MM-ddTHH:mm:ss`, which the input accepts but renders without the seconds — so
 * trimming them here means the value the form posts back is the value the form was given, and a save
 * that changes nothing does not move the window by a second.
 */
function toLocalInput(value: string): string {
  return value.length >= 16 ? value.slice(0, 16) : value;
}
