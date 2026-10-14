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

import { SignInPage } from './sign-in-page';
import { CustomerSessionDto } from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';
import { CartApi } from '../../core/cart/cart-api';

const CUSTOMER: CustomerSessionDto = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: 'Perera',
  emailConfirmed: false,
};

/**
 * Sign in (ACC-01, doc 11 P11 step B).
 *
 * Three things here are not ordinary form tests and are the reason this file is long:
 *
 * - **the page re-renders on submit**, so the refusal must land in a region that already existed;
 * - **the refusal must not say whether the address is known** (doc 08 §3.1 DB10), which is a test about
 *   what is *absent* from the page;
 * - **the bag is re-read on success**, because the server merged the guest bag in the same request and
 *   the header's badge is stale by exactly the lines that moved.
 */
describe('SignInPage (ACC-01)', () => {
  let fixture: ComponentFixture<SignInPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function create(options: { platform?: 'browser' | 'server'; returnTo?: string } = {}): void {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', children: [] },
          { path: 'account/orders', children: [] },
          { path: 'account/register', children: [] },
          { path: 'account/forgot-password', children: [] },
          { path: 'anywhere', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        ...(options.platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);

    // The session read is single-flight and settled before the page is built, so the page's own
    // `load()` costs no request and no test has to flush one it does not care about.
    TestBed.inject(AccountStore).accept(null);

    fixture = TestBed.createComponent(SignInPage);

    if (options.returnTo !== undefined) {
      fixture.componentRef.setInput('returnTo', options.returnTo);
    }

    element = fixture.nativeElement as HTMLElement;
  }

  function field(testId: string): HTMLInputElement {
    return element.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
  }

  /**
   * Types into a box the way a browser does, **change detection included** — see the same helper in
   * `register-page.spec.ts` for why that is not cosmetic: without a render between keystrokes, a bound
   * signal that returns to its old value is a binding Angular never writes to the DOM.
   */
  function type(testId: string, value: string): void {
    const input = field(testId);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function submit(): void {
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit', { cancelable: true }),
    );
  }

  function signInRequest(): TestRequest {
    return http.expectOne(
      (request) => request.url === '/api/account/login' && request.method === 'POST',
    );
  }

  function alertRegion(): HTMLElement {
    return element.querySelector('[data-testid="sign-in-alert"]') as HTMLElement;
  }

  function text(): string {
    return element.textContent ?? '';
  }

  beforeEach(() => create());

  afterEach(() => http?.verify());

  describe('what the server renders', () => {
    /**
     * **All of it**, because none of it is personal until something is typed. A sign-in form that waited
     * for JavaScript would be a form a shopper on a slow connection stares at.
     */
    it('renders the whole form on the server and asks the shop nothing', async () => {
      create({ platform: 'server' });
      await settle();

      expect(element.querySelector('h1')?.textContent?.trim()).toBe('Sign in');
      expect(field('sign-in-email')).not.toBeNull();
      expect(field('sign-in-password')).not.toBeNull();
      expect(element.querySelector('[data-testid="sign-in-submit"]')).not.toBeNull();
      expect(http.match(() => true)).toEqual([]);
    });

    it('renders the live region before there is anything to announce', async () => {
      create({ platform: 'server' });
      await settle();

      expect(alertRegion()).not.toBeNull();
      expect(alertRegion().getAttribute('role')).toBe('alert');
      expect(alertRegion().textContent).toBe('');
    });

    /** A sign-in form in a search result is a phishing surface and a wasted crawl. */
    it('asks not to be indexed and not to be stored', async () => {
      await settle();

      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');

      const headers = new Headers(response.headers ?? {});
      expect(headers.get('Cache-Control')).toContain('no-store');
      expect(headers.get('Vary')).toBe('Cookie');
    });

    it('clears the robots tag on the way out', async () => {
      await settle();
      fixture.destroy();

      expect(TestBed.inject(Meta).getTag('name="robots"')).toBeNull();
    });

    it('titles itself for the shop the customer is in', async () => {
      await settle();

      expect(TestBed.inject(Title).getTitle()).toBe('Sign in | Colombo Wardrobe');
    });
  });

  describe('signing in', () => {
    it('sends what was typed and nothing else', async () => {
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();

      expect(signInRequest().request.body).toEqual({
        email: 'nimal@example.invalid',
        password: 'a real password',
      });
    });

    /**
     * Doc 11 P11 step B.6: the server merges the guest bag into the account bag **in this request**. The
     * header's badge is therefore stale by exactly the lines that moved, and a shopper who signed in
     * from the bag page would see the old count until they navigated.
     */
    it('re-reads the bag, because the server merged it during the sign-in', async () => {
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();
      signInRequest().flush(CUSTOMER);
      await settle();

      expect(
        http.match((request) => request.url === CartApi.url && request.method === 'GET'),
      ).toHaveLength(1);
    });

    it('tells the store who signed in, so the header does not have to ask', async () => {
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();
      signInRequest().flush(CUSTOMER);
      await settle();

      expect(TestBed.inject(AccountStore).customer()).toEqual(CUSTOMER);
      // And asks nothing more: the response was the session.
      expect(http.match((request) => request.url === '/api/account/me')).toEqual([]);

      http.match((request) => request.url === CartApi.url).forEach((request) => request.flush(null));
    });

    it('goes to the order history when nothing said otherwise', async () => {
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();
      signInRequest().flush(CUSTOMER);
      await settle();

      expect(TestBed.inject(Router).url).toBe('/account/orders');

      http.match((request) => request.url === CartApi.url).forEach((request) => request.flush(null));
    });

    it('goes where the shopper was going, query string and all', async () => {
      create({ returnTo: '/anywhere?page=3' });
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();
      signInRequest().flush(CUSTOMER);
      await settle();

      expect(TestBed.inject(Router).url).toBe('/anywhere?page=3');

      http.match((request) => request.url === CartApi.url).forEach((request) => request.flush(null));
    });

    /**
     * **The one real security decision on these pages.** A link to
     * `/account/sign-in?returnTo=https://example.invalid/pay` would turn the shop's own sign-in form
     * into a phishing hop. `returnPath` refuses it; this is the test that the page actually goes through
     * `returnPath` rather than using the raw value.
     */
    it('refuses a returnTo that would leave this origin', async () => {
      create({ returnTo: 'https://example.invalid/pay' });
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();
      signInRequest().flush(CUSTOMER);
      await settle();

      expect(TestBed.inject(Router).url).toBe('/account/orders');

      http.match((request) => request.url === CartApi.url).forEach((request) => request.flush(null));
    });
  });

  describe('when it is refused', () => {
    /**
     * **Doc 08 §3.1 DB10 at the last step.** The server answers one 401 for a wrong password, an unknown
     * address, a staff address and a deactivated account, each at the same password-hash cost. A page
     * that added "we do not recognise that email", or marked the *email* box rather than neither, would
     * undo the whole property after all that care — so this asserts what is absent as well as what is
     * present.
     */
    it('says one thing, whatever was wrong, and marks neither box', async () => {
      await settle();
      type('sign-in-email', 'nobody@example.invalid');
      type('sign-in-password', 'wrong');
      submit();
      signInRequest().flush(
        {
          code: 'invalid_credentials',
          title: 'We could not sign you in',
          detail: 'That email address and password do not match an account.',
        },
        { status: 401, statusText: 'Unauthorized' },
      );
      await settle();

      expect(alertRegion().textContent).toContain('do not match an account');
      expect(text()).not.toMatch(/unknown|not recognise|no account with that/i);
      expect(field('sign-in-email').getAttribute('aria-invalid')).toBeNull();
      expect(field('sign-in-password').getAttribute('aria-invalid')).toBeNull();
    });

    /**
     * The whole point of {@link AccountNotice}: the page re-renders, the form and its values are still
     * there, and the only new thing is the sentence. Holding the node from before and comparing it is
     * the only way to prove the message was written into a region that already existed.
     */
    it('writes the refusal into the region that was already on the page', async () => {
      await settle();
      const before = alertRegion();

      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'wrong');
      submit();
      signInRequest().flush(
        { code: 'invalid_credentials', detail: 'Those do not match an account.' },
        { status: 401, statusText: 'x' },
      );
      await settle();

      expect(alertRegion()).toBe(before);
      expect(before.textContent).toContain('Those do not match an account.');
    });

    /**
     * WCAG 2.2 4.1.3, and the defect checkout recorded: a refused form that drew red field messages,
     * announced nothing and left focus on `<body>`, with axe reporting zero violations.
     */
    it('moves focus into the refusal rather than leaving it on body', async () => {
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'wrong');
      field('sign-in-password').focus();
      submit();
      signInRequest().flush(
        { code: 'invalid_credentials', detail: 'Those do not match an account.' },
        { status: 401, statusText: 'x' },
      );
      await settle();

      expect(document.activeElement).toBe(alertRegion());
      expect(document.activeElement).not.toBe(document.body);
    });

    it('keeps what was typed, so a lockout does not cost the shopper their email address', async () => {
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'wrong');
      submit();
      signInRequest().flush(
        { code: 'locked_out', detail: 'Too many attempts. Try again in a few minutes.' },
        { status: 423, statusText: 'Locked' },
      );
      await settle();

      expect(field('sign-in-email').value).toBe('nimal@example.invalid');
      expect(alertRegion().textContent).toContain('Try again in a few minutes');
    });

    it('shows the field message a 400 names, beside its own box', async () => {
      await settle();
      submit();
      signInRequest().flush(
        { errors: { email: ['Enter your email address.'], password: ['Enter your password.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(field('sign-in-email').getAttribute('aria-invalid')).toBe('true');
      expect(field('sign-in-email').getAttribute('aria-describedby')).toBe('si-email-error');
      expect(element.querySelector('#si-email-error')?.textContent).toContain(
        'Enter your email address.',
      );
      expect(element.querySelector('#si-password-error')?.textContent).toContain(
        'Enter your password.',
      );
    });
  });

  describe('the submit button', () => {
    /**
     * #109. A `disabled` attribute applied to the control that currently has focus throws focus to
     * `<body>` and fires no `focusin`, and pressing Enter in a field is a press of this button — so the
     * shopper's place would be thrown away by their own submit.
     */
    it('never carries the disabled attribute, in flight or at rest', async () => {
      await settle();

      expect(element.querySelectorAll('button[disabled], a[disabled], input[disabled]')).toHaveLength(0);

      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();
      await settle();

      expect(element.querySelectorAll('button[disabled]')).toHaveLength(0);
      expect(
        element.querySelector('[data-testid="sign-in-submit"]')?.getAttribute('aria-disabled'),
      ).toBe('true');

      signInRequest().flush(CUSTOMER);
      await settle();
      http.match((request) => request.url === CartApi.url).forEach((request) => request.flush(null));
    });

    /** `aria-disabled` is only half of it: the handler has to refuse the press as well. */
    it('refuses a second press while the first is in flight', async () => {
      await settle();
      type('sign-in-email', 'nimal@example.invalid');
      type('sign-in-password', 'a real password');
      submit();
      submit();
      submit();

      expect(
        http.match((request) => request.url === '/api/account/login'),
      ).toHaveLength(1);
    });
  });

  describe('a shopper who is already signed in', () => {
    /**
     * It says so rather than redirecting. A page a shopper deliberately opened that bounces them
     * elsewhere fights the back button, and the honest answer to "I opened the sign-in page while signed
     * in" is to say so and offer the way on.
     */
    it('says who is signed in and still offers the form', async () => {
      TestBed.inject(AccountStore).accept(CUSTOMER);
      await settle();

      expect(text()).toContain('already signed in as Nimal');
      expect(field('sign-in-email')).not.toBeNull();
      expect(TestBed.inject(Router).url).toBe('/');
    });
  });

  describe('nothing invented', () => {
    /**
     * **No placeholder that reads like data**, on any page of this task. A box holding
     * "nimal@example.com" is a box a shopper submits without reading, and a form that looks filled in is
     * a form nobody checks.
     */
    it('puts no placeholder and no value in either box', async () => {
      await settle();

      for (const input of element.querySelectorAll('input')) {
        expect(input.getAttribute('placeholder')).toBeNull();
        expect(input.value).toBe('');
      }
    });

    /** No copy may promise an email: the transport is unconfigured (doc 08 §3.8). */
    it('promises no email', async () => {
      await settle();

      expect(text()).not.toMatch(/email (you|your)/i);
      expect(text()).not.toMatch(/check your (inbox|email)/i);
    });
  });

  describe('the headings and the targets, hand-checked', () => {
    /**
     * **axe caught neither a wrongly-ranked heading nor a 20 px control on this project this week**, so
     * these are written by hand. One `h1`, no level skipped, and every control carrying the class that
     * sets the 44 px floor — jsdom has no layout, so the class is what is assertable here and the pixels
     * are a browser test's job.
     */
    it('has exactly one h1 and skips no heading level', async () => {
      await settle();

      const levels = [...element.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
        Number(node.tagName.slice(1)),
      );

      expect(levels.filter((level) => level === 1)).toHaveLength(1);
      expect(levels[0]).toBe(1);

      for (let i = 1; i < levels.length; i += 1) {
        expect(levels[i] - levels[i - 1], `${levels[i - 1]} then ${levels[i]}`).toBeLessThanOrEqual(1);
      }
    });

    it('gives every control a 44 px class and every box a label', async () => {
      await settle();

      expect(element.querySelector('[data-testid="sign-in-submit"]')?.className).toContain('btn');

      for (const link of element.querySelectorAll('a.link, button.link')) {
        expect(link.className, link.textContent ?? '').toContain('acc-inline-action');
      }

      for (const input of element.querySelectorAll('input')) {
        expect(input.className).toContain('acc-input');
        expect(element.querySelector(`label[for="${input.id}"]`), input.id).not.toBeNull();
      }
    });
  });
});
