import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { loginPath } from '../../core/auth/auth-guards';
import { encodeQr, qrSvgPath } from '../../core/two-factor/qr-code';
import { RecoveryCodesPanel } from '../../core/two-factor/recovery-codes-panel';
import { TwoFactorApi, type TwoFactorError, type TwoFactorSetup } from '../../core/two-factor/two-factor-api';

type SetupState = 'loading' | 'scan' | 'done' | 'already-on' | 'failed';

/** Modules of light margin around the QR code, as the standard asks for. */
const quietZone = 4;

/**
 * W1-10: set up two-factor authentication with an authenticator app. Mandatory for Super Admins — until they do
 * it, this is the only admin page the API lets them use (doc 08 §3.1 N14) — and available to every other staff
 * member. Scan the QR code (drawn here in the browser, so the key never goes to another service) or type the key,
 * confirm with a code, then save the recovery codes, which are shown only this once.
 */
@Component({
  selector: 'sda-two-factor-setup-page',
  imports: [FormsModule, RouterLink, RecoveryCodesPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="setup">
      <div class="setup__card">
        <h1 class="setup__title" id="setup-title" tabindex="-1" #title>Set up two-factor authentication</h1>

        @switch (state()) {
          @case ('loading') {
            <p class="setup__lead" role="status">Preparing your set-up key…</p>
          }

          @case ('failed') {
            <div class="setup__error" role="alert">
              <p class="setup__error-text">{{ errorMessage() }}</p>
            </div>
            <button class="setup__secondary" type="button" (click)="load()">Try again</button>
          }

          @case ('already-on') {
            <p class="setup__lead">Two-factor authentication is already on for this account.</p>
            <a class="setup__link-button" routerLink="/security">Go to account security</a>
          }

          @case ('scan') {
            @if (required()) {
              <p class="setup__lead">
                Super Admin accounts must use two-factor authentication. Set it up to continue to the admin panel.
              </p>
            } @else {
              <p class="setup__lead">Protect your account with a code from your phone each time you sign in.</p>
            }

            <ol class="setup__steps">
              <li>
                Install an authenticator app on your phone, such as Google Authenticator, Microsoft Authenticator or
                1Password.
              </li>
              <li>
                Scan this QR code with the app.
                <svg
                  class="setup__qr"
                  role="img"
                  aria-labelledby="qr-title"
                  [attr.viewBox]="'0 0 ' + qrViewSize() + ' ' + qrViewSize()"
                  shape-rendering="crispEdges"
                >
                  <title id="qr-title">QR code for your authenticator app</title>
                  <rect class="setup__qr-background" [attr.width]="qrViewSize()" [attr.height]="qrViewSize()" />
                  <path class="setup__qr-modules" [attr.d]="qrPath()" />
                </svg>
                <p class="setup__hint">Can't scan it? Choose "enter a setup key" in the app and type:</p>
                <p class="setup__key" id="setup-key">{{ setup()?.sharedKey }}</p>
                <button class="setup__secondary setup__secondary--small" type="button" (click)="copyKey()">
                  Copy key
                </button>
                <p class="setup__hint" role="status">{{ copyStatus() }}</p>
              </li>
              <li>
                <form class="setup__form" (ngSubmit)="confirm()" novalidate>
                  <label class="setup__field">
                    <span class="setup__label">Code from the app</span>
                    <input
                      #codeInput
                      class="setup__input"
                      type="text"
                      name="code"
                      autocomplete="one-time-code"
                      inputmode="numeric"
                      pattern="[0-9 ]*"
                      maxlength="7"
                      required
                      [attr.aria-invalid]="codeInError() ? 'true' : null"
                      [attr.aria-describedby]="codeInError() ? 'setup-code-hint setup-error' : 'setup-code-hint'"
                      [ngModel]="code"
                      (ngModelChange)="code = $event"
                      [readonly]="submitting()"
                    />
                  </label>
                  <p class="setup__hint" id="setup-code-hint">Enter the 6-digit code the app shows for this account.</p>

                  @if (errorMessage(); as message) {
                    <div class="setup__error" id="setup-error" role="alert" tabindex="-1" #errorSummary>
                      <p class="setup__error-text">{{ message }}</p>
                    </div>
                  }

                  <button class="setup__submit" type="submit" [disabled]="submitting() || !code.trim()">
                    {{ submitting() ? 'Checking…' : 'Turn on two-factor authentication' }}
                  </button>
                </form>
              </li>
            </ol>
          }

          @case ('done') {
            <p class="setup__lead" role="status">Two-factor authentication is on.</p>
            <sda-recovery-codes-panel [codes]="recoveryCodes()" [account]="account()" />
            <label class="setup__check">
              <input type="checkbox" name="saved" [ngModel]="saved()" (ngModelChange)="saved.set($event)" />
              <span>I have saved my recovery codes</span>
            </label>
            <button class="setup__submit" type="button" [disabled]="!saved()" (click)="finish()">Continue</button>
          }
        }

        @if (state() !== 'done') {
          <button class="setup__secondary" type="button" (click)="logout()" [disabled]="submitting()">Sign out</button>
        }
      </div>
    </section>
  `,
  styles: `
    :host { display: block; }
    .setup { display: flex; justify-content: center; padding: var(--gutter); }
    .setup__card {
      display: grid;
      gap: var(--space-2);
      width: 100%;
      max-width: 560px;
      min-width: 0;
      padding: var(--space-4);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    @media (max-width: 400px) {
      .setup__card { padding: var(--space-2); }
    }
    .setup__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .setup__title:focus { outline: none; }
    .setup__lead,
    .setup__hint { color: var(--color-text-muted); font-size: var(--text-small); }
    .setup__hint:empty { display: none; }
    .setup__steps {
      display: grid;
      gap: var(--space-3);
      margin: 0;
      padding-inline-start: var(--space-3);
      font-size: var(--text-body);
    }
    /* Kept as list items (a grid item loses its number); children are spaced instead. */
    .setup__steps > li { min-width: 0; padding-inline-start: var(--space-0-5); }
    .setup__steps > li > * + * { margin-top: var(--space-1); }
    .setup__qr {
      display: block;
      width: 100%;
      max-width: 220px;
      height: auto;
      border: 1px solid var(--color-border);
    }
    /* Always dark modules on a light ground: scanners need that contrast whatever the theme. */
    .setup__qr-background { fill: var(--color-surface); }
    .setup__qr-modules { fill: var(--color-text); }
    .setup__key {
      margin: 0;
      padding: var(--space-1) var(--space-1-5);
      font-size: var(--text-data);
      font-variant-numeric: tabular-nums;
      letter-spacing: var(--tracking-label);
      overflow-wrap: anywhere;
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }
    .setup__form { display: grid; gap: var(--space-1-5); }
    .setup__field { display: grid; gap: var(--space-0-5); }
    .setup__label {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .setup__input {
      width: 100%;
      min-width: 0;
      max-width: 14rem;
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      font-size: var(--text-h4);
      font-variant-numeric: tabular-nums;
      letter-spacing: var(--tracking-label);
      border: 1px solid var(--color-border-strong);
      background: var(--color-bg);
    }
    .setup__input[aria-invalid='true'] { border-color: var(--color-sale); }
    .setup__error {
      padding: var(--space-1-5);
      font-size: var(--text-small);
      color: var(--color-sale);
      background: color-mix(in srgb, var(--color-sale) 8%, transparent);
      border: 1px solid var(--color-sale);
    }
    .setup__error:focus-visible { outline-offset: 2px; }
    .setup__error-text { margin: 0; }
    .setup__check {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      font-size: var(--text-small);
    }
    .setup__check input { width: 1.25rem; height: 1.25rem; flex: none; }
    .setup__submit,
    .setup__secondary,
    .setup__link-button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: var(--tap);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-align: center;
      text-decoration: none;
      cursor: pointer;
    }
    .setup__submit {
      font-weight: var(--weight-semibold);
      color: var(--color-on-primary);
      background: var(--color-primary);
      border: none;
    }
    .setup__submit:hover:not(:disabled) { background: var(--color-primary-hover); }
    .setup__secondary,
    .setup__link-button {
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }
    .setup__secondary--small { padding-inline: var(--space-2); }
    .setup__submit:disabled,
    .setup__secondary:disabled { opacity: 0.6; cursor: not-allowed; }
  `,
})
export class TwoFactorSetupPage implements OnInit {
  private readonly twoFactorApi = inject(TwoFactorApi);
  private readonly authApi = inject(AdminAuthApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeInput');
  private readonly errorSummary = viewChild<ElementRef<HTMLElement>>('errorSummary');
  private readonly title = viewChild<ElementRef<HTMLElement>>('title');

  protected code = '';
  protected readonly state = signal<SetupState>('loading');
  protected readonly required = signal(false);
  protected readonly setup = signal<TwoFactorSetup | null>(null);
  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly codeInError = signal(false);
  protected readonly copyStatus = signal('');
  protected readonly recoveryCodes = signal<readonly string[]>([]);
  protected readonly saved = signal(false);

  private readonly qr = computed(() => {
    const setup = this.setup();
    return setup ? encodeQr(setup.authenticatorUri) : null;
  });
  protected readonly qrViewSize = computed(() => (this.qr()?.size ?? 21) + quietZone * 2);
  protected readonly qrPath = computed(() => {
    const qr = this.qr();
    return qr ? qrSvgPath(qr, quietZone) : '';
  });

  /** The account label from the otpauth URI, for the heading of the downloaded codes. */
  protected readonly account = computed(() => {
    const uri = this.setup()?.authenticatorUri ?? '';
    const label = uri.replace(/^otpauth:\/\/totp\//, '').split('?')[0];
    const account = label.includes(':') ? label.slice(label.indexOf(':') + 1) : label;
    try {
      return decodeURIComponent(account);
    } catch {
      return '';
    }
  });

  ngOnInit(): void {
    this.load();
  }

  /**
   * Status first, then set-up, one after the other: starting set-up can rotate the account's security stamp and
   * re-issue this session's cookie, so no other request may be in flight with the old cookie meanwhile.
   */
  protected load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);

    this.twoFactorApi.status().subscribe({
      next: (status) => {
        this.required.set(status.required);
        if (status.enabled) {
          this.state.set('already-on');
          return;
        }

        this.twoFactorApi.setup().subscribe({
          next: (setup) => {
            this.setup.set(setup);
            this.state.set('scan');
          },
          error: (error: TwoFactorError) => this.fail(error),
        });
      },
      error: (error: TwoFactorError) => this.fail(error),
    });
  }

  protected confirm(): void {
    if (this.submitting() || !this.code.trim()) {
      return;
    }

    this.errorMessage.set(null);
    this.codeInError.set(false);
    this.submitting.set(true);

    this.twoFactorApi.enable(this.code).subscribe({
      next: (codes) => {
        this.submitting.set(false);
        this.recoveryCodes.set(codes);
        this.code = '';
        this.state.set('done');
        this.focusAfterRender(() => this.title());
      },
      error: (error: TwoFactorError) => {
        this.submitting.set(false);
        if (error.kind === 'invalid_code') {
          this.codeInError.set(true);
          this.errorMessage.set(error.message);
          this.focusAfterRender(() => this.codeInput());
          return;
        }

        if (error.kind === 'two_factor_already_enabled') {
          this.state.set('already-on');
          return;
        }

        this.fail(error);
      },
    });
  }

  protected copyKey(): void {
    const key = this.setup()?.sharedKey ?? '';
    const clipboard = globalThis.navigator?.clipboard;
    if (!clipboard) {
      this.copyStatus.set('Copying is not available in this browser. Select the key and copy it instead.');
      return;
    }

    clipboard.writeText(key.replace(/\s/g, '')).then(
      () => this.copyStatus.set('Key copied.'),
      () => this.copyStatus.set('Could not copy. Select the key and copy it instead.'),
    );
  }

  protected finish(): void {
    void this.router.navigateByUrl('/');
  }

  protected logout(): void {
    this.authApi.logout().subscribe({
      next: () => void this.router.navigateByUrl(loginPath),
      error: () => void this.router.navigateByUrl(loginPath),
    });
  }

  private fail(error: TwoFactorError): void {
    if (error.kind === 'session_expired') {
      void this.router.navigateByUrl(loginPath);
      return;
    }

    this.errorMessage.set(error.message);
    if (this.state() === 'scan') {
      this.focusAfterRender(() => this.errorSummary());
      return;
    }

    this.state.set('failed');
  }

  private focusAfterRender(target: () => ElementRef<HTMLElement> | undefined): void {
    afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
  }
}
