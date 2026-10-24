import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { checkCurrencyRate, currencyRateMessage } from '../../core/settings/currency-rate-rules';
import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import {
  AdminSettingGroup,
  CURRENCY_RATE_STALE_AFTER_DAYS,
  CurrencyField,
  CurrencyRateValues,
  DISPLAY_CURRENCIES,
} from '../../core/settings/admin-settings.model';

const DAY_MS = 86_400_000;

/** One currency as the template draws it: everything decided here, so the template only prints. */
interface CurrencyRow {
  readonly field: CurrencyField;
  readonly code: string;
  readonly name: string;
  readonly text: string;
  readonly error: string | null;

  /** What the row says about the saved rate (its age, or that it is not offered). */
  readonly line: string;

  /** The age with its full date, for a tooltip. Null when there is no saved rate. */
  readonly lineTitle: string | null;

  /** The warning for a saved rate that has not changed for more than the limit, or null. */
  readonly stale: string | null;

  readonly describedBy: string;
}

/**
 * The display currencies panel (issue #248; doc 08 §3.8 group `Currency.Display`, `Settings.Edit`).
 *
 * **Drawn only when the server sends the group**, which it does for a caller who holds `Settings.Edit`: the read
 * model filters every row by its own permission, so hiding the panel here is convenience and the API is the boundary.
 *
 * **Display only.** These rates change what a price looks like to a shopper who chooses a currency. PayPal still
 * charges the rupee total converted to US dollars at the PayPal rate, which has its own panel.
 *
 * **Only the boxes that changed are sent**, the bank group's rule: an absent field is left alone, and an empty one
 * clears that rate (the currency is then not offered). The age of a rate is the setting row's own `UpdatedAtUtc`, so
 * it moves only when the number does: typing the same rate again changes nothing and re-dates nothing.
 *
 * **Unsaved typing survives a re-read.** The page re-reads after any save on the screen, which hands this panel a
 * new group; a box the admin has edited and not saved is kept, and only a box they have not touched is refreshed.
 *
 * **A saved confirmation that is on the screen as well as announced.** The email panel's is screen-reader only, which
 * leaves a sighted admin guessing whether the button did anything.
 */
