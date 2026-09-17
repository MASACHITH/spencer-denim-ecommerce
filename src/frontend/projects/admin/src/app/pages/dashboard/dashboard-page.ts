import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ApiStatus } from '@spencer-denim/shared';

/** Scaffold placeholder dashboard (W0-09). Replaced by ADM-01 and the dashboard features. */
@Component({
  selector: 'sda-dashboard-page',
  imports: [ApiStatus],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h1 class="panel__title">Spencer Denim Admin</h1>
      <p class="panel__lead">The admin panel is being built. Sign-in and dashboards arrive in the next milestones.</p>
      <sd-api-status />
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
  `,
})
export class DashboardPage {}
