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

import { ConfirmEmailPage } from './confirm-email-page';
import { CustomerSessionDto } from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';
import { CartApi } from '../../core/cart/cart-api';

const CUSTOMER: CustomerSessionDto = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: 'Perera',
  emailConfirmed: true,
};

/** The server's one opaque refusal for a spent, expired or wrong link, from `InvalidLink`. */
const SERVER_REFUSAL =
  'This link has already been used, or it has expired. Ask for a new one and use the newest message.';

/**
 * Confirm an email address, and show the guest orders it brought with it (ACC-01, doc 11 P11 step A.4,
 * doc 08 §3.1 D5).
 *
 * The two properties this file exists for:
 *
 * - **the server must not redeem the token.** The reply carries a `Set-Cookie`, which on the SSR process
 *   would be set on a response to nobody, and the token is single-use, so a prefetch or a crawl would
 *   spend it and the shopper would open their own live link to be told it had been used.
 * - **"no answer yet" must not render as "refused."** Those are the same falsy value, and rendering the
 *   refusal branch for the whole of the server's response would tell every shopper their link had failed.
 */
describe('ConfirmEmailPage (ACC-01)', () => {
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

  async function open(url: string, platform: 'browser' | 'server' = 'browser'): Promise<void> {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'account/confirm-email', component: ConfirmEmailPage },
          { path: 'account/orders', children: [] },
          { path: 'account/profile', children: [] },
          { path: 'account/sign-in', children: [] },
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

  function request(): TestRequest {
    return http.expectOne(
      (candidate) => candidate.url === '/api/account/confirm-email' && candidate.method === 'POST',
    );
  }

  function flushBag(): void {
    http
      .match((candidate) => candidate.url === CartApi.url && candidate.method === 'GET')
      .forEach((candidate) => candidate.flush(null));
  }

  function statusRegion(): HTMLElement {
    return element.querySelector('[data-testid="confirm-email-status"]') as HTMLElement;
  }

  function alertRegion(): HTMLElement {
    return element.querySelector('[data-testid="confirm-email-alert"]') as HTMLElement;
  }

  function text(): string {
    return element.textContent ?? '';
  }

  afterEach(() => http?.verify());

  describe('what the server does with a one-time token', () => {
    /**
     * **Nothing, and both reasons are enough on their own.** The reply carries a `Set-Cookie` — the
     * holder of a live confirmation link is the account's owner, so the server signs them in — which on
     * the SSR process would be set on a response to nobody; and the token is single-use, so a render is
     * not an intention.
     */
    it('posts nothing during a server render', async () => {
      await open('/account/confirm-email?token=a-single-use-token', 'server');

      expect(http.match(() => true)).toEqual([]);
    });

    /**
     * **And says it is working rather than that it failed.** "No answer yet" and "refused" are the same
     * falsy value; the first version of this template rendered the refusal branch here, which would
     * have told every shopper their own live link had failed, for the whole of the server's response.
     */
    it('says it is confirming, not that the link did not work', async () => {
      await open('/account/confirm-email?token=a-single-use-token', 'server');

      expect(element.querySelector('[data-testid="confirm-email-working"]')).not.toBeNull();
      expect(text()).not.toContain('Ask for a new link');
      expect(alertRegion().textContent).toBe('');
    });

    it('renders both live regions before there is anything to announce', async () => {
      await open('/account/confirm-email?token=a-token', 'server');

      expect(statusRegion().getAttribute('role')).toBe('status');
      expect(alertRegion().getAttribute('role')).toBe('alert');
    });

    it('asks not to be indexed and not to be stored', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: [] });
      await settle();
      flushBag();

      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
      expect(new Headers(response.headers ?? {}).get('Cache-Control')).toContain('no-store');
      expect(TestBed.inject(Title).getTitle()).toBe('Confirm your email address | Colombo Wardrobe');
    });
  });

  describe('the token', () => {
    it('posts the token from the query string, in the body', async () => {
      await open('/account/confirm-email?token=a-single-use-token');

      expect(request().request.body).toEqual({ token: 'a-single-use-token' });
    });

    /** The same decision `reset-password-page.ts` makes: it must not sit in history or a screenshot. */
    it('strips the token from the address bar', async () => {
      await open('/account/confirm-email?token=a-single-use-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: [] });
      await settle();
      flushBag();

      expect(TestBed.inject(Router).url).toBe('/account/confirm-email');
    });

    it('posts once and only once', async () => {
      await open('/account/confirm-email?token=a-single-use-token');
      await settle();
      await settle();

      expect(http.match((candidate) => candidate.url === '/api/account/confirm-email')).toHaveLength(1);
    });

    it('posts nothing and explains itself when there is no token', async () => {
      await open('/account/confirm-email');

      expect(http.match(() => true)).toEqual([]);
      expect(element.querySelector('[data-testid="confirm-email-no-token"]')).not.toBeNull();
    });
  });

  describe('the orders it brought with it (doc 11 P11 step A.4)', () => {
    /**
     * Doc 11 P11's own example: "Customer registers with the email used for 3 guest orders → after
     * confirming, all 3 appear in My account." The server names them, so the page names them — a silent
     * confirmation leaves a shopper wondering whether it worked.
     */
    it('names every order that became this customer’s', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({
        customer: CUSTOMER,
        linkedOrderNumbers: ['SD-2026-00041', 'SD-2026-00033', 'SD-2026-00012'],
      });
      await settle();
      flushBag();

      const listed = [
        ...element.querySelectorAll('[data-testid="confirm-email-linked"] li'),
      ].map((node) => node.textContent?.trim());

      expect(listed).toEqual(['SD-2026-00041', 'SD-2026-00033', 'SD-2026-00012']);
      expect(text()).toContain('3 earlier orders are now yours');
    });

    it('uses the singular for one order', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: ['SD-2026-00041'] });
      await settle();
      flushBag();

      expect(text()).toContain('One earlier order is now yours');
    });

    /**
     * **Empty is the ordinary case** — most people register before they order — so it is said as a fact
     * rather than as a disappointment, and the confirmation still counts as having worked.
     */
    it('says so plainly when there were none, and still confirms', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: [] });
      await settle();
      flushBag();

      expect(element.querySelector('[data-testid="confirm-email-none-linked"]')).not.toBeNull();
      expect(statusRegion().textContent).toContain('confirmed');
      expect(element.querySelector('[data-testid="confirm-email-orders"]')).not.toBeNull();
      expect(text()).not.toContain('Ask for a new link');
    });

    it('takes the session the reply carries, so the header greets them in the same frame', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: [] });
      await settle();
      flushBag();

      expect(TestBed.inject(AccountStore).customer()).toEqual(CUSTOMER);
      expect(http.match((candidate) => candidate.url === '/api/account/me')).toEqual([]);
    });

    /**
     * The confirmation signs the shopper in, and signing in is what merges the guest bag on the server —
     * so the header's badge is stale by exactly the lines that moved.
     */
    it('re-reads the bag, because confirming signed the shopper in', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: [] });
      await settle();

      expect(
        http.match((candidate) => candidate.url === CartApi.url && candidate.method === 'GET'),
      ).toHaveLength(1);
    });

    it('moves focus to the confirmation', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: [] });
      await settle();
      flushBag();

      expect(document.activeElement).toBe(statusRegion());
    });

    it('promises no email', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: ['SD-2026-00041'] });
      await settle();
      flushBag();

      expect(text()).not.toMatch(/check your (inbox|email)/i);
      expect(text()).not.toMatch(/\bwe (have |)sent\b/i);
    });
  });

  describe('when the link cannot be used', () => {
    /**
     * **One opaque refusal for all four cases** — already used, expired, wrong, or for an address the
     * account no longer uses — because saying "expired" rather than "wrong" tells a holder the token was
     * once real. The page prints the server's sentence and adds nothing.
     */
    it('shows the server’s one sentence and the way to a new link', async () => {
      await open('/account/confirm-email?token=a-spent-token');
      request().flush(
        {
          code: 'invalid_link',
          title: 'We could not confirm your email address',
          detail: SERVER_REFUSAL,
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(alertRegion().textContent).toBe(SERVER_REFUSAL);
      expect(text()).toContain('Ask for a new link');
      expect(document.activeElement).toBe(alertRegion());
      expect(element.querySelector('[data-testid="confirm-email-linked"]')).toBeNull();
    });

    it('writes the refusal into the region that was already on the page', async () => {
      await open('/account/confirm-email?token=a-spent-token');
      const before = alertRegion();

      request().flush({ code: 'invalid_link', detail: SERVER_REFUSAL }, {
        status: 400,
        statusText: 'Bad Request',
      });
      await settle();

      expect(alertRegion()).toBe(before);
      expect(before.textContent).toBe(SERVER_REFUSAL);
    });

    it('does not sign anyone in or touch the bag', async () => {
      await open('/account/confirm-email?token=a-spent-token');
      request().flush({ code: 'invalid_link', detail: SERVER_REFUSAL }, {
        status: 400,
        statusText: 'Bad Request',
      });
      await settle();

      expect(TestBed.inject(AccountStore).customer()).toBeNull();
      expect(http.match(() => true)).toEqual([]);
    });
  });

  describe('the checks axe did not make', () => {
    it('has one h1 and skips no heading level', async () => {
      await open('/account/confirm-email?token=a-token');
      request().flush({ customer: CUSTOMER, linkedOrderNumbers: ['SD-2026-00041'] });
      await settle();
      flushBag();

      const levels = [...element.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
        Number(node.tagName.slice(1)),
      );

      expect(levels.filter((level) => level === 1)).toHaveLength(1);
      expect(levels).toEqual([1, 2]);
    });
  });
});
