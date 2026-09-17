import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ApiStatus } from '@spencer-denim/shared';

/** Scaffold placeholder home page (W0-09). Replaced by HOME-01..10. */
@Component({
  selector: 'sd-home-page',
  imports: [ApiStatus],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="hero">
      <div class="container hero__inner">
        <p class="eyebrow">New season</p>
        <h1 class="hero__title">Spencer Denim</h1>
        <p class="hero__lead">Timeless denim and everyday essentials. Our new store is on its way.</p>
        <sd-api-status />
      </div>
    </section>
  `,
  styles: `
    :host { display: block; }
    .hero { background: var(--color-bg-alt); }
    .hero__inner {
      display: grid;
      gap: var(--space-3);
      padding-block: var(--space-12);
    }
    .eyebrow {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .hero__title { font-size: var(--text-display); letter-spacing: -0.01em; }
    .hero__lead { max-width: 40ch; color: var(--color-text-muted); }
  `,
})
export class HomePage {}
