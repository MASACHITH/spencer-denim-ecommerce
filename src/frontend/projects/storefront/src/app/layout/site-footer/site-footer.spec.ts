import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SiteFooter } from './site-footer';
import { ContentPageLinkDto } from '../../core/content/content-page.model';
import { flushContentPages } from '../../core/content/content-pages.testing';
import { SiteSettingsDto } from '../../core/site-settings/site-settings.model';

/**
 * The six system pages, as the API lists them once a client has published all of them (GLB-10).
 *
 * The default state is the opposite — nothing published, so no policy links at all — and `beforeEach`
 * answers with that, because it is what a visitor meets on the day the shop opens. The tests that are
 * *about* the links use this.
 */
const PUBLISHED_PAGES: readonly ContentPageLinkDto[] = [
  { slug: 'about', title: 'About us', systemKey: 'about', updatedAtUtc: null },
  { slug: 'contact', title: 'Contact us', systemKey: 'contact', updatedAtUtc: null },
  { slug: 'returns', title: 'Returns and exchanges', systemKey: 'returns', updatedAtUtc: null },
  { slug: 'shipping', title: 'Delivery and shipping', systemKey: 'shipping', updatedAtUtc: null },
  { slug: 'privacy', title: 'Privacy policy', systemKey: 'privacy', updatedAtUtc: null },
  { slug: 'terms', title: 'Terms and conditions', systemKey: 'terms', updatedAtUtc: null },
];