@Component({
  selector: 'sda-currency-rates-panel',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel" aria-labelledby="currency-title" data-testid="currency-panel">
      <h2 class="adm-panel__title" id="currency-title">Display currencies</h2>
      <p class="adm-panel__hint">
        Lets a shopper choose to see prices in US dollars, pounds, euros, Australian dollars or dirhams. For each one,
        type how many rupees one unit is worth: for example, 399.50 for the pound means one pound costs 399.50
        rupees. Shoppers see every price converted at your rate and marked as approximate.
      </p>
      <p class="adm-panel__hint">
        <strong>Display only.</strong> PayPal still charges the rupee total, converted to US dollars at the PayPal
        rate. These rates are typed by hand and are never updated automatically, so check them when exchange rates
        move. A rate's age counts from the last time its number changed. Leave a box empty to stop offering that
        currency.
      </p>
      <p class="adm-panel__hint" data-testid="currency-offered">{{ offered() }}</p>

      @if (savedMessage(); as message) {
        <p class="adm-state" role="status" data-testid="currency-saved">{{ message }}</p>
      }

      @if (problem(); as failure) {
        @if (failure.kind !== 'validation') {
          <div class="adm-state adm-state--error" role="alert" data-testid="currency-error">
            <p>{{ failure.message }}</p>
            @if (failure.kind === 'conflict') {
              <div class="adm-actions">
                <button class="adm-btn" type="button" (click)="reload.emit()" data-testid="currency-reload">
                  Load the latest rates
                </button>
              </div>
            }
          </div>
        }
      }

      <form class="cur-rows" (ngSubmit)="save()" novalidate>
        @for (row of rows(); track row.field) {
          <div class="cur-row" [attr.data-testid]="'currency-row-' + row.field">
            <div class="adm-field">
              <label class="adm-label" [attr.for]="'currency-' + row.field">
                {{ row.name }} ({{ row.code }}): rupees for one
              </label>
              <input
                class="adm-input cur-input"
                type="text"
                inputmode="decimal"
                enterkeyhint="next"
                autocomplete="off"
                spellcheck="false"
                placeholder="Not set"
                [id]="'currency-' + row.field"
                [name]="row.field"
                [attr.aria-invalid]="row.error ? 'true' : null"
                [attr.aria-describedby]="row.describedBy"
                [ngModel]="row.text"
                (ngModelChange)="patch(row.field, $event)"
                (blur)="check(row.field, $event)"
                [attr.data-testid]="'currency-field-' + row.field"
              />
              @if (row.error; as message) {
                <span
                  class="adm-field-error"
                  [id]="'currency-error-' + row.field"
                  [attr.data-testid]="'currency-error-' + row.field"
                  >{{ message }}</span
                >
              }
            </div>

            <div class="cur-meta">
              <span
                class="adm-panel__hint"
                [id]="'currency-line-' + row.field"
                [attr.title]="row.lineTitle"
                [attr.data-testid]="'currency-line-' + row.field"
                >{{ row.line }}</span
              >
              @if (row.stale; as warning) {
                <p
                  class="adm-state adm-state--warning"
                  [id]="'currency-stale-' + row.field"
                  [attr.data-testid]="'currency-stale-' + row.field"
                >
                  {{ warning }}
                </p>
              }
            </div>
          </div>
        }

        <div class="adm-actions">
          <!-- aria-disabled, not disabled, so pressing does not throw focus to the body (the bank form's reason). -->
          <button
            #saveButton
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="busy() ? 'true' : null"
            data-testid="save-currency"
          >
            {{ busy() ? 'Saving…' : 'Save display currencies' }}
          </button>
        </div>
      </form>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .cur-rows {
      display: grid;
      gap: var(--space-1);
    }
    .cur-row {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-0-5) var(--space-2);
      padding-block: var(--space-1-5);
      border-top: 1px solid var(--color-border);
    }
    .cur-row:first-child {
      border-top: 0;
    }
    .cur-input {
      max-width: 14rem;
    }
    .cur-meta {
      display: grid;
      gap: var(--space-1);
      align-content: start;
      min-width: 0;
    }
    @media (min-width: 720px) {
      .cur-row {
        grid-template-columns: minmax(0, 18rem) minmax(0, 1fr);
        align-items: start;
      }
      .cur-meta {
        padding-top: var(--space-3);
      }
    }
  `,
})
export class CurrencyRatesPanel {
  /** The `Currency.Display` group as the server sent it. */
  readonly group = input.required<AdminSettingGroup>();

  /** Raised after a successful save, so the page re-reads (the group's token has moved). */
  readonly saved = output<void>();

  /** Raised by "Load the latest rates" after a conflict: the page re-reads and hands this panel the new token. */
  readonly reload = output<void>();

  private readonly api = inject(AdminSettingsApi);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly saveButton = viewChild<ElementRef<HTMLButtonElement>>('saveButton');

  protected readonly currencies = DISPLAY_CURRENCIES;

  /** What is typed in each box. Strings, never null: an empty box is an empty string. */
  protected readonly form = signal<Readonly<Record<CurrencyField, string>>>(emptyForm());
  protected readonly errors = signal<Readonly<Partial<Record<CurrencyField, string>>>>({});
  protected readonly busy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly savedMessage = signal('');

  /** "Now", read when the server last sent the group and not on every render, so the ages do not shift under a reader. */
  private readonly nowMs = signal(Date.now());

  /** The boxes the admin has edited and not saved. A re-read leaves these alone. */
  private readonly dirty = new Set<CurrencyField>();

  protected readonly rows = computed<readonly CurrencyRow[]>(() => {
    const group = this.group();
    const typed = this.form();
    const errors = this.errors();
    const now = this.nowMs();

    return DISPLAY_CURRENCIES.map((currency) => {
      const saved = savedRate(group, currency.key);
      const text = typed[currency.field];
      const error = errors[currency.field] ?? null;
      const age = saved.value === '' ? null : ageOf(saved.updatedAtUtc, now);
      const ageSentence = age ? `Last changed ${age.relative} (${age.date}).` : 'Not shown to shoppers.';
      const trimmed = text.trim();

      let line: string;
      if (trimmed === '' && saved.value === '') {
        line = 'Not shown to shoppers until a rate is entered.';
      } else if (trimmed === '') {
        line = `Saving with this box empty stops offering ${currency.code} to shoppers.`;
      } else if (!sameRate(trimmed, saved.value)) {
        line =
          saved.value === ''
            ? `Unsaved. ${currency.code} will be offered to shoppers once you save.`
            : `Unsaved change. The saved rate is ${saved.value}. ${ageSentence}`;
      } else {
        line = age ? ageSentence : 'Saved.';
      }

      const stale =
        saved.value !== '' && age !== null && age.days > CURRENCY_RATE_STALE_AFTER_DAYS
          ? `This rate has not changed for ${age.days} days. Check it is still right.`
          : null;

      return {
        field: currency.field,
        code: currency.code,
        name: currency.name,
        text,
        error,
        line,
        lineTitle: age ? `${age.relative}, on ${age.date}` : null,
        stale,
        describedBy: [
          `currency-line-${currency.field}`,
          stale ? `currency-stale-${currency.field}` : null,
          error ? `currency-error-${currency.field}` : null,
        ]
          .filter((id): id is string => id !== null)
          .join(' '),
      };
    });
  });

  /** Which currencies shoppers can choose right now: the saved rates, not what is typed. */
  protected readonly offered = computed(() => {
    const group = this.group();
    const codes = DISPLAY_CURRENCIES.filter((currency) => savedRate(group, currency.key).value !== '').map(
      (currency) => currency.code,
    );

    return codes.length === 0
      ? 'No currency is offered to shoppers yet. Type a rate to offer one.'
      : `Shoppers can choose to see prices in: ${codes.join(', ')}.`;
  });

  constructor() {
    // Every time the server sends the group (first read and every re-read after a save anywhere on the screen), the
    // boxes show it, except a box the admin has typed in and not saved.
    effect(() => {
      const group = this.group();

      untracked(() => {
        this.nowMs.set(Date.now());

        this.form.update((current) => {
          const next = { ...current };

          for (const currency of DISPLAY_CURRENCIES) {
            const saved = savedRate(group, currency.key).value;

            // Once the server holds what was typed, the box is no longer unsaved.
            if (this.dirty.has(currency.field) && sameRate(current[currency.field].trim(), saved)) {
              this.dirty.delete(currency.field);
            }

            if (!this.dirty.has(currency.field)) {
              next[currency.field] = saved;
            }
          }

          return next;
        });
      });
    });
  }

  protected patch(field: CurrencyField, value: string | null): void {
    this.dirty.add(field);
    this.form.update((current) => ({ ...current, [field]: value ?? '' }));
    this.savedMessage.set('');
    this.clearError(field);
  }

  /**
   * Checks one box when the admin leaves it, so a typo is caught where it was made.
   *
   * **Not when they are leaving it for Save.** The error line pushes Save down; if that happens between the mouse
   * going down on the button and coming up, the pointer is no longer over it, no click is made, and the first press
   * on Save does nothing (found by a failing browser test, then reproduced with a deliberately slow click). Save runs
   * the same check when it is pressed, so nothing is missed.
   */
  protected check(field: CurrencyField, event: FocusEvent): void {
    const save = this.saveButton()?.nativeElement;

    if (save && event.relatedTarget === save) {
      return;
    }

    const message = this.messageFor(field);

    if (message) {
      this.errors.update((current) => ({ ...current, [field]: message }));
    }
  }

  protected save(): void {
    if (this.busy()) {
      return;
    }

    this.problem.set(null);
    this.savedMessage.set('');

    const changes: Partial<Record<CurrencyField, string>> = {};
    const found: Partial<Record<CurrencyField, string>> = {};
    const group = this.group();

    for (const currency of DISPLAY_CURRENCIES) {
      const trimmed = this.form()[currency.field].trim();

      if (sameRate(trimmed, savedRate(group, currency.key).value)) {
        continue;
      }

      changes[currency.field] = trimmed;

      const message = this.messageFor(currency.field);
      if (message) {
        found[currency.field] = message;
      }
    }

    const fields = Object.keys(changes) as CurrencyField[];

    if (fields.length === 0) {
      this.savedMessage.set('There is nothing to save: no rate has changed.');
      return;
    }

    if (Object.keys(found).length > 0) {
      this.errors.set(found);
      this.focusFirstInvalid(found);
      return;
    }

    this.errors.set({});
    this.busy.set(true);

    const body: CurrencyRateValues = changes;

    this.api.updateCurrencyRates(body, group.rowVersion).subscribe({
      next: () => {
        for (const field of fields) {
          this.dirty.delete(field);
        }

        this.busy.set(false);
        this.savedMessage.set('Rates saved.');
        this.saved.emit();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  private messageFor(field: CurrencyField): string | null {
    const currency = DISPLAY_CURRENCIES.find((candidate) => candidate.field === field)!;
    const trimmed = this.form()[field].trim();

    // An empty box is not an error: it means "stop offering this currency".
    return trimmed === ''
      ? null
      : currencyRateMessage(checkCurrencyRate(trimmed), currency.name, currency.code);
  }

  private fail(error: unknown): void {
    const failure = toCatalogFailure(error);

    this.busy.set(false);
    this.problem.set(failure);

    if (failure.kind === 'validation') {
      const fromServer: Partial<Record<CurrencyField, string>> = {};

      for (const currency of DISPLAY_CURRENCIES) {
        const message = failure.fieldErrors?.[currency.field]?.[0];

        if (message) {
          fromServer[currency.field] = message;
        }
      }

      this.errors.set(fromServer);
      this.focusFirstInvalid(fromServer);
    }
  }

  private clearError(field: CurrencyField): void {
    this.errors.update((current) => {
      if (!(field in current)) {
        return current;
      }

      const next = { ...current };
      delete next[field];
      return next;
    });

    this.problem.update((current) =>
      current?.kind === 'validation' && current.fieldErrors?.[field] ? null : current,
    );
  }

  private focusFirstInvalid(found: Partial<Record<CurrencyField, string>>): void {
    const first = DISPLAY_CURRENCIES.find((currency) => found[currency.field]);

    if (first) {
      this.host.nativeElement.querySelector<HTMLInputElement>(`#currency-${first.field}`)?.focus();
    }
  }
}

