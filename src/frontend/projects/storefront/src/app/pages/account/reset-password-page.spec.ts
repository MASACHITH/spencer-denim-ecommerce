import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { PLATFORM_ID, RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Meta, Title } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ResetPasswordPage } from './reset-password-page';

/** The server's real success sentence, from `CustomerAuthEndpoints.ResetPasswordAsync`. */
const SERVER_REPLY =
  'Your password is set. Sign in with it, and any other browser that was signed in has been signed out.';

/** The server's one opaque refusal for a spent, expired or wrong link, from `InvalidLink`. */
const SERVER_REFUSAL =
  'This link has already been used, or it has expired. Ask for a new one and use the newest message.';

/**
 * Set a new password from a reset link (ACC-01, doc 11 P11 step C.9).
 *
 * **A router harness rather than a bare component**, because the two things worth proving here are both
 * about the address bar: the token is read out of the query string, and it is taken *out* of the address
 * bar afterwards so it is not left in history or in a screenshot. Neither is observable from a component
 * fixture with an input bound by hand.
 */
describe('ResetPasswordPage (ACC-01)', () => {
  let harness: RouterTestingHarness;
  let fixture: ComponentFixture<unknown>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    harness.detectChanges();
    await fixture.whenStable();
    harness.detectChanges();
  }

  async function open(
    url: string,
    platform: 'browser' | 'server' = 'browser',
  ): Promise<void> {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'account/reset-password', component: ResetPasswordPage },
          { path: 'account/sign-in', children: [] },
          { path: 'account/forgot-password', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create(url);
    fixture = harness.fixture;
    element = harness.routeNativeElement as HTMLElement;
    await settle();
  }

  function passwordBox(): HTMLInputElement {
    return element.querySelector('[data-testid="reset-password-password"]') as HTMLInputElement;
  }

  /** See `register-page.spec.ts` for why the render between keystrokes is not cosmetic. */
  function type(value: string): void {
    passwordBox().value = value;
    passwordBox().dispatchEvent(new Event('input'));
    harness.detectChanges();
  }

  function submit(): void {
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit', { cancelable: true }),
    );
  }

  function request(): TestRequest {
    return http.expectOne(
      (candidate) => candidate.url === '/api/account/reset-password' && candidate.method === 'POST',
    );
  }

  function statusRegion(): HTMLElement {
    return element.querySelector('[data-testid="reset-password-status"]') as HTMLElement;
  }

  function alertRegion(): HTMLElement {
    return element.querySelector('[data-testid="reset-password-alert"]') as HTMLElement;
  }

  function text(): string {
    return element.textContent ?? '';
  }

  afterEach(() => http?.verify());

  describe('the token, and where it must not stay', () => {
    /**
     * **The token is taken out of the address bar**, with `replaceUrl`, so the entry carrying it is not
     * left in history either. `CustomerTokenRequest` on the server is the other half of the same
     * decision: the token travels in the request *body* although it arrived in the page's URL, because a
     * token in a request URL lands in the API's access log and in any proxy's.
     */
    it('strips the token from the address bar on the first render', async () => {
      await open('/account/reset-password?token=a-single-use-token');

      expect(TestBed.inject(Router).url).toBe('/account/reset-password');
    });

    /** And keeps it, because the form is submitted after the address bar has been rewritten. */
    it('still has the token to spend after stripping it', async () => {
      await open('/account/reset-password?token=a-single-use-token');
      type('a long enough password');
      submit();

      expect(request().request.body).toEqual({
        token: 'a-single-use-token',
        password: 'a long enough password',
      });
    });

    it('leaves the address alone when there was no token to strip', async () => {
      await open('/account/reset-password');

      expect(TestBed.inject(Router).url).toBe('/account/reset-password');
    });

    /**
     * **A missing token is a page state, not an empty form.** Someone who opens the page with a
     * truncated link gets the explanation and a way to ask for a new one, rather than a form that would
     * refuse whatever they typed.
     */
    it('explains itself and offers a new link when there is no token', async () => {
      await open('/account/reset-password');

      expect(element.querySelector('[data-testid="reset-password-no-token"]')).not.toBeNull();
      expect(passwordBox()).toBeNull();
      expect(text()).toContain('Ask for a new link');
    });

    it('treats a blank token as no token', async () => {
      await open('/account/reset-password?token=');

      expect(element.querySelector('[data-testid="reset-password-no-token"]')).not.toBeNull();
    });
  });

  describe('what the server renders', () => {
    /**
     * The form, and **no request**: redeeming a one-time token during a page render would spend it on a
     * prefetch or a crawl. The live regions are in that markup from the first byte.
     */
    it('renders the form, posts nothing, and has both live regions already', async () => {
      await open('/account/reset-password?token=a-token', 'server');

      expect(element.querySelector('h1')?.textContent?.trim()).toBe('Choose a new password');
      expect(passwordBox()).not.toBeNull();
      expect(statusRegion().getAttribute('role')).toBe('status');
      expect(alertRegion().getAttribute('role')).toBe('alert');
      expect(http.match(() => true)).toEqual([]);
    });

    it('asks not to be indexed and not to be stored', async () => {
      await open('/account/reset-password?token=a-token');

      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
      expect(new Headers(response.headers ?? {}).get('Cache-Control')).toContain('no-store');
      expect(TestBed.inject(Title).getTitle()).toBe('Choose a new password | Colombo Wardrobe');
    });
  });

  describe('when the password is set', () => {
    /**
     * **No session is issued**, deliberately: the link was opened by whatever browser read the email, and
     * handing a session to a mailbox reader is the one thing a reset must not do. The server's own reply
     * already says to sign in and that every other browser has been signed out, so the page prints it and
     * offers the sign-in link.
     */
    it('shows the server’s sentence and offers the sign-in link instead of a form', async () => {
      await open('/account/reset-password?token=a-token');
      type('a long enough password');
      submit();
      request().flush({ message: SERVER_REPLY });
      await settle();

      expect(statusRegion().textContent).toBe(SERVER_REPLY);
      expect(element.querySelector('[data-testid="reset-password-sign-in"]')).not.toBeNull();
      expect(passwordBox()).toBeNull();
      expect(document.activeElement).toBe(statusRegion());
    });

    it('promises no email', async () => {
      await open('/account/reset-password?token=a-token');
      type('a long enough password');
      submit();
      request().flush({ message: SERVER_REPLY });
      await settle();

      expect(text()).not.toMatch(/check your (inbox|email)/i);
      expect(text()).not.toMatch(/email (you|your)/i);
    });
  });

  describe('when it is refused', () => {
    /**
     * **A refused password does not spend the token**, so the same link still works — which is why the
     * form has to stay on screen after a 400 on `password`. A page that replaced it with "ask for a new
     * link" would be telling the shopper to throw away a link that is still good.
     */
    it('keeps the form when only the password was refused', async () => {
      await open('/account/reset-password?token=a-token');
      type('short');
      submit();
      request().flush(
        {
          errors: {
            password: [
              'Use at least 10 characters, including an upper-case letter, a lower-case letter, a ' +
                'number and a symbol.',
            ],
          },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(passwordBox()).not.toBeNull();
      expect(passwordBox().getAttribute('aria-invalid')).toBe('true');
      expect(element.querySelector('#rp-password-error')?.textContent).toContain(
        'Use at least 10 characters',
      );
      expect(element.querySelector('[data-testid="reset-password-sign-in"]')).toBeNull();
    });

    /**
     * **One opaque refusal for a spent, expired or wrong link**, because saying "expired" rather than
     * "wrong" tells a holder the token was once real. The page prints the server's sentence and must not
     * improve on it.
     */
    it('shows the server’s one sentence for a link that cannot be used', async () => {
      await open('/account/reset-password?token=a-spent-token');
      type('a long enough password');
      submit();
      request().flush(
        { code: 'invalid_link', title: 'That link cannot be used', detail: SERVER_REFUSAL },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(alertRegion().textContent).toBe(SERVER_REFUSAL);
      expect(text()).not.toMatch(/\bexpired\b.*\bnot\b|wrong token|never existed/i);
      expect(document.activeElement).toBe(alertRegion());
    });

    it('writes the refusal into the region that was already on the page', async () => {
      await open('/account/reset-password?token=a-token');
      const before = alertRegion();

      type('short');
      submit();
      request().flush({ errors: { password: ['Too short.'] } }, { status: 400, statusText: 'x' });
      await settle();

      expect(alertRegion()).toBe(before);
      expect(before.textContent).toContain('Too short.');
    });

    it('states no password rule of its own before the server refuses one', async () => {
      await open('/account/reset-password?token=a-token');

      expect(text()).not.toMatch(/at least \d+ characters/i);
      expect(passwordBox().getAttribute('minlength')).toBeNull();
    });
  });

  describe('the button, and the checks axe did not make', () => {
    it('never carries the disabled attribute and refuses its own second press', async () => {
      await open('/account/reset-password?token=a-token');
      type('a long enough password');
      submit();
      submit();
      await settle();

      expect(element.querySelectorAll('[disabled]')).toHaveLength(0);
      expect(
        element.querySelector('[data-testid="reset-password-submit"]')?.getAttribute('aria-disabled'),
      ).toBe('true');
      expect(http.match((candidate) => candidate.url === '/api/account/reset-password')).toHaveLength(1);
    });

    it('has one h1, no placeholder, and a label for its box', async () => {
      await open('/account/reset-password?token=a-token');

      expect(element.querySelectorAll('h1')).toHaveLength(1);
      expect(element.querySelectorAll('h2, h3, h4, h5, h6')).toHaveLength(0);
      expect(passwordBox().getAttribute('placeholder')).toBeNull();
      expect(passwordBox().value).toBe('');
      expect(element.querySelector('label[for="rp-password"]')).not.toBeNull();
    });
  });
});
