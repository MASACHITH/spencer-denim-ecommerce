import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

@Component({
  selector: 'sd-root',
  imports: [RouterOutlet, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="skip-link sr-only" href="#main">Skip to content</a>
    <header class="site-header">
      <div class="container site-header__inner">
        <a class="wordmark" routerLink="/">Spencer Denim</a>
      </div>
    </header>
    <main id="main" tabindex="-1">
      <router-outlet />
    </main>
  `,
  styles: `
    :host { display: block; min-height: 100vh; }
    .site-header { border-bottom: 1px solid var(--color-border); background: var(--color-surface); }
    .site-header__inner { display: flex; align-items: center; min-height: calc(var(--tap) + var(--space-2)); }
    .wordmark {
      font-size: var(--text-small);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-wordmark);
      text-transform: uppercase;
      text-decoration: none;
    }
    .skip-link:focus { position: static !important; width: auto; height: auto; margin: 0; clip: auto; }
    main:focus { outline: none; }
  `,
})
export class App {}
