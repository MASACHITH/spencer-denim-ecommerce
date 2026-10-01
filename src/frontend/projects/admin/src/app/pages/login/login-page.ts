import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';

import { AdminAuthApi, type AdminLoginError } from '../../core/auth/admin-auth-api';
import {
  pendingAccountStep,
  returnUrlParam,
  sanitizeReturnUrl,
  twoFactorLoginPath,
} from '../../core/auth/auth-guards';

/**
 * ADM-01 admin sign-in. Redirects to the dashboard on success — or to the change-password page when the account
 * must change its password first (issue #17) — and shows a neutral error otherwise (lesson L1). When the route
 * guard sent the visitor here with a `returnUrl` (W1-11, #8: they were headed somewhere else in the admin app
 * before being asked to sign in), they land back there afterwards instead of always on the dashboard; a pending
 * forced password change still takes priority, carrying the same `returnUrl` forward to the change-password page.
 */
@Component({
  selector: 'sda-login-page',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="login">
      <form class="login__card" (ngSubmit)="submit()" novalidate>
        <!-- Not the application name: the header bar directly above this card already carries it, and it
             wrapped to two lines inside the card at all eight breakpoints. A heading that says what the
             page is for beats one that repeats the line above it. -->
        <h1 class="login__title">Sign in</h1>
        <p class="login__lead">Manage the store.</p>

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
  private readonly route = inject(ActivatedRoute);

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

    const returnUrl = sanitizeReturnUrl(this.route.snapshot.queryParamMap.get(returnUrlParam));

    this.authApi.login(this.email, this.password).subscribe({
      next: (reply) => {
        this.submitting.set(false);

        // A two-factor challenge is not a session yet, so there is no pending step to read from it.
        // The code is entered on its own page; carry returnUrl so the visitor still lands where they meant to.
        if ('twoFactorRequired' in reply && reply.twoFactorRequired) {
          void this.router.navigateByUrl(this.withReturnUrl(twoFactorLoginPath, returnUrl));
          return;
        }

        // Built as a string (rather than passed as a UrlTree) so a plain sign-in with no returnUrl still
        // navigates to the bare path, exactly as it did before returnUrl handling existed.
        const step = pendingAccountStep(reply);
        void this.router.navigateByUrl(step ? this.withReturnUrl(step, returnUrl) : (returnUrl ?? '/'));
      },
      error: (error: AdminLoginError) => {
        this.submitting.set(false);
        this.errorMessage.set(error.message);
      },
    });
  }

  /** A path with the visitor's intended destination attached, or the bare path when there is none. */
  private withReturnUrl(path: string, returnUrl: string | null): string {
    return returnUrl
      ? this.router.serializeUrl(
          this.router.createUrlTree([path], { queryParams: { [returnUrlParam]: returnUrl } }),
        )
      : path;
  }
}
