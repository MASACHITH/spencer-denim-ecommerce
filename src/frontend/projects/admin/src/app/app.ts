import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

@Component({
  selector: 'sda-root',
  imports: [RouterOutlet, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="admin-header">
      <a class="admin-header__brand" routerLink="/">Spencer Denim Admin</a>
    </header>
    <main id="main">
      <router-outlet />
    </main>
  `,
  styles: `
    :host { display: block; min-height: 100vh; background: var(--color-bg-alt); }
    .admin-header {
      display: flex;
      align-items: center;
      min-height: calc(var(--tap) + var(--space-2));
      padding-inline: var(--gutter);
      background: var(--color-primary);
      color: var(--color-on-primary);
    }
    .admin-header__brand {
      font-size: var(--text-small);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-decoration: none;
    }
    .admin-header__brand:focus-visible { outline-color: var(--color-on-primary); }
  `,
})
export class App {}
