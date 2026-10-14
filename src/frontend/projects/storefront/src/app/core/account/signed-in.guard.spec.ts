import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  GuardResult,
  MaybeAsync,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { isObservable } from 'rxjs';

import { CustomerSessionDto } from './account.model';
import { AccountStore } from './account.store';
import { DEFAULT_RETURN_PATH, customerSignedIn, returnPath } from './signed-in.guard';

const CUSTOMER: CustomerSessionDto = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: null,
  emailConfirmed: false,
};

/**
 * The guard on the three signed-in account routes, and the `returnTo` it hands the sign-in form.
 *
 * `returnPath` gets the most attention here because it is the one real security decision on these pages:
 * an unchecked `returnTo` turns the shop's own sign-in form into an open redirect, which is a phishing
 * primitive. The rest of the file is the three answers the guard can give.
 */
describe('customerSignedIn (W3-01)', () => {
  let http: HttpTestingController;

  function create(platform: 'browser' | 'server' = 'browser'): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);
  }

  /** Runs the guard for one URL, in an injection context, as the router would. */
  function run(url: string): MaybeAsync<GuardResult> {
    const state = { url } as RouterStateSnapshot;
    const route = {} as ActivatedRouteSnapshot;

    // The TestBed's injector is what the router would run the guard in: the guard injects PLATFORM_ID,
    // Router and AccountStore, all of which are rooted there.
    return TestBed.runInInjectionContext(() => customerSignedIn(route, state));
  }

  /** The guard's verdict, whether it answered synchronously or through the store's read. */
  async function verdict(url: string): Promise<GuardResult> {
    const answer = run(url);

    return isObservable(answer)
      ? await new Promise<GuardResult>((resolve) => answer.subscribe(resolve))
      : await answer;
  }

  beforeEach(() => create());

  afterEach(() => http.verify());

  /**
   * **The server lets the page render.** It has no cookie to read — `sd_customer_auth` is not forwarded
   * to the API during SSR — so the three answers it could give are: redirect everyone to the sign-in page
   * (every bookmarked account URL would then serve the wrong page), block on a cookie it does not have
   * (impossible), or render the shell and let the browser decide. It does the third.
   *
   * The platform check is tested rather than inferred from the store's answer, because
   * `AccountStore.load()` answers `of(null)` synchronously on the server — which, without the check,
   * would redirect.
   */
  it('lets the server render the page rather than redirecting every visitor', async () => {
    create('server');

    expect(await verdict('/account/orders')).toBe(true);
    expect(http.match(() => true)).toEqual([]);
  });

  it('lets a signed-in customer through', async () => {
    TestBed.inject(AccountStore).accept(CUSTOMER);

    expect(await verdict('/account/orders')).toBe(true);
  });

  /**
   * Doc 11 §7 G-24: confirmation gates **only** the linking of earlier guest orders. An unconfirmed
   * customer signs in, shops and checks out — so this guard must not be the place somebody quietly turns
   * confirmation into a gate. `CUSTOMER` above is deliberately unconfirmed.
   */
  it('lets an unconfirmed customer through, because confirmation is not a gate', async () => {
    TestBed.inject(AccountStore).accept({ ...CUSTOMER, emailConfirmed: false });

    expect(await verdict('/account/addresses')).toBe(true);
  });

  it('sends a signed-out visitor to the sign-in form', async () => {
    TestBed.inject(AccountStore).accept(null);

    const answer = await verdict('/account/orders');

    expect(answer).toBeInstanceOf(UrlTree);
    expect(String(answer)).toBe('/account/sign-in?returnTo=%2Faccount%2Forders');
  });

  /**
   * **The whole URL including its query string**, taken from the router's state rather than rebuilt from
   * the snapshot. A shopper sent to sign in from page 3 of their order history and returned to page 1
   * would read it as having lost their place.
   */
  it('keeps the query string in returnTo, so page 3 comes back as page 3', async () => {
    TestBed.inject(AccountStore).accept(null);

    const answer = await verdict('/account/orders?page=3');

    expect(String(answer)).toBe('/account/sign-in?returnTo=%2Faccount%2Forders%3Fpage%3D3');
  });

  it('reads the session through the store, so the guard and the header share one request', async () => {
    const pending = verdict('/account/profile');

    http.expectOne('/api/account/session').flush({ customer: CUSTOMER });

    expect(await pending).toBe(true);
    expect(TestBed.inject(AccountStore).customer()).toEqual(CUSTOMER);
  });

  /**
   * **A failed read is not a refusal.** The store leaves the session `unknown` rather than `signedOut`
   * when the shop could not answer, so the guard redirects — which is the conservative answer for a
   * guard, because it lands on a page the shopper can act on. What it must not do is let them through to
   * a page that will then show nothing.
   */
  it('redirects rather than throwing when the session read fails', async () => {
    const pending = verdict('/account/orders');

    http.expectOne('/api/account/session').flush(null, { status: 500, statusText: 'Server Error' });

    expect(await pending).toBeInstanceOf(UrlTree);
  });

  describe('returnPath, which is the one open-redirect decision on these pages', () => {
    it('keeps a same-origin path', () => {
      expect(returnPath('/account/orders')).toBe('/account/orders');
      expect(returnPath('/account/orders?page=2')).toBe('/account/orders?page=2');
    });

    /**
     * **Every one of these would be an open redirect.** A link to
     * `/account/sign-in?returnTo=https://example.invalid/pay` would turn the shop's own sign-in form
     * into a phishing hop, and `//example.invalid` is the same thing without a scheme: a browser reads a
     * leading `//` as protocol-relative and goes to another host. A backslash is the third way out —
     * some browsers normalise `/\evil.test` to `//evil.test` before resolving it.
     */
    it('refuses anything that could leave this origin', () => {
      const hostile = [
        'https://example.invalid/pay',
        'http://example.invalid',
        '//example.invalid',
        '/\\example.invalid',
        '/account\\..\\x',
        'javascript:alert(1)',
        'account/orders',
        '',
      ];

      for (const value of hostile) {
        expect(returnPath(value), value).toBe(DEFAULT_RETURN_PATH);
      }
    });

    it('falls back to the order history when there is no returnTo at all', () => {
      expect(returnPath(null)).toBe('/account/orders');
      expect(returnPath(undefined)).toBe('/account/orders');
    });
  });
});
