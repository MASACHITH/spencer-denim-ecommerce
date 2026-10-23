import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import {
  AdminSettingGroup,
  SETTING_KEYS,
  SmtpTextValues,
  SMTP_TEXT_FIELDS,
} from '../../core/settings/admin-settings.model';

/** One text box of the form, in the order it is drawn. */
interface SmtpField {
  readonly name: keyof SmtpTextValues;
  readonly label: string;
  readonly hint: string;
  readonly inputMode?: 'numeric' | 'email';
  readonly autocomplete: string;
}

/**
 * The email account panel (ADM-22, 2026-10-21; doc 08 §3.8 `Email.Smtp`, `Settings.Security`).
 *
 * **Drawn only when the server sends the `Email.Smtp` group**, which it does for a Super Admin only — the
 * permission cannot be granted to any other role. The API is the boundary; hiding the panel is convenience.
 *
 * **Its own component**, not more lines in the settings page: it has two actions (save and send a test) and its
 * own busy state, and the page only needs to know when to re-read.
 *
 * **The password is write-only.** The server never returns it, so the box starts empty and an empty box means
 * "keep the saved one" on save *and* on test. It is cleared from memory after a successful save.
 */
@Component({
  selector: 'sda-email-account-panel',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel" aria-labelledby="smtp-title" data-testid="smtp-panel">
      <h2 class="adm-panel__title" id="smtp-title">Email account</h2>
      <p class="adm-panel__hint">
        The mail account every email from the shop is sent through: order confirmations, bank
        transfer details, dispatch notices and password resets. Your email provider gives you these
        details. Until a mail server is saved here, the shop uses the account set up on the server,
        if there is one.
      </p>
      <p class="adm-panel__hint">
        <strong
          >Every change is sent as a security alert to all Super Admins, through both the old and
          the new account</strong
        >, so a change nobody expected is noticed.
      </p>

      <p class="sr-only" role="status" data-testid="smtp-announcement">{{ said() }}</p>

      @if (problem(); as failure) {
        @if (failure.kind !== 'validation') {
          <div class="adm-state adm-state--error" role="alert" data-testid="smtp-error">
            <p>{{ failure.message }}</p>
          </div>
        }
      }

      @if (sentTo(); as address) {
        <p class="adm-state" data-testid="smtp-test-sent">
          Test email sent to <strong>{{ address }}</strong
          >. Check that inbox (and its spam folder).
        </p>
      }

      <form class="adm-fields" (ngSubmit)="save()" novalidate>
        @for (field of fields; track field.name) {
          <div class="adm-field">
            <label class="adm-label" [attr.for]="'smtp-' + field.name">{{ field.label }}</label>
            <input
              class="adm-input"
              type="text"
              [id]="'smtp-' + field.name"
              [name]="field.name"
              [attr.autocomplete]="field.autocomplete"
              spellcheck="false"
              [attr.inputmode]="field.inputMode ?? null"
              [attr.aria-invalid]="fieldError(field.name) ? 'true' : null"
              [attr.aria-describedby]="'smtp-hint-' + field.name"
              [ngModel]="form()[field.name]"
              (ngModelChange)="patch(field.name, $event)"
              [attr.data-testid]="'smtp-field-' + field.name"
            />
            <span class="adm-panel__hint" [id]="'smtp-hint-' + field.name">{{ field.hint }}</span>
            @if (fieldError(field.name); as message) {
              <span class="adm-field-error" [attr.data-testid]="'smtp-error-' + field.name">{{
                message
              }}</span>
            }
          </div>

          @if (field.name === 'username') {
            <!-- Write-only: the server never returns it. type password with new-password so a browser neither
                 fills it with the admin's own login nor offers to save it. No backticks in this comment. -->
            <div class="adm-field">
              <label class="adm-label" for="smtp-password">Password</label>
              <input
                class="adm-input"
                type="password"
                id="smtp-password"
                name="password"
                autocomplete="new-password"
                spellcheck="false"
                [placeholder]="
                  passwordIsSet() ? 'A password is saved. Type a new one to replace it.' : 'Not set'
                "
                [attr.aria-invalid]="fieldError('password') ? 'true' : null"
                aria-describedby="smtp-hint-password"
                [ngModel]="password()"
                (ngModelChange)="password.set($event)"
                data-testid="smtp-field-password"
              />
              <span
                class="adm-panel__hint"
                id="smtp-hint-password"
                data-testid="smtp-password-state"
              >
                {{ passwordIsSet() ? 'A password is saved.' : 'No password is saved yet.' }}
                It is stored encrypted and is never shown again. Leave the box empty to keep the
                saved one.
              </span>
              @if (fieldError('password'); as message) {
                <span class="adm-field-error" data-testid="smtp-error-password">{{ message }}</span>
              }
            </div>
          }
        }

        <div class="adm-field adm-field--wide">
          <label class="adm-check">
            <input
              type="checkbox"
              name="useStartTls"
              aria-describedby="smtp-hint-useStartTls"
              [checked]="useStartTls()"
              (change)="patchStartTls($event)"
              data-testid="smtp-field-useStartTls"
            />
            <span>Encrypt the connection (STARTTLS)</span>
          </label>
          <span class="adm-panel__hint" id="smtp-hint-useStartTls">
            Keep this on unless your provider says otherwise. With it off nothing is encrypted, so a
            user name and password are not accepted. Servers that need SSL on port 465 are not
            supported; use the provider's port 587 instead.
          </span>
          @if (fieldError('useStartTls'); as message) {
            <span class="adm-field-error" data-testid="smtp-error-useStartTls">{{ message }}</span>
          }
        </div>

        <div class="adm-actions adm-field--wide">
          <!-- aria-disabled, not disabled, so pressing does not throw focus to the body (the bank form's reason). -->
          <button
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="busy() ? 'true' : null"
            data-testid="save-smtp"
          >
            {{ busy() === 'save' ? 'Saving…' : 'Save email account' }}
          </button>
          <button
            class="adm-btn"
            type="button"
            [attr.aria-disabled]="busy() ? 'true' : null"
            (click)="sendTest()"
            data-testid="test-smtp"
          >
            {{ busy() === 'test' ? 'Sending…' : 'Send test email' }}
          </button>
        </div>
        <p class="adm-panel__hint adm-field--wide">
          The test uses the details in the boxes above, saved or not, and goes to your own email
          address.
        </p>
      </form>
    </section>
  `,
})
export class EmailAccountPanel {
  /** The `Email.Smtp` group as the server sent it. */
  readonly group = input.required<AdminSettingGroup>();

  /** Raised after a successful save, so the page re-reads (the group's token has moved). */
  readonly saved = output<void>();

  private readonly api = inject(AdminSettingsApi);

  protected readonly fields: readonly SmtpField[] = [
    {
      name: 'host',
      label: 'Mail server',
      hint: 'From your email provider, for example the SMTP server name. No https:// and no port.',
      autocomplete: 'off',
    },
    {
      name: 'port',
      label: 'Port',
      hint: 'Usually 587. A whole number between 1 and 65535.',
      inputMode: 'numeric',
      autocomplete: 'off',
    },
    {
      name: 'username',
      label: 'User name',
      hint: 'Usually the full email address. Leave empty for a server that does not ask for one.',
      autocomplete: 'off',
    },
    {
      name: 'fromAddress',
      label: 'Send from address',
      hint: 'The address customers see emails come from. The account must be allowed to send from it.',
      inputMode: 'email',
      autocomplete: 'off',
    },
    {
      name: 'fromName',
      label: 'Send from name',
      hint: 'Shown beside the address in the inbox. Leave empty to show the address alone.',
      autocomplete: 'off',
    },
  ];

  protected readonly form = signal<SmtpTextValues>(emptyForm());
  protected readonly password = signal('');
  protected readonly useStartTls = signal(true);
  protected readonly busy = signal<'save' | 'test' | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');
  protected readonly sentTo = signal<string | null>(null);

  protected readonly passwordIsSet = computed(
    () =>
      this.group().settings.find((setting) => setting.key === SETTING_KEYS.smtpPassword)
        ?.hasValue ?? false,
  );

  constructor() {
    // Every time the server sends the group (first read and every re-read after a save), the boxes show it.
    effect(() => {
      const group = this.group();
      const value = (key: string) =>
        group.settings.find((setting) => setting.key === key)?.value ?? null;

      this.form.set({
        host: value(SETTING_KEYS.smtpHost),
        port: value(SETTING_KEYS.smtpPort),
        username: value(SETTING_KEYS.smtpUsername),
        fromAddress: value(SETTING_KEYS.smtpFromAddress),
        fromName: value(SETTING_KEYS.smtpFromName),
      });

      // An unsaved switch counts as on, as it does on the server.
      this.useStartTls.set(value(SETTING_KEYS.smtpUseStartTls) !== 'false');
    });
  }

  protected patch(field: keyof SmtpTextValues, value: string): void {
    this.form.update((current) => ({ ...current, [field]: value }));
    this.clearFieldError(field);
  }

  protected patchStartTls(event: Event): void {
    this.useStartTls.set((event.target as HTMLInputElement | null)?.checked ?? false);
    this.clearFieldError('useStartTls');
  }

  protected fieldError(field: string): string | null {
    const failure = this.problem();
    return failure?.kind === 'validation' ? (failure.fieldErrors?.[field]?.[0] ?? null) : null;
  }

  protected save(): void {
    if (this.busy()) {
      return;
    }

    this.begin('save', 'Saving the email account…');

    this.api.updateSmtp(this.body(), this.group().rowVersion).subscribe({
      next: () => {
        this.password.set('');
        this.busy.set(null);
        this.said.set('Email account saved. A security alert has been sent to every Super Admin.');
        this.saved.emit();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  protected sendTest(): void {
    if (this.busy()) {
      return;
    }

    this.begin('test', 'Sending a test email…');

    this.api.testSmtp(this.body()).subscribe({
      next: (response) => {
        this.busy.set(null);
        this.sentTo.set(response.sentTo);
        this.said.set(`Test email sent to ${response.sentTo}.`);
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  /** Every text box, the switch, and the password only if one was typed (blank keeps the saved one). */
  private body(): SmtpTextValues & { readonly password?: string; readonly useStartTls: boolean } {
    const typed = this.password();
    return {
      ...this.form(),
      ...(typed.trim().length > 0 ? { password: typed } : {}),
      useStartTls: this.useStartTls(),
    };
  }

  private begin(kind: 'save' | 'test', message: string): void {
    this.busy.set(kind);
    this.problem.set(null);
    this.sentTo.set(null);
    this.said.set(message);
  }

  private fail(error: unknown): void {
    this.busy.set(null);
    this.said.set('');
    this.problem.set(toCatalogFailure(error));
  }

  private clearFieldError(field: string): void {
    this.problem.update((current) =>
      current?.kind === 'validation' && current.fieldErrors?.[field] ? null : current,
    );
  }
}

function emptyForm(): SmtpTextValues {
  return Object.fromEntries(
    SMTP_TEXT_FIELDS.map((field) => [field, null]),
  ) as unknown as SmtpTextValues;
}
