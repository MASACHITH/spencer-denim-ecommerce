import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { loginPath } from '../../core/auth/auth-guards';
import { RecoveryCodesPanel } from '../../core/two-factor/recovery-codes-panel';
import { TwoFactorApi, type TwoFactorError, type TwoFactorStatus } from '../../core/two-factor/two-factor-api';

type PasswordAction = 'regenerate' | 'disable';

/**
 * W1-10: the signed-in admin's account security — whether two-factor authentication is on, how many recovery
 * codes are left, new recovery codes, and (for staff who are not Super Admins) turning it off. Both changes ask
 * for the current password again, with the same lockout as sign-in.
 */
@Component({
  selector: 'sda-security-page',
  imports: [FormsModule, RouterLink, RecoveryCodesPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="security">
      <div class="security__card">
        <h1 class="security__title">Account security</h1>

        @if (status(); as status) {
          <section class="security__section" aria-labelledby="two-factor-title">
            <h2 class="security__heading" id="two-factor-title">Two-factor authentication</h2>
            @if (status.enabled) {
              <p class="security__text">
                On. You enter a code from your authenticator app each time you sign in.
                <span class="security__muted">Recovery codes left: {{ status.recoveryCodesLeft }}.</span>
              </p>
              @if (status.recoveryCodesLeft <= 2) {
                <p class="security__warning">You are running out of recovery codes. Generate new ones below.</p>
              }
            } @else {
              <p class="security__text">Off. Turn it on to protect your account with a code from your phone.</p>
              <a class="security__button security__button--primary" routerLink="/two-factor/setup">
                Set up two-factor authentication
              </a>
            }
          </section>

          @if (newCodes().length > 0) {
            <sda-recovery-codes-panel [codes]="newCodes()" />
          }

          @if (status.enabled) {
            <form class="security__section" (ngSubmit)="submit()" novalidate aria-labelledby="password-title">
              <h2 class="security__heading" id="password-title">
                {{ action() === 'disable' ? 'Turn off two-factor authentication' : 'New recovery codes' }}
              </h2>
              <p class="security__text" id="password-lead">
                @if (action() === 'disable') {
                  Your account will be protected by your password only.
                } @else {
                  Your current recovery codes will stop working.
                }
                Enter your password to continue.
              </p>

              @if (status.canDisable) {
                <fieldset class="security__choice">
                  <legend class="security__label">What do you want to do?</legend>
                  <label class="security__radio">
                    <input type="radio" name="action" value="regenerate" [ngModel]="action()" (ngModelChange)="chooseAction($event)" />
                    <span>Generate new recovery codes</span>
                  </label>
                  <label class="security__radio">
                    <input type="radio" name="action" value="disable" [ngModel]="action()" (ngModelChange)="chooseAction($event)" />
                    <span>Turn off two-factor authentication</span>
                  </label>
                </fieldset>
              } @else if (status.required) {
                <p class="security__muted">Two-factor authentication cannot be turned off for a Super Admin.</p>
              }

              <label class="security__field">
                <span class="security__label">Current password</span>
                <input
                  #passwordInput
                  class="security__input"
                  type="password"
                  name="currentPassword"
                  autocomplete="current-password"
                  required
                  [attr.aria-invalid]="passwordInError() ? 'true' : null"
                  [attr.aria-describedby]="passwordInError() ? 'password-lead security-error' : 'password-lead'"
                  [ngModel]="password"
                  (ngModelChange)="password = $event"
                  [readonly]="submitting()"
                />
              </label>

              @if (errorMessage(); as message) {
                <div class="security__error" id="security-error" role="alert" tabindex="-1" #errorSummary>
                  <p class="security__error-text">{{ message }}</p>
                </div>
              }

              <button class="security__button security__button--primary" type="submit" [disabled]="submitting() || !password">
                @if (submitting()) {
                  Saving…
                } @else if (action() === 'disable') {
                  Turn off
                } @else {
                  Generate new codes
                }
              </button>
            </form>
          }
        } @else if (loadError(); as message) {
          <div class="security__error" role="alert"><p class="security__error-text">{{ message }}</p></div>
        } @else {
          <p class="security__text" role="status">Loading…</p>
        }

        <p class="security__text" role="status">{{ notice() }}</p>

        <nav class="security__links" aria-label="Account">
          <a routerLink="/change-password">Change password</a>
          <a routerLink="/">Back to the dashboard</a>
        </nav>
      </div>
    </section>
  `,
  styles: `
    :host { display: block; }
    .security { display: flex; justify-content: center; padding: var(--gutter); }
    .security__card {
      display: grid;
      gap: var(--space-3);
      width: 100%;
      max-width: 560px;
      min-width: 0;
      padding: var(--space-4);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    @media (max-width: 400px) {
      .security__card { padding: var(--space-2); }
    }
    .security__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .security__section { display: grid; gap: var(--space-1-5); min-width: 0; }
    .security__heading { font-size: var(--text-h4); font-weight: var(--weight-semibold); }
    .security__text { margin: 0; font-size: var(--text-small); }
    .security__text:empty { display: none; }
    .security__muted { color: var(--color-text-muted); font-size: var(--text-small); }
    .security__warning { margin: 0; font-size: var(--text-small); color: var(--color-warning); }
    .security__choice { display: grid; gap: var(--space-0-5); margin: 0; padding: 0; border: none; }
    .security__radio { display: flex; align-items: center; gap: var(--space-1); min-height: var(--tap); font-size: var(--text-small); }
    .security__radio input { width: 1.25rem; height: 1.25rem; flex: none; }
    .security__field { display: grid; gap: var(--space-0-5); }
    .security__label {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .security__input {
      width: 100%;
      min-width: 0;
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-bg);
    }
    .security__input[aria-invalid='true'] { border-color: var(--color-sale); }
    .security__error {
      padding: var(--space-1-5);
      font-size: var(--text-small);
      color: var(--color-sale);
      background: color-mix(in srgb, var(--color-sale) 8%, transparent);
      border: 1px solid var(--color-sale);
    }
    .security__error:focus-visible { outline-offset: 2px; }
    .security__error-text { margin: 0; }
    .security__button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      justify-self: start;
      min-height: var(--tap);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-align: center;
      text-decoration: none;
      cursor: pointer;
    }
    .security__button--primary {
      font-weight: var(--weight-semibold);
      color: var(--color-on-primary);
      background: var(--color-primary);
      border: none;
    }
    .security__button--primary:hover:not(:disabled) { background: var(--color-primary-hover); }
    .security__button:disabled { opacity: 0.6; cursor: not-allowed; }
    .security__links { display: flex; flex-wrap: wrap; gap: var(--space-2); font-size: var(--text-small); }
    .security__links a { color: var(--color-text); display: inline-flex; align-items: center; min-height: var(--tap); }
  `,
})
export class SecurityPage implements OnInit {
  private readonly twoFactorApi = inject(TwoFactorApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  private readonly passwordInput = viewChild<ElementRef<HTMLInputElement>>('passwordInput');
  private readonly errorSummary = viewChild<ElementRef<HTMLElement>>('errorSummary');

  protected password = '';
  protected readonly status = signal<TwoFactorStatus | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly action = signal<PasswordAction>('regenerate');
  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly passwordInError = signal(false);
  protected readonly newCodes = signal<readonly string[]>([]);
  protected readonly notice = signal('');

  ngOnInit(): void {
    this.refresh();
  }

  protected chooseAction(action: PasswordAction): void {
    this.action.set(action);
    this.errorMessage.set(null);
    this.passwordInError.set(false);
  }

  protected submit(): void {
    if (this.submitting() || !this.password) {
      return;
    }

    this.errorMessage.set(null);
    this.passwordInError.set(false);
    this.notice.set('');
    this.submitting.set(true);

    if (this.action() === 'disable') {
      this.twoFactorApi.disable(this.password).subscribe({
        next: () => this.succeeded([], 'Two-factor authentication is off.'),
        error: (error: TwoFactorError) => this.failed(error),
      });
      return;
    }

    this.twoFactorApi.regenerateRecoveryCodes(this.password).subscribe({
      next: (codes) => this.succeeded(codes, 'New recovery codes are ready. Your old codes no longer work.'),
      error: (error: TwoFactorError) => this.failed(error),
    });
  }

  private refresh(): void {
    this.twoFactorApi.status().subscribe({
      next: (status) => this.status.set(status),
      error: (error: TwoFactorError) => {
        if (error.kind === 'session_expired') {
          void this.router.navigateByUrl(loginPath);
          return;
        }
        this.loadError.set(error.message);
      },
    });
  }

  private succeeded(codes: readonly string[], notice: string): void {
    this.submitting.set(false);
    this.password = '';
    this.action.set('regenerate');
    this.newCodes.set(codes);
    this.notice.set(notice);
    this.refresh();
  }

  /** Focus goes to the password when it was wrong, otherwise to the error itself (lockout, server failure). */
  private failed(error: TwoFactorError): void {
    this.submitting.set(false);
    if (error.kind === 'session_expired') {
      void this.router.navigateByUrl(loginPath);
      return;
    }

    const aboutThePassword = error.kind === 'invalid_current_password';
    this.passwordInError.set(aboutThePassword);
    this.errorMessage.set(error.message);
    afterNextRender(
      () => (aboutThePassword ? this.passwordInput() : this.errorSummary())?.nativeElement.focus(),
      { injector: this.injector },
    );
  }
}
