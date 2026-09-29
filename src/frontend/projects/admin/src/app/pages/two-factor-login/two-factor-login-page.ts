import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { AdminAuthApi, type TwoFactorLoginError } from '../../core/auth/admin-auth-api';
import { pendingAccountStep } from '../../core/auth/auth-guards';

/**
 * W1-10: the second sign-in step for an account with two-factor authentication. The password step has left a
 * short-lived pending cookie; this page sends the six-digit code from the authenticator app, or one of the
 * single-use recovery codes. Wrong codes count towards the same lockout as wrong passwords.
 */
@Component({
  selector: 'sda-two-factor-login-page',
  imports: [FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="step">
      <form class="step__card" (ngSubmit)="submit()" novalidate aria-labelledby="step-title">
        <h1 class="step__title" id="step-title">Two-factor authentication</h1>
        @if (useRecoveryCode()) {
          <p class="step__lead" id="step-lead">
            Enter one of the recovery codes you saved when you set up two-factor authentication. Each code works once.
          </p>
        } @else {
          <p class="step__lead" id="step-lead">Enter the 6-digit code from your authenticator app.</p>
        }

        <!--
          Read-only rather than disabled while checking, so it can take focus back the moment an error arrives.
          The field is described by the lead text, and by the error only when the error is about the code.
        -->
        <label class="step__field">
          <span class="step__label">{{ useRecoveryCode() ? 'Recovery code' : 'Authentication code' }}</span>
          <!-- One field for both kinds, so the label always has its control; only its hints change. -->
          <input
            #codeInput
            class="step__input step__input--code"
            type="text"
            name="code"
            spellcheck="false"
            required
            [attr.autocomplete]="useRecoveryCode() ? 'off' : 'one-time-code'"
            [attr.inputmode]="useRecoveryCode() ? 'text' : 'numeric'"
            [attr.pattern]="useRecoveryCode() ? null : '[0-9 ]*'"
            [attr.autocapitalize]="useRecoveryCode() ? 'characters' : null"
            [attr.maxlength]="useRecoveryCode() ? 20 : 7"
            [attr.aria-invalid]="codeInError() ? 'true' : null"
            [attr.aria-describedby]="codeInError() ? 'step-lead step-error' : 'step-lead'"
            [ngModel]="code"
            (ngModelChange)="code = $event"
            [readonly]="submitting()"
          />
        </label>

        @if (errorMessage(); as message) {
          <div class="step__error" id="step-error" role="alert" tabindex="-1" #errorSummary>
            <p class="step__error-text">{{ message }}</p>
            @if (expired()) {
              <a class="step__error-link" routerLink="/login">Back to sign in</a>
            }
          </div>
        }

        <button class="step__submit" type="submit" [disabled]="submitting() || expired() || !code.trim()">
          {{ submitting() ? 'Checking…' : 'Verify' }}
        </button>
        <button class="step__secondary" type="button" (click)="toggleMode()" [disabled]="submitting()">
          {{ useRecoveryCode() ? 'Use my authenticator app instead' : 'Use a recovery code instead' }}
        </button>
        <a class="step__back" routerLink="/login">Cancel and sign in again</a>
      </form>
    </section>
  `,
  styles: `
    :host { display: block; min-height: 100vh; }
    .step {
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100vh;
      padding: var(--gutter);
      background: var(--color-bg-alt);
    }
    .step__card {
      display: grid;
      gap: var(--space-2);
      width: 100%;
      max-width: 400px;
      padding: var(--space-4);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    .step__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .step__lead { color: var(--color-text-muted); font-size: var(--text-small); }
    .step__field { display: grid; gap: var(--space-0-5); }
    .step__label {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .step__input {
      width: 100%;
      min-width: 0;
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-bg);
    }
    .step__input--code {
      font-size: var(--text-h4);
      font-variant-numeric: tabular-nums;
      letter-spacing: var(--tracking-label);
    }
    .step__input[aria-invalid='true'] { border-color: var(--color-sale); }
    .step__error {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-1-5);
      font-size: var(--text-small);
      color: var(--color-sale);
      background: color-mix(in srgb, var(--color-sale) 8%, transparent);
      border: 1px solid var(--color-sale);
    }
    .step__error:focus-visible { outline-offset: 2px; }
    .step__error-text { margin: 0; }
    .step__error-link,
    .step__back { color: var(--color-text); font-size: var(--text-small); }
    .step__back { justify-self: center; }
    .step__submit,
    .step__secondary {
      min-height: var(--tap);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
    }
    .step__submit {
      margin-top: var(--space-1);
      font-weight: var(--weight-semibold);
      color: var(--color-on-primary);
      background: var(--color-primary);
      border: none;
    }
    .step__submit:hover:not(:disabled) { background: var(--color-primary-hover); }
    .step__secondary {
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }
    .step__submit:disabled,
    .step__secondary:disabled { opacity: 0.6; cursor: not-allowed; }
  `,
})
export class TwoFactorLoginPage {
  private readonly authApi = inject(AdminAuthApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeInput');
  private readonly errorSummary = viewChild<ElementRef<HTMLElement>>('errorSummary');

  protected code = '';
  protected readonly useRecoveryCode = signal(false);
  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  /** The error is about what was typed (the field is marked invalid and focused), not a lockout or expiry. */
  protected readonly codeInError = signal(false);
  protected readonly expired = signal(false);

  constructor() {
    // Arriving from the password step, the code is the only thing to do: put the cursor there (tester finding),
    // instead of leaving focus on the page body.
    this.focusAfterRender(() => this.codeInput());
  }

  protected toggleMode(): void {
    this.useRecoveryCode.update((value) => !value);
    this.code = '';
    this.errorMessage.set(null);
    this.codeInError.set(false);
    this.focusAfterRender(() => this.codeInput());
  }

  protected submit(): void {
    if (this.submitting() || this.expired() || !this.code.trim()) {
      return;
    }

    this.errorMessage.set(null);
    this.codeInError.set(false);
    this.submitting.set(true);

    this.authApi.completeTwoFactorLogin(this.code, this.useRecoveryCode()).subscribe({
      next: (session) => {
        this.submitting.set(false);
        void this.router.navigateByUrl(pendingAccountStep(session) ?? '/');
      },
      error: (error: TwoFactorLoginError) => {
        this.submitting.set(false);
        this.showError(error);
      },
    });
  }

  /**
   * Moves focus to the code when it was wrong (so it can be retyped at once), otherwise to the error itself, so
   * focus never falls back to the page body.
   */
  private showError(error: TwoFactorLoginError): void {
    const aboutTheCode = error.kind === 'invalid_two_factor_code';
    this.codeInError.set(aboutTheCode);
    this.expired.set(error.kind === 'two_factor_session_expired' || error.kind === 'locked_out');
    this.errorMessage.set(error.kind === 'locked_out' ? lockedOutMessage(error) : error.message);
    this.focusAfterRender(() => (aboutTheCode ? this.codeInput() : this.errorSummary()));
  }

  private focusAfterRender(target: () => ElementRef<HTMLElement> | undefined): void {
    afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
  }
}

function lockedOutMessage(error: TwoFactorLoginError): string {
  if (!error.lockoutEndUtc) {
    return error.message;
  }

  const until = new Date(error.lockoutEndUtc);
  if (Number.isNaN(until.getTime())) {
    return error.message;
  }

  const time = until.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  return `Too many wrong codes. This account is locked until ${time}. Sign in again after that.`;
}
