import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CustomerSessionDto } from './account.model';
import { AccountStore } from './account.store';

const CUSTOMER: CustomerSessionDto = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: 'Perera',
  emailConfirmed: true,
};

/**
 * The signed-in customer, as the shell and the account pages read it.
 *
 * Three properties are worth more than the rest of this file put together, and each is a defect this
 * project has already shipped in some form:
 *
 * 1. **The server asks nothing.** `sd_customer_auth` is not forwarded during SSR, so a request made
 *    there would answer 401 for every visitor, and a store that then said "signed out" would be
 *    rendering a wrong answer into markup the browser has to agree with.
 * 2. **One request however many callers.** The route guard and the site header both ask on a cold load,
 *    and the brief names a cold-load race as a real defect this week.
 * 3. **"Nobody has asked" is not "signed out".** They are the same falsy value and two red `main`
 *    branches on this project were built on that distinction.
 */
describe('AccountStore (ACC-01)', () => {
  let http: HttpTestingController;
  let store: AccountStore;

  function create(platform: 'browser' | 'server' = 'browser'): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AccountStore);
  }

  beforeEach(() => create());

  afterEach(() => http.verify());

  describe('what the server does', () => {
    /**
     * **Nothing, and that is the decision.** `cartCookieInterceptor` forwards `sd_cart` and never the
     * whole `Cookie` header — on purpose, because the same browser may carry `sd_admin_auth` — so the
     * SSR process has no way to know who this visitor is. Asking anyway would cost a request per render
     * and answer 401 for everybody.
     */
    it('asks the shop nothing during a server render, and does not claim the visitor is signed out', () => {
      create('server');

      let answered: CustomerSessionDto | null | undefined;
      store.load().subscribe((customer) => (answered = customer));

      expect(http.match(() => true)).toEqual([]);
      expect(answered).toBeNull();
      expect(store.status()).toBe('unknown');
      expect(store.isSignedOut()).toBe(false);
      expect(store.isSignedIn()).toBe(false);
    });
  });

  describe('one read per application load', () => {
    /**
     * The cold-load race, stated as a test. On a real page load the signed-in guard runs *during* the
     * first navigation and the header's read is scheduled by `afterNextRender`; without single-flight
     * those are two requests whose answers arrive in an order nothing controls.
     */
    it('makes one request however many callers ask, and gives all of them the same answer', () => {
      const answers: (CustomerSessionDto | null)[] = [];

      store.load().subscribe((customer) => answers.push(customer));
      store.load().subscribe((customer) => answers.push(customer));
      store.load().subscribe((customer) => answers.push(customer));

      http.expectOne('/api/account/session').flush({ customer: CUSTOMER });

      expect(answers).toEqual([CUSTOMER, CUSTOMER, CUSTOMER]);
    });

    it('answers a later caller from the read that already happened, with no request at all', () => {
      store.load().subscribe();
      http.expectOne('/api/account/session').flush({ customer: CUSTOMER });

      let answered: CustomerSessionDto | null | undefined;
      store.load().subscribe((customer) => (answered = customer));

      expect(http.match(() => true)).toEqual([]);
      expect(answered).toEqual(CUSTOMER);
    });

    it('asks again when told to, and only then', () => {
      store.load().subscribe();
      http.expectOne('/api/account/session').flush({ customer: CUSTOMER });

      store.refresh().subscribe();
      http.expectOne('/api/account/session').flush({ customer: { ...CUSTOMER, firstName: 'Nimali' } });

      expect(store.customer()?.firstName).toBe('Nimali');
    });
  });

  describe('what each answer means', () => {
    it('takes a 200 as the signed-in customer', () => {
      store.load().subscribe();
      http.expectOne('/api/account/session').flush({ customer: CUSTOMER });

      expect(store.customer()).toEqual(CUSTOMER);
      expect(store.status()).toBe('signedIn');
      expect(store.isSignedIn()).toBe(true);
      expect(store.isSignedOut()).toBe(false);
    });

    /** The ordinary state of every visitor who is not signed in. An answer, not an error. */
    it('takes a null customer as signed out', () => {
      store.load().subscribe();
      http.expectOne('/api/account/session').flush({ customer: null });

      expect(store.customer()).toBeNull();
      expect(store.status()).toBe('signedOut');
      expect(store.isSignedOut()).toBe(true);
    });

    /** `/session` never answers 401 today, but one from a proxy still means "not signed in". */
    it('still takes a 401 as signed out', () => {
      store.load().subscribe();
      http.expectOne('/api/account/session').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(store.customer()).toBeNull();
      expect(store.status()).toBe('signedOut');
      expect(store.isSignedOut()).toBe(true);
    });

    /**
     * **A 500 is not an answer about the session**, and the difference matters: the signed-in guard
     * redirects on `signedOut`, so a store that read a network blip as "signed out" would throw a
     * signed-in customer out of their own account for the length of an API wobble. #30 is the same
     * lesson on the catalogue side — a 404 for an outage drops a live listing out of the index.
     */
    it('leaves the session unknown when the shop could not answer, and lets the next caller try again', () => {
      store.load().subscribe();
      http.expectOne('/api/account/session').flush(null, { status: 500, statusText: 'Server Error' });

      expect(store.status()).toBe('unknown');
      expect(store.isSignedOut()).toBe(false);

      // The memo was dropped, so this is a real second request rather than a replay of the failure.
      store.load().subscribe();
      http.expectOne('/api/account/session').flush({ customer: CUSTOMER });

      expect(store.status()).toBe('signedIn');
    });

    it('treats an offline browser the same way as a server error', () => {
      store.load().subscribe();
      http.expectOne('/api/account/session').error(new ProgressEvent('offline'), { status: 0 });

      expect(store.status()).toBe('unknown');
      expect(store.isSignedOut()).toBe(false);
    });
  });

  describe('a session handed over rather than asked for', () => {
    /**
     * Sign-in, email confirmation and change-password each answer with the whole session, so the store is
     * told. That is one fewer request and, more to the point, no window in which the header disagrees
     * with the page that just signed the shopper in.
     */
    it('takes a session from a caller and asks nothing afterwards', () => {
      store.accept(CUSTOMER);

      expect(store.status()).toBe('signedIn');
      expect(store.customer()).toEqual(CUSTOMER);

      store.load().subscribe();
      expect(http.match(() => true)).toEqual([]);
    });

    it('takes null as signed out, which is what the route guard reads', () => {
      store.accept(null);

      expect(store.status()).toBe('signedOut');
      expect(store.isSignedOut()).toBe(true);

      store.load().subscribe();
      expect(http.match(() => true)).toEqual([]);
    });
  });

  describe('the confirmation flag (doc 11 §7 G-24)', () => {
    it('is true for a signed-in customer who has not confirmed', () => {
      store.accept({ ...CUSTOMER, emailConfirmed: false });

      expect(store.needsEmailConfirmation()).toBe(true);
    });

    it('is false for a confirmed customer', () => {
      store.accept(CUSTOMER);

      expect(store.needsEmailConfirmation()).toBe(false);
    });

    /**
     * **Not `!emailConfirmed`.** A visitor nobody has asked about and a signed-out visitor both have no
     * customer at all, and reading that as "unconfirmed" would put a "confirm your email" note into the
     * server's markup for every visitor to every account page.
     */
    it('is false when there is nobody, signed out or not yet asked', () => {
      expect(store.needsEmailConfirmation()).toBe(false);

      store.accept(null);
      expect(store.needsEmailConfirmation()).toBe(false);
    });
  });

  describe('signing out (doc 11 P11 step B.7)', () => {
    it('clears the session when the server removes the cookie', () => {
      store.accept(CUSTOMER);

      let ok: boolean | undefined;
      store.signOut().subscribe((result) => (ok = result));
      http
        .expectOne((request) => request.url === '/api/account/logout' && request.method === 'POST')
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(ok).toBe(true);
      expect(store.customer()).toBeNull();
      expect(store.status()).toBe('signedOut');
    });

    /**
     * **A 401 here means the session had already gone**, so refusing to clear the local state would leave
     * the header greeting somebody who is not signed in. The caller is told the request failed, and the
     * state is cleared either way.
     */
    it('clears the session even when the server refuses the request', () => {
      store.accept(CUSTOMER);

      let ok: boolean | undefined;
      store.signOut().subscribe((result) => (ok = result));
      http
        .expectOne((request) => request.url === '/api/account/logout')
        .flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(ok).toBe(false);
      expect(store.customer()).toBeNull();
      expect(store.status()).toBe('signedOut');
    });

    it('leaves a later read answering signed out, with no request', () => {
      store.accept(CUSTOMER);
      store.signOut().subscribe();
      http.expectOne('/api/account/logout').flush(null, { status: 204, statusText: 'No Content' });

      store.load().subscribe();
      expect(http.match(() => true)).toEqual([]);
      expect(store.isSignedOut()).toBe(true);
    });
  });
});
