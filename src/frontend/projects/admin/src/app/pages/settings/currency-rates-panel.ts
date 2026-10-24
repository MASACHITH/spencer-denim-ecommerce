import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
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
import { RateAge, dayCount, describeRateAge } from '../../core/settings/currency-rate-age';
import { checkCurrencyRate, currencyRateMessage } from '../../core/settings/currency-rate-rules';
import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import {
  AdminSettingGroup,
  CurrencyField,
  CurrencyRateValues,
  DISPLAY_CURRENCIES,
} from '../../core/settings/admin-settings.model';

/** The sentence the other admin screens give for a 401 (the sign-in and two-factor calls say it the same way). */
const SESSION_ENDED = 'Your session has ended. Please sign in again.';

/** A 400 whose field names are none of the five this panel draws: there is no box to put a message beside. */
const NOT_ACCEPTED = 'The server did not accept these rates. Check each one and try again.';

/** One currency as the template draws it: everything decided here, so the template only prints. */
interface CurrencyRow {
  readonly field: CurrencyField;
  readonly code: string;

  /** The name as it reads in the middle of a sentence ("US dollar", "pound sterling"): the API's own. */
  readonly name: string;
  readonly text: string;
  readonly error: string | null;

  /** What the row says about the saved rate (its age, or that none is set or that it cannot be used). */
  readonly line: string;

  /** The age with its full date, for a tooltip. Null when there is no date to give. */
  readonly lineTitle: string | null;

  /** The warning for a saved rate that has not changed for more than the limit, or null. */
  readonly stale: string | null;

  readonly describedBy: string;
}

/**
 * What the settings row holds for one currency, as far as this screen can tell.
 *
 * `none`: no value. `valid`: a rate the API's own rule accepts. `invalid`: text that is not a rate (the public feed
 * skips it, so it must not be counted or dated as if it were one). `unreadable`: not text at all, which the screen
 * treats as nothing it can show.
 */
interface StoredRate {
  readonly state: 'none' | 'valid' | 'invalid' | 'unreadable';

  /** The text to show in the box: the trimmed value for `valid` and `invalid`, empty otherwise. */
  readonly text: string;
  readonly updatedAtUtc: string | null;
}

/**
 * The display currencies panel (issue #248; doc 08 §3.8 group `Currency.Display`, `Settings.Edit`).
 *
 * **Drawn only when the server sends the group**, which it does for a caller who holds `Settings.Edit`: the read
 * model filters every row by its own permission, so hiding the panel here is convenience and the API is the boundary.
 *
 * **Display only.** These rates are for the shop's currency switcher, which is not released yet. PayPal still
 * charges the rupee total converted to US dollars at the PayPal rate, which has its own panel. Nothing this panel
 * says may promise what the storefront does today: there is no switcher on this branch, and the order page, the
 * PayPal step and the price-range filter stay in rupees whatever is typed here.
 *
 * **Only the boxes that changed are sent**, the bank group's rule: an absent field is left alone, and an empty one
 * clears that rate (the currency is then left out). The age of a rate is the setting row's own `UpdatedAtUtc`, so
 * it moves only when the number does: typing the same rate again changes nothing and re-dates nothing.
 *
 * **Unsaved typing survives a re-read.** The page re-reads after any save on the screen, which hands this panel a
 * new group; a box the admin has edited and not saved is kept, and only a box they have not touched is refreshed.
 * That includes typing done while a save was in flight: only a box whose text is still exactly what was sent counts
 * as saved when the answer arrives.
 *
 * **The outcome is under the button that caused it.** A saved confirmation, "nothing to save" and the 401, 403, 409,
 * 5xx and offline alerts are drawn directly under Save and scrolled into view (nearest, without moving focus),
 * because on a phone or a 1280 px screen the top of a long form is out of sight by the time Save has been pressed.
 * The same text is announced through an always-present status region.
 */
