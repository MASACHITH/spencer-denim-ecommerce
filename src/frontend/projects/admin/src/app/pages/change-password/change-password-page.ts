import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';

import { AdminAuthApi, type ChangePasswordError } from '../../core/auth/admin-auth-api';
import { loginPath } from '../../core/auth/auth-guards';

/**
 * Issue #17: the signed-in admin changes their own password. When the account was flagged "must change password"
 * (the first Super Admin's bootstrap password, doc 08 §9) this is the only page the admin app lets them reach,
 * and the API refuses everything else until the change is made.
 */
@Component({
  selector: 'sda-change-password-page',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="change">
      <form class="change__card" (ngSubmit)="submit()" novalidate aria-labelledby="change-title">
        <h1 class="change__title" id="change-title">Change your password</h1>
        @if (required()) {
          <p class="change__lead">
            You are signed in with a temporary password. Choose a new one to continue to the admin panel.
          </p>
        } @else {
          <p class="change__lead">Choose a new password. Other devices signed in to this account will be signed out.</p>
        }

        <!-- Lets password managers file the new password under the right account. -->
        <input type="email" name="username" autocomplete="username" [value]="email()" hidden readonly />

        <label class="change__field">
          <span class="change__label">Current password</span>
          <input
            class="change__input"
            type="password"
            name="currentPassword"
            autocomplete="current-password"
            required
            [attr.aria-invalid]="errorKind() === 'invalid_current_password' ? 'true' : null"
            [attr.aria-describedby]="errorMessage() ? 'change-error' : null"
            [ngModel]="currentPassword"
            (ngModelChange)="currentPassword = $event"
            [disabled]="submitting()"
          />
        </label>

        <label class="change__field">
          <span class="change__label">New password</span>
          <input
            class="change__input"
            type="password"
            name="newPassword"
            autocomplete="new-password"
            required
            aria-describedby="change-rules"
            [attr.aria-invalid]="errorKind() === 'new_password_rejected' ? 'true' : null"
            [ngModel]="newPassword"
            (ngModelChange)="newPassword = $event"
            [disabled]="submitting()"
          />
        </label>
        <p class="change__hint" id="change-rules">
          Use at least 6 characters, with an upper-case letter, a lower-case letter, a number and a symbol.
        </p>

        <label class="change__field">
          <span class="change__label">Confirm new password</span>
          <input
            class="change__input"
            type="password"
            name="confirmPassword"
            autocomplete="new-password"
            required
            [attr.aria-invalid]="mismatch() ? 'true' : null"
            [ngModel]="confirmPassword"
            (ngModelChange)="confirmPassword = $event"
            [disabled]="submitting()"
          />
        </label>

        @if (errorMessage(); as message) {
          <div class="change__error" id="change-error" role="alert">
            <p class="change__error-text">{{ message }}</p>
            @if (rules().length > 0) {
              <ul class="change__rules">
                @for (rule of rules(); track rule) {
                  <li>{{ rule }}</li>
                }
              </ul>
            }
          </div>
        }

        <button
          class="change__submit"
          type="submit"
          [disabled]="submitting() || !currentPassword || !newPassword || !confirmPassword"
        >
          {{ submitting() ? 'Saving…' : 'Change password' }}
        </button>
        <button class="change__secondary" type="button" (click)="logout()" [disabled]="submitting()">
          Sign out
        </button>
      </form>
    </section>
  `,
  styles: `
    :host { display: block; }
    .change {
      display: flex;
      justify-content: center;
      padding: var(--gutter);
    }
    .change__card {
      display: grid;
      gap: var(--space-2);
      width: 100%;
      max-width: 420px;
      padding: var(--space-4);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    .change__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .change__lead,
    .change__hint { color: var(--color-text-muted); font-size: var(--text-small); }
    .change__hint { margin-top: calc(-1 * var(--space-1)); }
    .change__field { display: grid; gap: var(--space-0-5); }
    .change__label {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .change__input {
      width: 100%;
      min-width: 0;
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-bg);
    }
    .change__input[aria-invalid='true'] { border-color: var(--color-sale); }
    .change__error {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-1-5);
      font-size: var(--text-small);
      color: var(--color-sale);
      background: color-mix(in srgb, var(--color-sale) 8%, transparent);
      border: 1px solid var(--color-sale);
    }
    .change__error-text { margin: 0; }
    .change__rules { margin: 0; padding-inline-start: var(--space-2); overflow-wrap: anywhere; }
    .change__submit,
    .change__secondary {
      min-height: var(--tap);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
    }
    .change__submit {
      margin-top: var(--space-1);
      font-weight: var(--weight-semibold);
      color: var(--color-on-primary);
      background: var(--color-primary);
      border: none;
    }
    .change__submit:hover:not(:disabled) { background: var(--color-primary-hover); }
    .change__secondary {
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }
    .change__submit:disabled,
    .change__secondary:disabled { opacity: 0.6; cursor: not-allowed; }
  `,
})
export class ChangePasswordPage implements OnInit {
  private readonly authApi = inject(AdminAuthApi);
  private readonly router = inject(Router);

  protected currentPassword = '';
  protected newPassword = '';
  protected confirmPassword = '';
  protected readonly required = signal(false);
  protected readonly email = signal('');
  protected readonly submitting = signal(false);
  protected readonly mismatch = signal(false);
  protected readonly errorKind = signal<ChangePasswordError['kind'] | 'mismatch' | null>(null);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly rules = signal<string[]>([]);

  ngOnInit(): void {
    // Only changes the wording; the guard has already made sure there is a session.
    this.authApi.whoAmI().subscribe({
      next: (session) => {
        this.required.set(session.mustChangePassword);
        this.email.set(session.email);
      },
      error: () => this.required.set(false),
    });
  }

  protected submit(): void {
    if (this.submitting() || !this.currentPassword || !this.newPassword || !this.confirmPassword) {
      return;
    }

    this.rules.set([]);
    if (this.newPassword !== this.confirmPassword) {
      this.mismatch.set(true);
      this.errorKind.set('mismatch');
      this.errorMessage.set('The new passwords do not match.');
      return;
    }

    this.mismatch.set(false);
    this.errorKind.set(null);
    this.errorMessage.set(null);
    this.submitting.set(true);

    this.authApi.changePassword(this.currentPassword, this.newPassword).subscribe({
      next: () => {
        this.submitting.set(false);
        void this.router.navigateByUrl('/');
      },
      error: (error: ChangePasswordError) => {
        this.submitting.set(false);
        if (error.kind === 'session_expired') {
          void this.router.navigateByUrl(loginPath);
          return;
        }

        this.errorKind.set(error.kind);
        this.errorMessage.set(error.message);
        this.rules.set(error.rules ?? []);
      },
    });
  }

  protected logout(): void {
    this.authApi.logout().subscribe({
      next: () => void this.router.navigateByUrl(loginPath),
      error: () => void this.router.navigateByUrl(loginPath),
    });
  }
}
