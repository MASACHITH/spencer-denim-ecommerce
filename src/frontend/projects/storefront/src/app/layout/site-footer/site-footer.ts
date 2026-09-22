import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { NavigationService } from '../../core/navigation/navigation.service';
import { SITE_CONFIG } from '../../core/config/site-config';

/** What the newsletter form is showing. There is no "subscribed": no backend accepts it yet. */
export type NewsletterState = 'idle' | 'coming-soon';

/**
 * The storefront footer (GLB-07): shop links, help and policies, contact, social and a newsletter
 * field.
 *
 * TODO(HOME-10): the newsletter has no endpoint yet, so submitting says exactly that. It must not
 * report success for something that was never stored.
 */
@Component({
  selector: 'sd-site-footer',
  imports: [Icon, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <footer class="site-footer">
      <div class="container">
        <section class="newsletter" aria-labelledby="newsletter-title">
          <div class="newsletter__intro">
            <h2 class="newsletter__title" id="newsletter-title">Join the Spencer Denim list</h2>
            <p class="t-small muted">New arrivals, restocks and sale previews. No more than twice a month.</p>
          </div>
          <form class="newsletter__form" (submit)="onNewsletterSubmit($event)">
            <label class="sr-only" for="newsletter-email">Email address</label>
            <input
              class="newsletter__input"
              id="newsletter-email"
              type="email"
              name="email"
              autocomplete="email"
              required
              placeholder="you@example.com"
            />
            <button class="btn btn--primary newsletter__submit" type="submit">Sign up</button>
          </form>
          <p class="newsletter__status t-small" role="status" data-testid="newsletter-status">
            @if (newsletterState() === 'coming-soon') {
              Newsletter sign-up is not switched on yet, so nothing was saved. It will be ready
              before the store opens — please try again then.
            }
          </p>
        </section>

        <div class="site-footer__columns">
          <nav class="site-footer__col" aria-labelledby="footer-shop">
            <h2 class="site-footer__heading label" id="footer-shop">Shop</h2>
            <ul>
              @for (item of navItems(); track item.id) {
                <li><a [routerLink]="item.path">{{ item.label }}</a></li>
              }
            </ul>
          </nav>

          <nav class="site-footer__col" aria-labelledby="footer-help">
            <h2 class="site-footer__heading label" id="footer-help">Help</h2>
            <ul>
              <li><a routerLink="/contact">Contact us</a></li>
              <li><a routerLink="/shipping">Delivery &amp; shipping</a></li>
              <li><a routerLink="/returns">Returns &amp; exchanges</a></li>
              <li><a routerLink="/account">Track your order</a></li>
            </ul>
          </nav>

          <nav class="site-footer__col" aria-labelledby="footer-about">
            <h2 class="site-footer__heading label" id="footer-about">About</h2>
            <ul>
              <li><a routerLink="/about">About Spencer Denim</a></li>
              <li><a routerLink="/privacy">Privacy policy</a></li>
              <li><a routerLink="/terms">Terms &amp; conditions</a></li>
            </ul>
          </nav>

          <section class="site-footer__col" aria-labelledby="footer-contact">
            <h2 class="site-footer__heading label" id="footer-contact">Contact</h2>
            <address class="site-footer__address">
              @for (line of contact.addressLines; track line) {
                <span>{{ line }}</span>
              }
              <a class="site-footer__contact-link" [href]="'mailto:' + contact.email">
                <sd-icon name="mail" size="sm" /> {{ contact.email }}
              </a>
              <a class="site-footer__contact-link" [href]="'tel:' + contact.phoneHref">
                <sd-icon name="phone" size="sm" /> {{ contact.phone }}
              </a>
            </address>
            <ul class="site-footer__hours t-small muted">
              @for (line of contact.openingHours; track line) {
                <li>{{ line }}</li>
              }
            </ul>
            <ul class="site-footer__social">
              @for (link of social; track link.url) {
                <li>
                  <a class="icon-btn" [href]="link.url" target="_blank" rel="noopener">
                    <sd-icon [name]="link.icon" />
                    <span class="sr-only">{{ link.label }}</span>
                  </a>
                </li>
              }
            </ul>
          </section>
        </div>

        <div class="site-footer__legal t-small muted">
          <p>&copy; {{ year }} Spencer Denim Industries (Pvt) Ltd. All rights reserved.</p>
          <p>Prices in Sri Lankan rupees. Delivery island-wide.</p>
        </div>
      </div>
    </footer>
  `,
  styles: `
    :host {
      display: block;
    }
    .site-footer {
      /* The bottom padding clears the floating WhatsApp button (56px + its offset), so the
         legal line is never hidden behind it at the foot of the page. */
      padding-block: var(--space-6) calc(var(--space-4) + 56px);
      background: var(--color-bg-alt);
      border-top: 1px solid var(--color-border);
    }
    .newsletter {
      display: grid;
      gap: var(--space-2);
      padding-bottom: var(--space-5);
      border-bottom: 1px solid var(--color-border);
    }
    .newsletter__title {
      font-size: var(--text-h3);
      font-weight: var(--weight-medium);
    }
    .newsletter__form {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      max-width: 34rem;
    }
    .newsletter__input {
      flex: 1 1 14rem;
      min-width: 0;
      min-height: 48px;
      padding: 0 var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-surface);
    }
    .newsletter__submit {
      flex: 0 0 auto;
    }
    .newsletter__status {
      min-height: 1.5em;
      max-width: 52ch;
      color: var(--color-text-muted);
    }

    .site-footer__columns {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: var(--space-4) var(--space-2);
      padding-block: var(--space-5);
    }
    .site-footer__heading {
      margin-bottom: var(--space-1);
      color: var(--color-text-muted);
    }
    .site-footer__col {
      min-width: 0;
    }
    /* Contact details are the longest strings in the footer; give them the full row on phones. */
    .site-footer__col:last-child {
      grid-column: 1 / -1;
    }
    .site-footer__col ul {
      list-style: none;
    }
    .site-footer__col a {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      font-size: var(--text-small);
      text-decoration: none;
    }
    .site-footer__col a:hover {
      text-decoration: underline;
      text-underline-offset: 4px;
    }
    .site-footer__col a,
    .site-footer__address span {
      max-width: 100%;
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .site-footer__address {
      display: grid;
      font-size: var(--text-small);
      font-style: normal;
    }
    .site-footer__contact-link {
      flex-wrap: wrap;
      gap: 0 var(--space-0-5);
    }
    .site-footer__hours {
      list-style: none;
      margin-top: var(--space-1);
    }
    .site-footer__social {
      display: flex;
      gap: var(--space-0-5);
      margin-top: var(--space-1);
      list-style: none;
    }
    .site-footer__legal {
      display: grid;
      gap: var(--space-0-5);
      padding-top: var(--space-3);
      border-top: 1px solid var(--color-border);
    }

    @media (min-width: 768px) {
      .newsletter {
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        align-items: end;
      }
      .newsletter__status {
        grid-column: 1 / -1;
      }
      .site-footer__columns {
        gap: var(--space-4);
      }
      .site-footer__col:last-child {
        grid-column: auto;
      }
    }
    @media (min-width: 1024px) {
      /* Four columns only when each is wide enough for a full email address on one line. */
      .site-footer__columns {
        grid-template-columns: repeat(4, minmax(0, 1fr));
      }
      .site-footer__legal {
        display: flex;
        justify-content: space-between;
        gap: var(--space-2);
      }
    }
  `,
})
export class SiteFooter {
  private readonly navigation = inject(NavigationService);
  private readonly config = inject(SITE_CONFIG);

  protected readonly navItems = this.navigation.items;
  protected readonly contact = this.config.contact;
  protected readonly social = this.config.social;
  protected readonly year = new Date().getFullYear();

  private readonly state = signal<NewsletterState>('idle');

  readonly newsletterState = this.state.asReadonly();

  protected onNewsletterSubmit(event: Event): void {
    event.preventDefault();
    this.state.set('coming-soon');
  }
}
