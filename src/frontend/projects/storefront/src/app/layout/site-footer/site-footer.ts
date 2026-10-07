import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { BRAND, Icon } from '@spencer-denim/shared';

import { CONTENT_PAGE_KEYS, ContentPageLink } from '../../core/content/content-page.model';
import { ContentApi, newsletterErrorMessage } from '../../core/content/content-api';
import { ContentPageService } from '../../core/content/content-page.service';
import { NavigationService } from '../../core/navigation/navigation.service';
import { SITE_CONFIG } from '../../core/config/site-config';
import { SiteSettingsService } from '../../core/site-settings/site-settings.service';

/** What the newsletter form is showing (GLB-07). */
export type NewsletterState = 'idle' | 'sending' | 'done' | 'error';

/**
 * The storefront footer (GLB-07): shop links, help and policies, contact, social and a newsletter
 * field.
 *
 * The newsletter field is real. It used to tell a shopper on every page of the site that nothing
 * had been saved, which was true until HOME-10 built `POST /api/content/newsletter`; it now writes
 * the same consent row the homepage form does, recorded against `footer` rather than `homepage` so
 * the client can see where an address came from (doc 08 §3.6 `Source`).
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
            <h2 class="newsletter__title" id="newsletter-title">Join the {{ brand }} list</h2>
            <p class="t-small muted">New arrivals, restocks and sale previews. No more than twice a month.</p>
          </div>
          @if (newsletterState() === 'done') {
            <p class="newsletter__status t-small" role="status" data-testid="newsletter-status">
              {{ newsletterMessage() }}
            </p>
          } @else {
            <form class="newsletter__form" novalidate (submit)="onNewsletterSubmit($event)">
              <!-- A prefixed id, because the homepage carries a newsletter field of its own and
                   an id may appear once in a document. -->
              <label class="sr-only" for="footer-newsletter-email">Email address</label>
              <input
                class="newsletter__input"
                id="footer-newsletter-email"
                data-testid="footer-newsletter-email"
                type="email"
                name="email"
                autocomplete="email"
                inputmode="email"
                spellcheck="false"
                required
                placeholder="you@example.com"
                [value]="newsletterEmail()"
                [disabled]="newsletterState() === 'sending'"
                [attr.aria-invalid]="newsletterState() === 'error' ? 'true' : null"
                [attr.aria-describedby]="'footer-newsletter-status'"
                (input)="onNewsletterInput($event)"
              />
              <button
                class="btn btn--primary newsletter__submit"
                type="submit"
                [disabled]="newsletterState() === 'sending'"
              >
                {{ newsletterState() === 'sending' ? 'Signing up…' : 'Sign up' }}
              </button>
            </form>
            <!-- Always mounted so a screen reader is watching before there is anything to say. -->
            <p
              class="newsletter__status t-small"
              id="footer-newsletter-status"
              role="alert"
              data-testid="newsletter-status"
            >
              {{ newsletterState() === 'error' ? newsletterMessage() : '' }}
            </p>
          }
          <!--
            GLB-10's own TODO, answered: the consent wording links to the privacy policy **when the
            client has published one**, and says the same sentence without a link until then. An
            unconditional link would be a 404 beside a consent statement on every page of the shop,
            which is a worse answer to "where is your privacy policy" than no link at all.
          -->
          <p class="t-small muted">
            We record your IP address as proof of consent. Unsubscribe from any email.
            @if (privacyPage(); as page) {
              <a class="site-footer__inline-link" [routerLink]="'/' + page.slug">{{ page.title }}</a>
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

          <!--
            GLB-10, W2-11: the policy links come from the published-page index, not from this file.

            **A link appears only once the client has published the page it points at**, and that is the
            requirement rather than a nicety: all six system pages ship unpublished with empty bodies
            (doc 11 P13 — their copy is the client's, and Privacy and Terms need a PDPA review), so six
            hard-coded links would have been six 404s in the footer of every page of the shop on the day
            it opened. The columns fill in by themselves as the client publishes.

            It is the same argument ADM-22 made about the three placeholder social icons, and the same
            shape of answer: show what exists, show nothing when nothing does.

            The headings stay even when their list is empty, because 'helpPages()' and 'aboutPages()'
            never both empty the column — "Track your order" is in Help and the newsletter blurb is
            above — and a heading over nothing for one deployment is better than a column that appears
            and disappears as pages are published. The @if is on the list, so an empty 'ul' does not
            take its margin.
          -->
          <nav class="site-footer__col" aria-labelledby="footer-help">
            <h2 class="site-footer__heading label" id="footer-help">Help</h2>
            <ul>
              @for (page of helpPages(); track page.slug) {
                <li><a [routerLink]="'/' + page.slug">{{ page.title }}</a></li>
              }
              <li><a routerLink="/account">Track your order</a></li>
            </ul>
          </nav>

          @if (aboutPages().length > 0) {
            <nav class="site-footer__col" aria-labelledby="footer-about">
              <h2 class="site-footer__heading label" id="footer-about">About</h2>
              <ul>
                @for (page of aboutPages(); track page.slug) {
                  <li><a [routerLink]="'/' + page.slug">{{ page.title }}</a></li>
                }
              </ul>
            </nav>
          }

          <section class="site-footer__col" aria-labelledby="footer-contact">
            <h2 class="site-footer__heading label" id="footer-contact">Contact</h2>
            <!--
              GLB-07, ADM-22 part 2 (W2-11): the client's own details when they have supplied them, and
              the documented placeholder in 'SITE_CONFIG' only until then.

              **The placeholder is kept here and not on the Contact page**, and the asymmetry is
              deliberate. The footer is on every page and an empty contact column would read as a broken
              shop; the Contact page is where somebody goes *for* these details, and showing them an
              invented address there is the one place it could actually send a person to the wrong
              building. So the footer degrades to a documented placeholder (flagged in 'SITE_CONFIG' as
              the thing to replace with the client's real details, docs/04 §4 decision 8) and the Contact
              page shows nothing at all. Both are replaced by the same settings the moment they are set.
            -->
            <address class="site-footer__address">
              @for (line of contactAddressLines(); track line) {
                <span>{{ line }}</span>
              }
              @if (contactEmail(); as address) {
                <a class="site-footer__contact-link" [href]="'mailto:' + address">
                  <sd-icon name="mail" size="sm" /> {{ address }}
                </a>
              }
              @if (contactPhoneHref(); as href) {
                <a class="site-footer__contact-link" [href]="'tel:' + href">
                  <sd-icon name="phone" size="sm" /> {{ contactPhone() }}
                </a>
              }
            </address>
            <ul class="site-footer__hours t-small muted">
              @for (line of contactOpeningHours(); track line) {
                <li>{{ line }}</li>
              }
            </ul>
            <!-- GLB-07, ADM-22. Only the profiles the client has actually given us.

                 **The list is empty until then, and that is the correct state.** It used to hold three
                 icons pointing at facebook.com, instagram.com and tiktok.com — the networks' own front
                 pages, not this shop's profiles. A social icon on a live shop that does not go to the
                 shop is worse than no icon: a shopper who clicks it concludes the shop is abandoned, and
                 nothing on the page says otherwise. So an unset address hides its icon, and the whole
                 list disappears rather than leaving an empty row in the contact column.

                 The @if wraps the whole list rather than the @for wrapping nothing, because an empty
                 ul still takes its margin-top and would leave a gap under the opening hours. -->
            @if (social().length > 0) {
              <ul class="site-footer__social" data-testid="social-links">
                @for (link of social(); track link.network) {
                  <li>
                    <a class="icon-btn" [href]="link.url" target="_blank" rel="noopener">
                      <sd-icon [name]="link.icon" />
                      <span class="sr-only">{{ link.label }}</span>
                    </a>
                  </li>
                }
              </ul>
            }
          </section>
        </div>

        <div class="site-footer__legal t-small muted">
          <p>&copy; {{ year }} {{ brand }}. All rights reserved.</p>
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
    /* The link fills its row, so the tap target is the full column width rather than the few
       characters of the label ("Men" measured 28px wide). Social icons keep the icon-button
       shape and are already 44 x 44. */
    .site-footer__col a:not(.icon-btn) {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      min-width: var(--tap);
      max-width: 100%;
      font-size: var(--text-small);
      text-decoration: none;
      overflow-wrap: anywhere;
    }
    .site-footer__col a:not(.icon-btn):hover {
      text-decoration: underline;
      text-underline-offset: 4px;
    }
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
    /* Inside a sentence, so WCAG 2.2's 2.5.8 exemption for a target "in a block of text" applies and
       the 44 px floor does not — the same reasoning the content page's own inline links record. It is
       underlined rather than relying on colour alone (1.4.1). */
    .site-footer__inline-link {
      text-decoration: underline;
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
  /** The shop's name, from the one place it is written down (@spencer-denim/shared). */
  protected readonly brand = BRAND.name;

  private readonly navigation = inject(NavigationService);
  private readonly config = inject(SITE_CONFIG);

  private readonly contentPages = inject(ContentPageService);
  private readonly siteSettings = inject(SiteSettingsService);

  protected readonly navItems = this.navigation.items;

  /** The documented placeholder contact details, used only where a setting is unset (see the template). */
  private readonly placeholder = this.config.contact;

  /**
   * The published content pages, split into the two columns the footer has room for.
   *
   * **The split is on `systemKey`, never on the slug**, because a slug is editable (doc 11 P13 step 12)
   * and a client who renames `returns` to `returns-and-exchanges` must not have the link move column.
   * A custom page the client creates goes under Help, which is where a shopper looks for "size guide"
   * and "stores"; a seventh system page added later would need a line here, and
   * `site-footer.spec.ts` fails if one is unaccounted for.
   */
  private readonly publishedPages = this.contentPages.pages;

  protected readonly helpPages = computed(() =>
    this.pagesFor([CONTENT_PAGE_KEYS.contact, CONTENT_PAGE_KEYS.shipping, CONTENT_PAGE_KEYS.returns], true),
  );

  protected readonly aboutPages = computed(() =>
    this.pagesFor([CONTENT_PAGE_KEYS.about, CONTENT_PAGE_KEYS.privacy, CONTENT_PAGE_KEYS.terms], false),
  );

  /** The privacy policy, for the newsletter consent wording, or null while it is unpublished. */
  protected readonly privacyPage = computed(
    () => this.publishedPages().find((page) => page.systemKey === CONTENT_PAGE_KEYS.privacy) ?? null,
  );

  /** The client's own contact details, falling back to the placeholder only in the footer. */
  protected readonly contactAddressLines = computed(() => {
    const lines = this.siteSettings.contact().addressLines;

    return lines.length > 0 ? lines : this.placeholder.addressLines;
  });

  protected readonly contactOpeningHours = computed(() => {
    const hours = this.siteSettings.contact().openingHours;

    return hours.length > 0 ? hours : this.placeholder.openingHours;
  });

  protected readonly contactEmail = computed(
    () => this.siteSettings.contact().email ?? this.placeholder.email,
  );

  protected readonly contactPhone = computed(
    () => this.siteSettings.contact().phone ?? this.placeholder.phone,
  );

  protected readonly contactPhoneHref = computed(
    () => this.siteSettings.contact().phoneHref ?? this.placeholder.phoneHref,
  );

  /**
   * The shop's social profiles, from `ops.Settings` (ADM-22) rather than from `SITE_CONFIG`.
   *
   * A signal, not a constant, because the server renders this footer and the value arrives with the
   * page; and empty whenever the settings cannot be read, which is the same rendering as "the client has
   * not sent them" — so a failed read costs three icons rather than the page (W2-10's lesson).
   */
  protected readonly social = this.siteSettings.social;

  protected readonly year = new Date().getFullYear();

  private readonly api = inject(ContentApi);
  private readonly destroyRef = inject(DestroyRef);

  private readonly state = signal<NewsletterState>('idle');
  private readonly email = signal('');
  private readonly answer = signal('');

  readonly newsletterState = this.state.asReadonly();

  protected readonly newsletterEmail = this.email.asReadonly();
  protected readonly newsletterMessage = this.answer.asReadonly();

  /**
   * The published pages for one column, in the order this file lists their keys.
   *
   * @param keys The system keys this column owns, in display order.
   * @param includeCustom Whether a page with no system key belongs here. Exactly one column may say
   *   yes, or a custom page would appear twice.
   */
  private pagesFor(keys: readonly string[], includeCustom: boolean): readonly ContentPageLink[] {
    const published = this.publishedPages();

    const system = keys
      .map((key) => published.find((page) => page.systemKey === key))
      .filter((page): page is ContentPageLink => page !== undefined);

    if (!includeCustom) {
      return system;
    }

    // `[...]` then `sort`, not `toSorted`: this project's TypeScript lib target predates ES2023, and
    // `sort` mutates, so the copy is what keeps it off the signal's own array.
    const custom = [...published.filter((page) => page.systemKey === null)].sort(
      (left: ContentPageLink, right: ContentPageLink) =>
        left.title.localeCompare(right.title, 'en-GB'),
    );

    return [...system, ...custom];
  }

  protected onNewsletterInput(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);

    if (this.state() === 'error') {
      this.state.set('idle');
      this.answer.set('');
    }
  }

  protected onNewsletterSubmit(event: Event): void {
    event.preventDefault();

    if (this.state() === 'sending') {
      return;
    }

    const email = this.email().trim();
    if (email.length === 0) {
      this.answer.set('Enter your email address to sign up.');
      this.state.set('error');
      return;
    }

    this.state.set('sending');
    this.answer.set('');

    this.api
      .subscribe(email, 'footer')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        // The server's own sentence: it is the same whether the address was new or already on the
        // list, and only the server knows which.
        next: (response) => {
          this.answer.set(
            response?.message?.trim() || 'Thank you. Your email address is on the list.',
          );
          this.state.set('done');
        },
        error: (failure: unknown) => {
          this.answer.set(newsletterErrorMessage(failure));
          this.state.set('error');
        },
      });
  }
}
