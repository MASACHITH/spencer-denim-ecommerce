import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import {
  AdminSettingGroup,
  PayHereStatus,
  PayHereValues,
  SETTING_KEYS,
} from '../../core/settings/admin-settings.model';

/**
 * The PayHere panel of the settings screen (doc 11 P8b; `Settings.Payments`): the merchant account, the
 * mode and the two windows - and, above them, whether checkout would offer PayHere right now, what is
 * missing, and the address to register in the PayHere portal.
 *
 * **A panel of its own and not more of `SettingsPage`**, so that a second payment gateway's panel is a
 * second file and this screen's already large class is not grown for each. The page hands it the group the
 * server sent; the panel owns its form, its save and its messages, and tells the page when it has saved so
 * the page can re-read (every save moves the group's row version).
 *
 * **The merchant secret is write-only here, as it is everywhere.** The server never returns it - the
 * settings payload says only whether one is saved - so the box always starts empty, a value is sent only
 * when one was typed, and a blank keeps the saved one. After a save the box is emptied; the secret is not
 * kept in this component any longer than the keystrokes that typed it.
 *
 * **There is no switch for the provider here, deliberately.** Whether the PayHere row is switched on is a
 * decision that waits on the owner confirming the change with the client (doc 11 G-43), and the existing
 * bank transfer has no switch either; the status shows the state and says where it is changed.
 */
