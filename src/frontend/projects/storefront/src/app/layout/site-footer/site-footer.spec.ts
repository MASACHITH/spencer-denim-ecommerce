import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SiteFooter } from './site-footer';

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
  });

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

  it('gives every social icon a text name and opens it safely', () => {
    const social = Array.from(element.querySelectorAll<HTMLAnchorElement>('.site-footer__social a'));

    expect(social.length).toBeGreaterThan(0);
    social.forEach((link) => {
      expect(link.querySelector('.sr-only')?.textContent?.trim().length).toBeGreaterThan(0);
      expect(link.getAttribute('rel')).toContain('noopener');
    });
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
