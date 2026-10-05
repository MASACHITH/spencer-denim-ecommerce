import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SiteFooter } from './site-footer';
import { SiteSettingsDto } from '../../core/site-settings/site-settings.model';

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
      request.flush({ logo: null, whatsAppNumber: null, social: [] }),
    );
  });

  /**
   * Rebuilds the footer with a settings payload of the test's choosing.
   *
   * A second fixture rather than a second `flush`, because the request in `beforeEach` has already been
   * answered — and the service is `providedIn: 'root'`, so its signal is settled for this TestBed.
   */
  function withSettings(settings: SiteSettingsDto): HTMLElement {
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
    rebuilt.detectChanges();

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

  it('renders one contentinfo landmark with named columns', () => {
    expect(element.querySelectorAll('footer')).toHaveLength(1);

    const headings = Array.from(element.querySelectorAll('.site-footer__heading')).map((heading) =>
      heading.textContent?.trim(),
    );
    expect(headings).toEqual(['Shop', 'Help', 'About', 'Contact']);
  });

  it('links to the five shop destinations and to the policy pages', () => {
    const hrefs = Array.from(element.querySelectorAll<HTMLAnchorElement>('a')).map(
      (link) => link.getAttribute('href') ?? '',
    );

    expect(hrefs).toEqual(expect.arrayContaining(['/women', '/men', '/kids', '/new-arrivals', '/sale']));
    expect(hrefs).toEqual(expect.arrayContaining(['/returns', '/shipping', '/privacy', '/terms']));
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