@Component({
  selector: 'sda-currency-rates-panel',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel" aria-labelledby="currency-title" data-testid="currency-panel">
      <h2 class="adm-panel__title" id="currency-title">Display currencies</h2>
      <p class="adm-panel__hint">
        For each currency, type how many rupees one unit costs. For example, 399.50 for the pound means one pound costs
        399.50 rupees. Leave a box empty to leave that currency out.
      </p>
      <p class="adm-panel__hint">
        <strong>Display only.</strong> PayPal still charges the rupee total, converted to US dollars at the PayPal
        rate. These rates are typed by hand and are never updated automatically, so check them when exchange rates
        move. A rate's age counts from the last time its number changed. The shop's currency switcher will use these
        rates when it is released.
      </p>
      <p class="adm-panel__hint" data-testid="currency-offered">{{ offered() }}</p>

      <form class="cur-rows" (ngSubmit)="save()" novalidate>
        @for (row of rows(); track row.field) {
          <div class="cur-row" [attr.data-testid]="'currency-row-' + row.field">
            <div class="adm-field">
              <label class="adm-label" [attr.for]="'currency-' + row.field">
                Rupees for one {{ row.name }} ({{ row.code }})
              </label>
              <input
                class="adm-input cur-input"
                type="text"
                inputmode="decimal"
                enterkeyhint="done"
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

        <div class="cur-save">
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

          <!--
            What happened, directly under the button that made it happen. The status region is always in the page so
            that what is put into it is announced; a new element each time (tracked by id) makes a second identical
            message announce again.
          -->
          <div class="sr-only" role="status" data-testid="currency-announcement">
            @for (entry of notices(); track entry.id) {
              <span>{{ entry.text }}</span>
            }
          </div>

          @for (entry of notices(); track entry.id) {
            <p class="adm-state cur-note" data-currency-outcome data-testid="currency-saved">{{ entry.text }}</p>
          }

          @if (problem(); as failure) {
            <div
              class="adm-state adm-state--error cur-note"
              role="alert"
              data-currency-outcome
              data-testid="currency-error"
            >
              <p>{{ failure.message }}</p>
              @if (failure.kind === 'conflict') {
                <div class="adm-actions">
                  <button class="adm-btn" type="button" (click)="loadLatest()" data-testid="currency-reload">
                    Load the latest rates
                  </button>
                </div>
              }
            </div>
          }
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
    .cur-save {
      display: grid;
      padding-top: var(--space-1);
    }
    .cur-note {
      margin-top: var(--space-2);

      /* Room round it when it is scrolled into view, so it is not flush against the edge of the window. */
      scroll-margin-block: var(--space-3);
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
  private readonly injector = inject(Injector);
  private readonly saveButton = viewChild<ElementRef<HTMLButtonElement>>('saveButton');

  protected readonly currencies = DISPLAY_CURRENCIES;

  /** What is typed in each box. Strings, never null: an empty box is an empty string. */
  protected readonly form = signal<Readonly<Record<CurrencyField, string>>>(emptyForm());
  protected readonly errors = signal<Readonly<Partial<Record<CurrencyField, string>>>>({});
  protected readonly busy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);

  /** The message under Save ("Rates saved.", "There is nothing to save…"). Each one has its own id. */
  private readonly notice = signal<{ readonly id: number; readonly text: string } | null>(null);
  private noticeCount = 0;

  /** The notice as a list of at most one, so a repeat of the same words is a new element and is announced again. */
  protected readonly notices = computed(() => {
    const current = this.notice();

    return current ? [current] : [];
  });

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
      const stored = storedRate(group, currency.key);
      const text = typed[currency.field];
      const error = errors[currency.field] ?? null;
      const age = stored.state === 'valid' ? describeRateAge(stored.updatedAtUtc, now) : null;
      const trimmed = text.trim();

      let line: string;
      if (trimmed === '') {
        line =
          stored.state === 'valid'
            ? `Saving with this box empty leaves ${currency.code} out.`
            : stored.state === 'invalid'
              ? 'Saving with this box empty clears the stored value.'
              : stored.state === 'unreadable'
                ? 'The stored value could not be read. Type a rate to replace it.'
                : `No rate set. ${currency.code} is left out until you type one.`;
      } else if (!sameRate(trimmed, stored.text)) {
        line =
          stored.state === 'valid'
            ? `Unsaved change. The saved rate is ${stored.text}. ${ageSentence(age)}`
            : stored.state === 'invalid'
              ? `Unsaved change. The stored value ${stored.text} is not a valid rate.`
              : `Unsaved. Save to set a rate for ${currency.code}.`;
      } else {
        line = stored.state === 'invalid' ? 'Stored value is not a valid rate.' : ageSentence(age);
      }

      const stale = age?.stale
        ? `This rate has not changed for ${dayCount(age.days)}. Check it is still right.`
        : null;

      return {
        field: currency.field,
        code: currency.code,
        name: currency.name,
        text,
        error,
        line,
        lineTitle: age?.kind === 'known' ? `${age.relative}, on ${age.date}` : null,
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

  /** Which currencies have a usable rate saved right now: the saved rates, not what is typed. */
  protected readonly offered = computed(() => {
    const group = this.group();
    const codes = DISPLAY_CURRENCIES.filter((currency) => storedRate(group, currency.key).state === 'valid').map(
      (currency) => currency.code,
    );

    return codes.length === 0 ? 'No rates set yet.' : `Rates set: ${codes.join(', ')}.`;
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
            const saved = storedRate(group, currency.key).text;

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
    this.notice.set(null);
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
    this.notice.set(null);

    const changes: Partial<Record<CurrencyField, string>> = {};
    const found: Partial<Record<CurrencyField, string>> = {};
    const group = this.group();

    for (const currency of DISPLAY_CURRENCIES) {
      const trimmed = this.form()[currency.field].trim();

      if (sameRate(trimmed, storedRate(group, currency.key).text)) {
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
      this.say('There is nothing to save: no rate has changed.');
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
          // A box typed in again while the save was in flight holds something newer than what was saved: it stays
          // unsaved, and the re-read that follows must not overwrite it.
          if (sameRate(this.form()[field].trim(), changes[field] ?? '')) {
            this.dirty.delete(field);
          }
        }

        this.busy.set(false);
        this.say('Rates saved.');
        this.saved.emit();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  /** "Load the latest rates" after a conflict: the alert has done its job, and the page brings the new token. */
  protected loadLatest(): void {
    this.problem.set(null);
    this.reload.emit();
  }

  private messageFor(field: CurrencyField): string | null {
    const currency = DISPLAY_CURRENCIES.find((candidate) => candidate.field === field)!;
    const trimmed = this.form()[field].trim();

    // An empty box is not an error: it means "leave this currency out".
    return trimmed === ''
      ? null
      : currencyRateMessage(checkCurrencyRate(trimmed), currency.name, currency.code);
  }

  private fail(error: unknown): void {
    this.busy.set(false);

    // A 401 is the session, not a fault in the rates; the catalogue classifier would call it "something went wrong".
    if (error instanceof HttpErrorResponse && error.status === 401) {
      this.problem.set({ kind: 'forbidden', message: SESSION_ENDED });
      this.reveal();
      return;
    }

    const failure = toCatalogFailure(error);

    if (failure.kind === 'validation') {
      const fromServer: Partial<Record<CurrencyField, string>> = {};

      for (const currency of DISPLAY_CURRENCIES) {
        const message = failure.fieldErrors?.[currency.field]?.[0];

        if (message) {
          fromServer[currency.field] = message;
        }
      }

      if (Object.keys(fromServer).length > 0) {
        this.problem.set(null);
        this.errors.set(fromServer);
        this.focusFirstInvalid(fromServer);
        return;
      }

      // The API named a field this panel has no box for: say so rather than leave the press with no answer.
      this.problem.set({ kind: 'unknown', message: NOT_ACCEPTED });
      this.reveal();
      return;
    }

    this.problem.set(failure);
    this.reveal();
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
  }

  private focusFirstInvalid(found: Partial<Record<CurrencyField, string>>): void {
    const first = DISPLAY_CURRENCIES.find((currency) => found[currency.field]);

    if (first) {
      this.host.nativeElement.querySelector<HTMLInputElement>(`#currency-${first.field}`)?.focus();
    }
  }

  /** Puts a message under Save and brings it into view. */
  private say(text: string): void {
    this.noticeCount += 1;
    this.notice.set({ id: this.noticeCount, text });
    this.reveal();
  }

  /**
   * Scrolls the outcome (the message or the alert under Save) into view once it has been drawn, the least that shows
   * it ("nearest"), smoothly unless the visitor asked for less motion. It does not move focus: a failed save that put
   * focus on the first wrong box has no outcome element at all, and one that did not keeps the focus where it was.
   */
  private reveal(): void {
    afterNextRender(
      () => {
        this.host.nativeElement.querySelector<HTMLElement>('[data-currency-outcome]')?.scrollIntoView?.({
          block: 'nearest',
          behavior: prefersReducedMotion() ? 'auto' : 'smooth',
        });
      },
      { injector: this.injector },
    );
  }
}

function emptyForm(): Readonly<Record<CurrencyField, string>> {
  return { usd: '', gbp: '', eur: '', aud: '', aed: '' };
}

function prefersReducedMotion(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * What the row for a currency holds. Keys are matched without regard to case, as the server does; a value that is
 * not text is "unreadable" rather than something to call `.trim()` on, which would take the whole screen down.
 */
function storedRate(group: AdminSettingGroup, key: string): StoredRate {
  const wanted = key.toLowerCase();
  const settings: readonly { key?: unknown; value?: unknown; updatedAtUtc?: unknown }[] = Array.isArray(group.settings)
    ? group.settings
    : [];
  const setting = settings.find((candidate) => typeof candidate.key === 'string' && candidate.key.toLowerCase() === wanted);

  const updatedAtUtc = typeof setting?.updatedAtUtc === 'string' ? setting.updatedAtUtc : null;
  const raw = setting?.value;

  if (raw === null || raw === undefined) {
    return { state: 'none', text: '', updatedAtUtc };
  }

  if (typeof raw !== 'string') {
    return { state: 'unreadable', text: '', updatedAtUtc };
  }

  const text = raw.trim();

  if (text === '') {
    return { state: 'none', text: '', updatedAtUtc };
  }

  return { state: checkCurrencyRate(text) === 'none' ? 'valid' : 'invalid', text, updatedAtUtc };
}

/** The sentence about when a saved rate last changed. */
function ageSentence(age: RateAge | null): string {
  if (!age || age.kind === 'unrecorded') {
    return 'No change date recorded.';
  }

  return age.kind === 'justNow' ? 'Last changed just now.' : `Last changed ${age.relative} (${age.date}).`;
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
