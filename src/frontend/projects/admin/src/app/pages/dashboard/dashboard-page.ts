import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiStatus } from '@spencer-denim/shared';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';

/** Scaffold placeholder dashboard (W0-09). Replaced by the full dashboard in a later milestone. */
@Component({
  selector: 'sda-dashboard-page',
  imports: [ApiStatus, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h1 class="panel__title">Spencer Denim Admin</h1>
      <p class="panel__lead">The admin panel is being built. More dashboards arrive in the next milestones.</p>
      <sd-api-status />
      <a class="panel__link" routerLink="/security">Account security</a>
      <button class="panel__logout" type="button" (click)="logout()" [disabled]="loggingOut()">
        {{ loggingOut() ? 'Signing out…' : 'Sign out' }}
      </button>
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .panel {
      display: grid;
      gap: var(--space-2);
      max-width: 720px;
      padding: var(--space-4);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      font-size: var(--text-data);
    }
    .panel__title { font-size: var(--text-h2); }
    .panel__lead { color: var(--color-text-muted); }
    .panel__link {
      justify-self: start;
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      color: var(--color-text);
    }
    .panel__logout {
      justify-self: start;
      min-height: var(--tap);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
      cursor: pointer;
    }
    .panel__logout:disabled { opacity: 0.6; cursor: not-allowed; }
  `,
})
export class DashboardPage {
  private readonly authApi = inject(AdminAuthApi);
  private readonly router = inject(Router);

  protected readonly loggingOut = signal(false);

  protected logout(): void {
    if (this.loggingOut()) {
      return;
    }

    this.loggingOut.set(true);
    this.authApi.logout().subscribe({
      next: () => {
        this.loggingOut.set(false);
        void this.router.navigateByUrl('/login');
      },
      error: () => {
        this.loggingOut.set(false);
        void this.router.navigateByUrl('/login');
      },
    });
  }
}