@Component({
  selector: 'sda-payhere-panel',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel" aria-labelledby="payhere-title" data-testid="payhere-panel">
      <h2 class="adm-panel__title" id="payhere-title">PayHere</h2>
      <p class="adm-panel__hint">
        Card, mobile-wallet and internet-banking payments in rupees. Customers can choose PayHere only
        when it is switched on, the mode, merchant ID and secret below are filled in, and the shop's
        public address is configured on the server. The order is paid when PayHere's own server confirms
        it, never because the customer's browser came back.
      </p>

      <p class="sr-only" role="status" data-testid="payhere-announcement">{{ announcement() }}</p>

      @if (failure(); as problem) {
        @if (problem.kind !== 'validation') {
          <div class="adm-state adm-state--error" role="alert" data-testid="payhere-error">
            <p>{{ problem.message }}</p>
          </div>
        }
      }

      @if (status(); as current) {
        <div class="adm-state" data-testid="payhere-status">
          @if (current.offeredAtCheckout) {
            <p data-testid="payhere-offered">PayHere is offered to customers at checkout.</p>
          } @else {
            <p data-testid="payhere-not-offered">PayHere is not offered to customers at checkout.</p>
          }

          @if (!current.providerEnabled) {
            <p data-testid="payhere-switched-off">
              It is switched off in the payment providers list. There is no switch on this screen yet:
              it is turned on in the database once the shop owner has confirmed PayHere with the client.
            </p>
          }

          @if (current.problems.length > 0) {
            <ul data-testid="payhere-problems">
              @for (problem of current.problems; track problem) {
                <li>{{ problem }}</li>
              }
            </ul>
          }
        </div>

        <div class="adm-fields">
          <div class="adm-field adm-field--wide">
            <label class="adm-label" for="payhere-notify-url">Notify URL to register with PayHere</label>
            @if (current.notifyUrl; as notifyUrl) {
              <input
                class="adm-input"
                id="payhere-notify-url"
                type="text"
                readonly
                [value]="notifyUrl"
                aria-describedby="payhere-notify-hint"
                data-testid="payhere-notify-url"
              />
              <div class="adm-actions">
                <button
                  class="adm-btn"
                  type="button"
                  (click)="copy(notifyUrl)"
                  data-testid="payhere-copy-notify-url"
                >
                  Copy
                </button>
              </div>
            } @else {
              <p class="adm-field-error" data-testid="payhere-no-notify-url">
                The server does not know the shop's public web address yet (Email__StorefrontBaseUrl), so
                there is no address to give PayHere.
              </p>
            }
            <span class="adm-panel__hint" id="payhere-notify-hint" data-testid="payhere-domain-reminder">
              In the PayHere merchant portal add the shop's domain and wait for PayHere to approve it -
              PayHere refuses checkout from a domain it has not approved - then enter the address above as
              the notify URL. PayHere cannot call an address on a private network or on localhost, so the
              notify URL must be reachable from the internet over HTTPS.
            </span>
          </div>
        </div>
      }

      <form class="adm-fields" (ngSubmit)="save()" novalidate>
        <div class="adm-field">
          <label class="adm-label" for="payhere-mode">Mode</label>
          <select
            class="adm-input"
            id="payhere-mode"
            name="mode"
            [ngModel]="form().mode"
            (ngModelChange)="patch('mode', $event)"
            [attr.aria-invalid]="fieldError('mode') ? 'true' : null"
            aria-describedby="payhere-hint-mode"
            data-testid="field-payhere-mode"
          >
            <option value="sandbox">Sandbox (test, no real money)</option>
            <option value="live">Live (real payments)</option>
          </select>
          <span class="adm-panel__hint" id="payhere-hint-mode">
            The merchant ID and secret differ between the two, so change all three together.
          </span>
          @if (fieldError('mode'); as message) {
            <span class="adm-field-error" data-testid="error-payhere-mode">{{ message }}</span>
          }
        </div>

        <div class="adm-field">
          <label class="adm-label" for="payhere-merchantId">Merchant ID</label>
          <input
            class="adm-input"
            id="payhere-merchantId"
            name="merchantId"
            type="text"
            autocomplete="off"
            inputmode="numeric"
            [ngModel]="form().merchantId"
            (ngModelChange)="patch('merchantId', $event)"
            [attr.aria-invalid]="fieldError('merchantId') ? 'true' : null"
            aria-describedby="payhere-hint-merchantId"
            data-testid="field-payhere-merchantId"
          />
          <span class="adm-panel__hint" id="payhere-hint-merchantId">
            From the PayHere merchant portal. Not a secret.
          </span>
          @if (fieldError('merchantId'); as message) {
            <span class="adm-field-error" data-testid="error-payhere-merchantId">{{ message }}</span>
          }
        </div>

        <div class="adm-field adm-field--wide">
          <label class="adm-label" for="payhere-merchantSecret">Merchant secret</label>
          <input
            class="adm-input"
            id="payhere-merchantSecret"
            name="merchantSecret"
            type="password"
            autocomplete="new-password"
            spellcheck="false"
            [placeholder]="hasSecret() ? 'A secret is saved. Type a new one to replace it.' : 'Paste the merchant secret'"
            [ngModel]="form().merchantSecret"
            (ngModelChange)="patch('merchantSecret', $event)"
            [attr.aria-invalid]="fieldError('merchantSecret') ? 'true' : null"
            aria-describedby="payhere-hint-merchantSecret"
            data-testid="field-payhere-merchantSecret"
          />
          <span class="adm-panel__hint" id="payhere-hint-merchantSecret">
            Stored encrypted and never shown again, to anyone. Leave it empty to keep the one that is
            saved.
            @if (hasSecret()) {
              <strong data-testid="payhere-secret-set">A secret is saved.</strong>
            } @else {
              <strong data-testid="payhere-secret-unset">No secret is saved yet.</strong>
            }
          </span>
          @if (fieldError('merchantSecret'); as message) {
            <span class="adm-field-error" data-testid="error-payhere-merchantSecret">{{ message }}</span>
          }
        </div>

        <div class="adm-field">
          <label class="adm-label" for="payhere-pendingMaxDays">Pending hold (days)</label>
          <input
            class="adm-input"
            id="payhere-pendingMaxDays"
            name="pendingMaxDays"
            type="text"
            autocomplete="off"
            inputmode="numeric"
            [ngModel]="form().pendingMaxDays"
            (ngModelChange)="patch('pendingMaxDays', $event)"
            [attr.aria-invalid]="fieldError('pendingMaxDays') ? 'true' : null"
            aria-describedby="payhere-hint-pendingMaxDays"
            data-testid="field-payhere-pendingMaxDays"
          />
          <span class="adm-panel__hint" id="payhere-hint-pendingMaxDays">
            How long an order's items are held while PayHere reports a payment as pending. 1 to 14 days.
          </span>
          @if (fieldError('pendingMaxDays'); as message) {
            <span class="adm-field-error" data-testid="error-payhere-pendingMaxDays">{{ message }}</span>
          }
        </div>

        <div class="adm-field">
          <label class="adm-label" for="payhere-expiryMinutes">Payment window (minutes)</label>
          <input
            class="adm-input"
            id="payhere-expiryMinutes"
            name="expiryMinutes"
            type="text"
            autocomplete="off"
            inputmode="numeric"
            [ngModel]="form().expiryMinutes"
            (ngModelChange)="patch('expiryMinutes', $event)"
            [attr.aria-invalid]="fieldError('expiryMinutes') ? 'true' : null"
            aria-describedby="payhere-hint-expiryMinutes"
            data-testid="field-payhere-expiryMinutes"
          />
          <span class="adm-panel__hint" id="payhere-hint-expiryMinutes">
            How long an order waits for the customer to pay before the items are released. 15 to 1440
            minutes.
          </span>
          @if (fieldError('expiryMinutes'); as message) {
            <span class="adm-field-error" data-testid="error-payhere-expiryMinutes">{{ message }}</span>
          }
        </div>

        <div class="adm-actions adm-field--wide">
          <!-- aria-disabled, not disabled: disabling the button under the admin's own finger throws their
               focus to the document body. save() refuses a second write while one is in flight, so the
               control is inert either way. -->
          <button
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="busy() ? 'true' : null"
            data-testid="save-payhere"
          >
            {{ busy() ? 'Saving…' : 'Save PayHere settings' }}
          </button>
        </div>
      </form>
    </section>
  `,
})
export class PayHerePanel {
  private readonly api = inject(AdminSettingsApi);

  /** The `Payments.PayHere` group exactly as the server sent it. The secret's value is always null in it. */
  readonly group = input.required<AdminSettingGroup>();

  /** Emitted after a successful save, so the page re-reads and hands this panel the new row version. */
  readonly saved = output<void>();

  private readonly isBusy = signal(false);
  private readonly problem = signal<CatalogFailure | null>(null);
  private readonly said = signal('');
  private readonly current = signal<PayHereStatus | null>(null);

  /**
   * The form, reset to what the server holds each time the page hands over a new group - which it does
   * after every save, so the secret box is emptied by the same mechanism that refreshes the rest.
   */
  protected readonly form = linkedSignal<AdminSettingGroup, PayHereValues>({
    source: this.group,
    computation: (group) => toPayHereForm(group),
  });

  protected readonly busy = this.isBusy.asReadonly();
  protected readonly failure = this.problem.asReadonly();
  protected readonly announcement = this.said.asReadonly();
  protected readonly status = this.current.asReadonly();

  /** Whether a secret is saved - all the server will say about it. */
  protected readonly hasSecret = computed(
    () =>
      this.group().settings.find((setting) => setting.key === SETTING_KEYS.payHereMerchantSecret)
        ?.hasValue ?? false,
  );

  constructor() {
    this.loadStatus();
  }

  protected patch<K extends keyof PayHereValues>(field: K, value: PayHereValues[K]): void {
    this.form.update((current) => ({ ...current, [field]: value }));

    // The message about this field is now about a value nobody is looking at any more.
    this.problem.update((current) =>
      current?.kind === 'validation' && current.fieldErrors?.[field] ? null : current,
    );
  }

  protected fieldError(field: string): string | null {
    const failure = this.problem();

    return failure?.kind === 'validation' ? (failure.fieldErrors?.[field]?.[0] ?? null) : null;
  }

  protected save(): void {
    if (this.isBusy()) {
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set('Saving the PayHere settings…');

    this.api.updatePayHere(this.form(), this.group().rowVersion).subscribe({
      next: () => {
        this.isBusy.set(false);

        // The secret leaves this component the moment it has been sent.
        this.form.update((current) => ({ ...current, merchantSecret: '' }));

        this.said.set('PayHere settings saved.');
        this.loadStatus();
        this.saved.emit();
      },
      error: (error: unknown) => {
        this.isBusy.set(false);
        this.said.set('');
        this.problem.set(toCatalogFailure(error));
      },
    });
  }

  protected copy(value: string): void {
    const clipboard = globalThis.navigator?.clipboard;

    if (!clipboard) {
      this.said.set('Could not copy the address. Select it and copy it yourself.');
      return;
    }

    clipboard.writeText(value).then(
      () => this.said.set('Copied the notify URL.'),
      () => this.said.set('Could not copy the address. Select it and copy it yourself.'),
    );
  }

  /** Reads whether checkout would offer PayHere. A failure leaves the form usable and the status absent. */
  private loadStatus(): void {
    this.api.payHereStatus().subscribe({
      next: (status) => this.current.set(status),
      error: () => this.current.set(null),
    });
  }
}

/**
 * The form's values, read out of the group by key. The secret is always empty: the server never sends it,
 * and an empty box means "keep the saved one".
 */
export function toPayHereForm(group: AdminSettingGroup): PayHereValues {
  const byKey = new Map(group.settings.map((setting) => [setting.key, setting.value] as const));

  return {
    mode: byKey.get(SETTING_KEYS.payHereMode) ?? 'sandbox',
    merchantId: byKey.get(SETTING_KEYS.payHereMerchantId) ?? '',
    merchantSecret: '',
    pendingMaxDays: byKey.get(SETTING_KEYS.payHerePendingMaxDays) ?? '',
    expiryMinutes: byKey.get(SETTING_KEYS.payHereExpiryMinutes) ?? '',
  };
}