function emptyForm(): Readonly<Record<CurrencyField, string>> {
  return { usd: '', gbp: '', eur: '', aud: '', aed: '' };
}

/** A saved rate: its text ('' when there is none) and when its row last changed. */
function savedRate(
  group: AdminSettingGroup,
  key: string,
): { readonly value: string; readonly updatedAtUtc: string | null } {
  const setting = group.settings.find((candidate) => candidate.key === key);

  return { value: setting?.value?.trim() ?? '', updatedAtUtc: setting?.updatedAtUtc ?? null };
}

/**
 * Whether the typed text and the saved text are the same rate: both empty, or both numbers that are equal, so typing
 * 399.50 over a saved 399.5 is no change (the server stores the shorter form).
 */
function sameRate(typed: string, saved: string): boolean {
  if (typed === saved) {
    return true;
  }

  if (typed === '' || saved === '') {
    return false;
  }

  return checkCurrencyRate(typed) === 'none' && checkCurrencyRate(saved) === 'none' && Number(typed) === Number(saved);
}

/** How old a saved rate is, in whole days, in words and with its date in the shop's time zone. */
function ageOf(
  updatedAtUtc: string | null,
  nowMs: number,
): { readonly days: number; readonly relative: string; readonly date: string } | null {
  if (!updatedAtUtc) {
    return null;
  }

  const changed = Date.parse(updatedAtUtc);

  if (Number.isNaN(changed)) {
    return null;
  }

  const days = Math.max(0, Math.floor((nowMs - changed) / DAY_MS));
  const relative = days === 0 ? 'today' : days === 1 ? 'yesterday' : `${days} days ago`;
  const date = new Date(changed).toLocaleDateString('en-GB', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'Asia/Colombo',
  });

  return { days, relative, date };
}