describe('SiteFooter (GLB-07)', () => {
  let fixture: ComponentFixture<SiteFooter>;
  let element: HTMLElement;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SiteFooter],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    fixture = TestBed.createComponent(SiteFooter);
    element = fixture.nativeElement as HTMLElement;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();

    // The footer lists the departments (GLB-05), so creating it fetches the navigation tree. It
    // is answered here so the newsletter assertions below are about the newsletter.
    http.match('/api/catalog/departments').forEach((request) => request.flush([]));

    // ADM-22: the social icons come from ops.Settings now. Answered with the honest default — the
    // client has supplied nothing — so every assertion below is about the state a visitor meets
    // today. The tests that care about a configured profile answer it themselves via `settings`.
    http.match('/api/site/settings').forEach((request) =>
      request.flush({ logo: null, whatsAppNumber: null, social: [], contact: null }),
    );

    // GLB-10: the policy links come from the published-page index now. Answered with the honest
    // default — the client has published nothing, because all six system pages ship unpublished with
    // empty bodies (doc 11 P13) — so every assertion below is about the state a visitor meets today.
    flushContentPages(http);
    fixture.detectChanges();
  });

  /**
   * Rebuilds the footer with a settings payload of the test's choosing.
   *
   * A second fixture rather than a second `flush`, because the request in `beforeEach` has already been
   * answered — and the service is `providedIn: 'root'`, so its signal is settled for this TestBed.
   */
  function withSettings(
    settings: SiteSettingsDto,
    pages: readonly ContentPageLinkDto[] = [],
  ): HTMLElement {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [SiteFooter],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    const rebuilt = TestBed.createComponent(SiteFooter);
    const controller = TestBed.inject(HttpTestingController);
    rebuilt.detectChanges();

    controller.match('/api/catalog/departments').forEach((request) => request.flush([]));
    controller.match('/api/site/settings').forEach((request) => request.flush(settings));
    flushContentPages(controller, pages);
    rebuilt.detectChanges();

    // The rebuilt fixture owns the outstanding requests now, and `afterEach` verifies the one from
    // `beforeEach`. Pointing `http` at the new controller is what keeps that verification honest —
    // without it the original controller is never checked again and a stray request on the rebuilt
    // one is never checked at all.
    http = controller;

    return rebuilt.nativeElement as HTMLElement;
  }

  afterEach(() => http.verify());

  function field(): HTMLInputElement {
    return element.querySelector('[data-testid="footer-newsletter-email"]') as HTMLInputElement;
  }

  function type(value: string): void {
    const input = field();
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function submit(): void {
    element
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
  }

  function status(): string {
    return element.querySelector('[data-testid="newsletter-status"]')?.textContent?.trim() ?? '';
  }

  function headings(from: HTMLElement = element): (string | undefined)[] {
    return Array.from(from.querySelectorAll('.site-footer__heading')).map((heading) =>
      heading.textContent?.trim(),
    );
  }

  it('renders one contentinfo landmark with named columns', () => {
    expect(element.querySelectorAll('footer')).toHaveLength(1);

    // **Two columns, not four, on a shop nobody has set up yet (W2-11, GLB-10, ADM-22 part 2).** The
    // About column holds only content pages and the Contact column only settings, so each is omitted
    // entirely rather than drawn as a heading over nothing — a "Contact" heading above empty space used
    // to be filled with an invented address, which was Major 4. Help keeps its heading either way,
    // because "Track your order" is in it.
    expect(headings()).toEqual(['Shop', 'Help']);
  });

  it('draws the About column once there is a page in it', () => {
    const published = withSettings(
      { logo: null, whatsAppNumber: null, social: [], contact: null },
      PUBLISHED_PAGES,
    );

    expect(headings(published)).toEqual(['Shop', 'Help', 'About']);
  });

  function hrefs(from: HTMLElement = element): string[] {
    return Array.from(from.querySelectorAll<HTMLAnchorElement>('a')).map(
      (link) => link.getAttribute('href') ?? '',
    );
  }

  it('links to the five shop destinations', () => {
    expect(hrefs()).toEqual(
      expect.arrayContaining(['/women', '/men', '/kids', '/new-arrivals', '/sale']),
    );
  });

  /**
   * GLB-10, W2-11, and the half of it that is the requirement rather than the nicety.
   *
   * All six system pages ship **unpublished with empty bodies** — doc 11 P13: their copy is the
   * client's, and Privacy and Terms need a PDPA review. Six hard-coded footer links would therefore
   * have been six 404s on every page of the shop on the day it opened, which is the same defect
   * ADM-22 removed when it took out three social icons pointing at the networks' own front pages.
   *
   * So the test is phrased as the absence: no policy link until there is a policy page.
   */
  it('links to no policy page while the client has published none', () => {
    const links = hrefs();

    for (const path of ['/about', '/contact', '/returns', '/shipping', '/privacy', '/terms']) {
      expect(links, `${path} is linked although nothing is published there`).not.toContain(path);
    }
  });

  it('links to each policy page once it is published, with the title the client gave it', () => {
    const published = withSettings(
      { logo: null, whatsAppNumber: null, social: [], contact: null },
      PUBLISHED_PAGES,
    );

    expect(hrefs(published)).toEqual(
      expect.arrayContaining(['/about', '/contact', '/returns', '/shipping', '/privacy', '/terms']),
    );

    // The client's own title, not a constant in this repository: they may rename "Returns and
    // exchanges" to "Our returns promise", and the footer has to say what the page says.
    expect(published.textContent).toContain('Returns and exchanges');
    expect(published.textContent).toContain('Terms and conditions');
  });

  /**
   * A renamed slug follows the link, which is the whole reason the footer reads an index rather than
   * holding six paths. Doc 11 P13 step 12 lists the slug among the fields staff may edit.
   */
  it('follows a renamed slug rather than linking the seeded one', () => {
    const renamed = withSettings({ logo: null, whatsAppNumber: null, social: [], contact: null }, [
      { slug: 'delivery-and-returns', title: 'Delivery and returns', systemKey: 'returns', updatedAtUtc: null },
    ]);

    expect(hrefs(renamed)).toContain('/delivery-and-returns');
    expect(hrefs(renamed)).not.toContain('/returns');
  });

  /**
   * A custom page the client creates appears under Help, which is where a shopper looks for "size
   * guide" or "stores". Exactly one column takes them, or it would be listed twice.
   */
  it('lists a custom page once, under Help', () => {
    const custom = withSettings({ logo: null, whatsAppNumber: null, social: [], contact: null }, [
      { slug: 'size-guide', title: 'Size guide', systemKey: null, updatedAtUtc: null },
    ]);

    expect(hrefs(custom).filter((href) => href === '/size-guide')).toHaveLength(1);
  });

  /**
   * GLB-10's own TODO in this file, answered: the newsletter consent wording links to the privacy
   * policy when there is one, and says the same sentence without a link until then. An unconditional
   * link would be a 404 beside a consent statement on every page of the shop.
   */
  it('links the consent wording to the privacy policy only once it is published', () => {
    expect(hrefs()).not.toContain('/privacy');

    const published = withSettings(
      { logo: null, whatsAppNumber: null, social: [], contact: null },
      PUBLISHED_PAGES,
    );

    expect(
      published.querySelector<HTMLAnchorElement>('.site-footer__inline-link')?.getAttribute('href'),
    ).toBe('/privacy');
  });

  /**
   * ADM-22 part 2 (W2-11): the client's own contact details, from `ops.Settings`.
   *
   * There is no placeholder behind them any more. See the three tests below for the state this
   * replaced, which is the state of a fresh shop.
   */
  it('shows the contact details the client has set', () => {
    const configured = withSettings({
      logo: null,
      whatsAppNumber: null,
      social: [],
      contact: {
        phone: '+94 11 234 5678',
        email: 'hello@example.com',
        addressLines: ['Spencer Denim Studio', 'Colombo 03'],
        openingHours: ['Monday to Friday, 9am to 6pm'],
        mapEmbedUrl: null,
      },
    });

    expect(configured.textContent).toContain('Spencer Denim Studio');
    expect(configured.textContent).toContain('Monday to Friday, 9am to 6pm');
    expect(hrefs(configured)).toContain('tel:+94112345678');
    expect(hrefs(configured)).toContain('mailto:hello@example.com');
  });

  /**
   * **A fresh shop publishes no contact details at all, and this is the test that says so.**
   *
   * Reported by the W2-11 tester as Major 4. With the five `Contact.*` settings unset — which is a
   * freshly installed shop — this footer rendered, on every page:
   *
   *     Colombo Wardrobe / Colombo / Sri Lanka · hello@colombowardrobecompany.com
   *     · +94 77 000 0000 · Monday to Friday, 9am – 6pm / Saturday, 9am – 1pm
   *
   * None of it is the client's. A shopper emails an address nobody reads, rings a number that is not
   * the shop's, and the shop never learns they tried; the address could send somebody to the wrong
   * part of Colombo. The fallbacks were added by this branch and the component's own comment argued
   * against them. ADM-22 had already made the same call about three placeholder social links.
   *
   * The assertions name the exact strings rather than checking "is empty", because what must never come
   * back is these values specifically — an assertion on emptiness would pass again the moment somebody
   * introduced a *different* placeholder.
   */
  it('publishes no invented contact details while the settings are unset', () => {
    const text = element.textContent ?? '';

    expect(text).not.toContain('hello@');
    expect(text).not.toContain('+94 77 000 0000');
    expect(text).not.toContain('Monday to Friday, 9am – 6pm');
    expect(text).not.toContain('Saturday, 9am – 1pm');

    // Not `'Sri Lanka'`: the legal line says "Prices in Sri Lankan rupees", which is the shop's own
    // copy and has nothing to do with the placeholder address. Asserting on it matched that line and
    // would have been a test failing for the wrong reason.
    expect(element.querySelector('.site-footer__address')).toBeNull();
    expect(hrefs(element).filter((href) => href.startsWith('mailto:'))).toHaveLength(0);
    expect(hrefs(element).filter((href) => href.startsWith('tel:'))).toHaveLength(0);
  });

  /**
   * And the heading goes with them. A "Contact" heading over empty space is its own small lie, and an
   * `<address>` element with nothing in it is an accessibility defect rather than a neutral blank.
   */
  it('drops the whole contact column when there is nothing to put in it', () => {
    expect(element.querySelector('#footer-contact')).toBeNull();
    expect(element.querySelector('.site-footer__address')).toBeNull();
    expect(element.querySelector('.site-footer__hours')).toBeNull();
  });

  /**
   * The column is shared with the social links, so a shop that has supplied only an Instagram address
   * still gets it — which is why the guard is on "anything in this column" rather than on the contact
   * details alone.
   */
  it('keeps the contact column for a shop that has only social addresses', () => {
    const configured = withSettings({
      logo: null,
      whatsAppNumber: null,
      social: [{ network: 'instagram', url: 'https://www.instagram.com/colombowardrobe' }],
    });

    expect(configured.querySelector('#footer-contact')).not.toBeNull();
    expect(configured.querySelector('.site-footer__address')).toBeNull();
    expect(configured.querySelectorAll('.site-footer__social a')).toHaveLength(1);
  });

  /**
   * One field at a time, because that is how a client fills a settings form in: a telephone number
   * entered on Monday must not wait for the address to appear on Friday.
   */
  it('shows a single supplied detail on its own', () => {
    const configured = withSettings({
      logo: null,
      whatsAppNumber: null,
      social: [],
      contact: {
        phone: '+94 11 234 5678',
        email: null,
        addressLines: [],
        openingHours: [],
        mapEmbedUrl: null,
      },
    });

    expect(hrefs(configured)).toContain('tel:+94112345678');
    expect(hrefs(configured).filter((href) => href.startsWith('mailto:'))).toHaveLength(0);
    expect(configured.querySelector('.site-footer__hours')).toBeNull();
  });

  /**
   * ADM-22. The footer used to carry three icons pointing at facebook.com, instagram.com and
   * tiktok.com — the networks' own front pages rather than this shop's profiles. A dead social icon on
   * a live shop is worse than no icon, so an address the client has not supplied shows nothing.
   *
   * The list element itself has to be absent rather than empty: an empty `<ul>` keeps its `margin-top`
   * and leaves a gap under the opening hours.
   */
  it('shows no social icons at all until the client supplies an address', () => {
    expect(element.querySelectorAll('.site-footer__social a')).toHaveLength(0);
    expect(element.querySelector('[data-testid="social-links"]')).toBeNull();
  });

  it('gives every supplied social icon a text name and opens it safely', () => {
    const configured = withSettings({
      logo: null,
      whatsAppNumber: null,
      social: [
        { network: 'facebook', url: 'https://www.facebook.com/colombowardrobe' },
        { network: 'instagram', url: 'https://www.instagram.com/colombowardrobe' },
      ],
    });

    const social = Array.from(configured.querySelectorAll<HTMLAnchorElement>('.site-footer__social a'));

    expect(social).toHaveLength(2);
    social.forEach((link) => {
      expect(link.querySelector('.sr-only')?.textContent?.trim().length).toBeGreaterThan(0);
      expect(link.getAttribute('rel')).toContain('noopener');
      expect(link.getAttribute('href')).toMatch(/^https:\/\/www\.(facebook|instagram)\.com\//);
    });
  });

  /**
   * The dangerous half of the same rule, and the reason the check is repeated on this side of the wire:
   * the API refuses these on the way in, but a row can predate the rule, and an `href` is what a browser
   * actually follows. `javascript:` must never reach one.
   */
  it('drops a social address a browser should not follow, rather than linking it', () => {
    const configured = withSettings({
      logo: null,
      whatsAppNumber: null,
      social: [
        { network: 'facebook', url: 'javascript:alert(1)' },
        { network: 'instagram', url: '/women/jeans' },
        { network: 'tiktok', url: 'https://www.tiktok.com/@colombowardrobe' },
      ],
    });

    const hrefs = Array.from(
      configured.querySelectorAll<HTMLAnchorElement>('.site-footer__social a'),
    ).map((link) => link.getAttribute('href'));

    expect(hrefs).toEqual(['https://www.tiktok.com/@colombowardrobe']);
  });

  it('says nothing until the newsletter form is used', () => {
    expect(status()).toBe('');
    expect(fixture.componentInstance.newsletterState()).toBe('idle');
  });

  /**
   * An `id` is document-unique and the homepage carries a newsletter field of its own, so the
   * footer's had to stop being `newsletter-email`: with both on screen the footer's label pointed
   * at the homepage's input and this field had no accessible name at all.
   */
  it('has an id of its own, and a label that points at it', () => {
    const label = element.querySelector<HTMLLabelElement>('label[for]')!;

    expect(field().id).toBe('footer-newsletter-email');
    expect(label.getAttribute('for')).toBe('footer-newsletter-email');
  });

  /**
   * GLB-07 is finished. It used to tell a shopper on every page of the site that nothing had been
   * saved, which was true only until HOME-10 built the endpoint.
   */
  it('really subscribes, recorded against the footer rather than the homepage', () => {
    type('shopper@example.com');
    submit();

    const request = http.expectOne('/api/content/newsletter');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'shopper@example.com', source: 'footer' });

    request.flush({ message: 'Thank you. Your email address is on the list.' });
    fixture.detectChanges();

    expect(status()).toContain('Thank you');
    expect(element.querySelector('[data-testid="footer-newsletter-email"]')).toBeNull();
  });

  it('never claims success on its own: a failure says so and keeps the form', () => {
    type('shopper@example.com');
    submit();

    http
      .expectOne('/api/content/newsletter')
      .flush({ title: 'Nope' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(status()).toContain('We could not save your email address');
    expect(status().toLowerCase()).not.toContain('thank you');
    expect(field()).not.toBeNull();
    expect(field().getAttribute('aria-invalid')).toBe('true');
  });

  it('shows the message the API wrote for an address it refuses', () => {
    type('not-an-address');
    submit();

    http.expectOne('/api/content/newsletter').flush(
      {
        title: 'That email address could not be read.',
        errors: { email: ['Enter an email address, for example name@example.com.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(status()).toContain('Enter an email address');
  });

  it('does not send an empty box to the server', () => {
    submit();

    http.expectNone('/api/content/newsletter');
    expect(status()).toContain('Enter your email address to sign up.');
  });

  it('tells a screen reader about the failure, and only once there is one', () => {
    const live = element.querySelector('[data-testid="newsletter-status"]')!;

    expect(live.getAttribute('role')).toBe('alert');
    expect(live.textContent?.trim()).toBe('');
  });
});
