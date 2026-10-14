import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { PLATFORM_ID, RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Meta, Title } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';

import { ForgotPasswordPage } from './forgot-password-page';

/**
 * The server's real sentence, from `CustomerAuthEndpoints.ForgotPasswordReply`.
 *
 * It is the real one because the tests below are about the page printing the server's wording rather
 * than its own, and a made-up fixture reply would let a page that wrote "Check your inbox" pass.
 */
const SERVER_REPLY =
  'If that email address can be used to sign in, we have queued a reset link to it. If nothing ' +
  'arrives, please get in touch with us.';

/**
 * Ask for a password-reset link (ACC-01, doc 11 P11 step C.8).
 *
 * One field and one sentence. The whole substance of the page is in two absences: it must not say
 * whether the address is known (doc 08 §3.1 DB10) and it must not promise an email (doc 08 §3.8's
 * transport is unconfigured).
 */
describe('ForgotPasswordPage (ACC-01)', () => {
  let fixture: ComponentFixture<ForgotPasswordPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function create(platform: 'browser' | 'server' = 'browser'): void {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'account/sign-in', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ForgotPasswordPage);
    element = fixture.nativeElement as HTMLElement;
  }

  function emailBox(): HTMLInputElement {
    return element.querySelector('[data-testid="forgot-password-email"]') as HTMLInputElement;
  }

  /** See `register-page.spec.ts`: the render between keystrokes is what a browser really does. */
  function type(value: string): void {
    emailBox().value = value;
    emailBox().dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function submit(): void {
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit', { cancelable: true }),
    );
  }

  function request(): TestRequest {
    return http.expectOne(
      (candidate) => candidate.url === '/api/account/forgot-password' && candidate.method === 'POST',
    );
  }

  function statusRegion(): HTMLElement {
    return element.querySelector('[data-testid="forgot-password-status"]') as HTMLElement;
  }

  function alertRegion(): HTMLElement {
    return element.querySelector('[data-testid="forgot-password-alert"]') as HTMLElement;
  }

  beforeEach(() => create());

  afterEach(() => http?.verify());

  it('renders the whole form on the server and asks the shop nothing', async () => {
    create('server');
    await settle();

    expect(element.querySelector('h1')?.textContent?.trim()).toBe('Reset your password');
    expect(emailBox()).not.toBeNull();
    expect(statusRegion().getAttribute('role')).toBe('status');
    expect(alertRegion().getAttribute('role')).toBe('alert');
    expect(http.match(() => true)).toEqual([]);
  });

  it('asks not to be indexed and not to be stored', async () => {
    await settle();

    expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
    expect(new Headers(response.headers ?? {}).get('Cache-Control')).toContain('no-store');
    expect(TestBed.inject(Title).getTitle()).toBe('Reset your password | Colombo Wardrobe');
  });

  it('sends the one field it has', async () => {
    await settle();
    type('nimal@example.invalid');
    submit();

    expect(request().request.body).toEqual({ email: 'nimal@example.invalid' });
  });

  /**
   * **The server deliberately accepts a badly-shaped address** rather than refusing it, because
   * "that is not a valid address" tells a caller which strings are worth trying. So this page does no
   * shape validation of its own either: `novalidate` on the form is what stops the browser doing it on
   * the page's behalf, and `type="email"` stays as a keyboard hint.
   */
  it('validates no shape of its own, and lets the browser validate none either', async () => {
    await settle();

    expect(element.querySelector('form')?.hasAttribute('novalidate')).toBe(true);
    expect(emailBox().type).toBe('email');

    type('not an address at all');
    submit();

    expect(request().request.body).toEqual({ email: 'not an address at all' });
  });

  describe('the one sentence every address gets (doc 08 §3.1 DB10)', () => {
    it('shows the server’s sentence, word for word', async () => {
      await settle();
      type('nimal@example.invalid');
      submit();
      request().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      expect(statusRegion().textContent).toBe(SERVER_REPLY);
      expect(document.activeElement).toBe(statusRegion());
    });

    it('promises no email and never says whether the address is known', async () => {
      await settle();
      type('nobody@example.invalid');
      submit();
      request().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      const text = element.textContent ?? '';
      expect(text).not.toMatch(/check your (inbox|email)/i);
      expect(text).not.toMatch(/\bwe (have |)sent\b/i);
      expect(text).not.toMatch(/unknown|no account|not registered|does not exist/i);
    });

    /**
     * Emptied, so a second press cannot ask again for the same address and spend one of the five reset
     * links the server allows per /64 per ten minutes.
     */
    it('empties the box on success', async () => {
      await settle();
      type('nimal@example.invalid');
      submit();
      request().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      expect(emailBox().value).toBe('');
    });
  });

  describe('when it is refused', () => {
    it('writes the refusal into the region that was already on the page, and takes focus there', async () => {
      await settle();
      const before = alertRegion();

      submit();
      request().flush({ errors: { email: ['Enter your email address.'] } }, {
        status: 400,
        statusText: 'Bad Request',
      });
      await settle();

      expect(alertRegion()).toBe(before);
      expect(before.textContent).toContain('Enter your email address.');
      expect(element.querySelector('#fp-email-error')?.textContent).toContain(
        'Enter your email address.',
      );
      expect(document.activeElement).toBe(before);
    });

    it('shows the server’s sentence when too many links have been asked for', async () => {
      await settle();
      type('nimal@example.invalid');
      submit();
      request().flush(
        {
          code: 'rate_limited',
          detail: 'Too many reset links have been asked for from here recently. Please try again shortly.',
        },
        { status: 429, statusText: 'Too Many Requests' },
      );
      await settle();

      expect(alertRegion().textContent).toContain('Too many reset links have been asked for');
      // Still on screen, because the address is still worth keeping.
      expect(emailBox().value).toBe('nimal@example.invalid');
    });
  });

  describe('the button, and the checks axe did not make', () => {
    it('never carries the disabled attribute and refuses its own second press', async () => {
      await settle();
      type('nimal@example.invalid');
      submit();
      submit();
      await settle();

      expect(element.querySelectorAll('[disabled]')).toHaveLength(0);
      expect(
        element.querySelector('[data-testid="forgot-password-submit"]')?.getAttribute('aria-disabled'),
      ).toBe('true');
      expect(http.match((candidate) => candidate.url === '/api/account/forgot-password')).toHaveLength(
        1,
      );
    });

    it('has one h1, no placeholder, and a label for its box', async () => {
      await settle();

      expect(element.querySelectorAll('h1')).toHaveLength(1);
      expect(element.querySelectorAll('h2, h3, h4, h5, h6')).toHaveLength(0);
      expect(emailBox().getAttribute('placeholder')).toBeNull();
      expect(emailBox().value).toBe('');
      expect(element.querySelector('label[for="fp-email"]')).not.toBeNull();
    });
  });
});
