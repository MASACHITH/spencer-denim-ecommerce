import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';

import { AdminAuthApi, type AdminLoginError } from '../../core/auth/admin-auth-api';
import { changePasswordPath } from '../../core/auth/auth-guards';

/**
 * ADM-01 admin sign-in. Redirects to the dashboard on success — or to the change-password page when the account
 * must change its password first (issue #17) — and shows a neutral error otherwise (lesson L1).
 */
@Component({
  selector: 'sda-login-page',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="login">
      <form class="login__card" (ngSubmit)="submit()" novalidate>
        <h1 class="login__title">Spencer Denim Admin</h1>
        <p class="login__lead">Sign in to manage the store.</p>

        <label class="login__field">
          <span class="login__label">Email</span>
          <input
            class="login__input"
            type="email"
            name="email"
            autocomplete="username"
            required
            [ngModel]="email"
            (ngModelChange)="email = $event"
            [disabled]="submitting()"
          />
        </label>

        <label class="login__field">
          <span class="login__label">Password</span>
          <input
            class="login__input"
            type="password"
            name="password"
            autocomplete="current-password"
            required
            [ngModel]="password"
            (ngModelChange)="password = $event"
            [disabled]="submitting()"
          />
        </label>

        @if (errorMessage(); as message) {
          <p class="login__error" role="alert">{{ message }}</p>
        }

        <button class="login__submit" type="submit" [disabled]="submitting() || !email || !password">
          {{ submitting() ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>
    </section>
  `,
  styles: `
    :host { display: block; min-height: 100vh; }
    .login {
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100vh;
      padding: var(--gutter);
      background: var(--color-bg-alt);
    }
    .login__card {
      display: grid;
      gap: var(--space-2);
      width: 100%;
      max-width: 360px;
      padding: var(--space-4);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    .login__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .login__lead { color: var(--color-text-muted); font-size: var(--text-small); }
    .login__field { display: grid; gap: var(--space-0-5); }
    .login__label {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .login__input {
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-bg);
    }
    .login__error {
      margin: 0;
      padding: var(--space-1-5);
      font-size: var(--text-small);
      color: var(--color-sale);
      background: color-mix(in srgb, var(--color-sale) 8%, transparent);
      border: 1px solid var(--color-sale);
    }
    .login__submit {
      min-height: var(--tap);
      margin-top: var(--space-1);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-on-primary);
      background: var(--color-primary);
      border: none;
      cursor: pointer;
    }
    .login__submit:hover:not(:disabled) { background: var(--color-primary-hover); }
    .login__submit:disabled { opacity: 0.6; cursor: not-allowed; }
  `,
})
export class LoginPage {
  private readonly authApi = inject(AdminAuthApi);
  private readonly router = inject(Router);

  protected email = '';
  protected password = '';
  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected submit(): void {
    if (this.submitting() || !this.email || !this.password) {
      return;
    }

    this.submitting.set(true);
    this.errorMessage.set(null);

    this.authApi.login(this.email, this.password).subscribe({
      next: (session) => {
        this.submitting.set(false);
        void this.router.navigateByUrl(session.mustChangePassword ? changePasswordPath : '/');
      },
      error: (error: AdminLoginError) => {
        this.submitting.set(false);
        this.errorMessage.set(error.message);
      },
    });
  }
}
