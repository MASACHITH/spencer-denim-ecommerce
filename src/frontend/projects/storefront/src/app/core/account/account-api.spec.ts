import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  AccountApi,
  accountErrorMessage,
  accountFailure,
  addressLimitOf,
  fieldErrorsOf,
  lockoutEndOf,
} from './account-api';

/**
 * The customer account API client (`/api/account`).
 *
 * Two things are worth testing here and the rest is plumbing: **which URL and method each call uses**,
 * because an endpoint name typed twice is an endpoint name that can be typed wrong, and **the refusal
 * classification**, which is the part with judgement in it — two of the five statuses this surface
 * answers carry more than one meaning, told apart by the problem's `code` extension.
 */
describe('AccountApi (ACC-01, ACC-02)', () => {
  let api: AccountApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    api = TestBed.inject(AccountApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** A refusal as the API sends it: RFC 9457, with the extensions beside `title` and `detail`. */
  function problem(status: number, body: unknown): HttpErrorResponse {
    return new HttpErrorResponse({ status, error: body, statusText: 'x', url: '/api/account/x' });
  }

  describe('the endpoints it knows the names of', () => {
    it('posts a registration without a session being issued', () => {
      api
        .register({
          firstName: 'Nimal',
          lastName: null,
          email: 'nimal@example.invalid',
          password: 'correct horse battery',
          phone: null,
        })
        .subscribe();

      const request = http.expectOne('/api/account/register');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        firstName: 'Nimal',
        lastName: null,
        email: 'nimal@example.invalid',
        password: 'correct horse battery',
        phone: null,
      });

      request.flush({ message: 'If that email address can be used…' }, { status: 202, statusText: 'Accepted' });
    });

    it('signs in at /login, not /sign-in', () => {
      api.signIn('nimal@example.invalid', 'secret').subscribe();

      const request = http.expectOne('/api/account/login');
      expect(request.request.method).toBe('POST');
      request.flush({ userId: 1, email: 'x', firstName: 'N', lastName: null, emailConfirmed: false });
    });

    it('signs out at /logout with a POST', () => {
      api.signOut().subscribe();

      const request = http.expectOne('/api/account/logout');
      expect(request.request.method).toBe('POST');
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('reads the session with a GET on /session, which answers 200 signed in or not', () => {
      api.session().subscribe();

      const request = http.expectOne('/api/account/session');
      expect(request.request.method).toBe('GET');
      request.flush({ customer: null });
    });

    /**
     * **The token travels in the body although it arrived in the page's URL.** A token in a request URL
     * lands in the API's access log and in any proxy's; `CustomerTokenRequest` on the server records the
     * same decision from the other side. This is the test that notices if it is ever moved into the path
     * or the query string.
     */
    it('posts a confirmation token in the body and never in the URL', () => {
      api.confirmEmail('a-single-use-token').subscribe();

      const request = http.expectOne('/api/account/confirm-email');
      expect(request.request.method).toBe('POST');
      expect(request.request.urlWithParams).toBe('/api/account/confirm-email');
      expect(request.request.body).toEqual({ token: 'a-single-use-token' });

      request.flush({ customer: null, linkedOrderNumbers: [] });
    });

    it('posts a reset token and password in the body', () => {
      api.resetPassword('a-token', 'a new password').subscribe();

      const request = http.expectOne('/api/account/reset-password');
      expect(request.request.urlWithParams).toBe('/api/account/reset-password');
      expect(request.request.body).toEqual({ token: 'a-token', password: 'a new password' });

      request.flush({ message: 'Your password is set.' });
    });

    it('asks for a new confirmation link with no body', () => {
      api.resendConfirmation().subscribe();

      const request = http.expectOne('/api/account/resend-confirmation');
      expect(request.request.method).toBe('POST');
      request.flush({ message: 'queued' }, { status: 202, statusText: 'Accepted' });
    });

    it('reads and writes the profile at one address with GET and PUT', () => {
      api.profile().subscribe();
      expect(http.expectOne('/api/account/profile').request.method).toBe('GET');
      http.verify();

      api
        .saveProfile({ firstName: 'Nimal', lastName: null, phone: null, defaultDistrictId: 3 })
        .subscribe();

      const write = http.expectOne('/api/account/profile');
      expect(write.request.method).toBe('PUT');
      expect(write.request.body).toEqual({
        firstName: 'Nimal',
        lastName: null,
        phone: null,
        defaultDistrictId: 3,
      });

      write.flush({});
    });

    it('uses the five address routes the server declares', () => {
      const edit = {
        label: null,
        recipientName: 'Nimal',
        phone: '0771234567',
        line1: '1 A Road',
        line2: null,
        city: 'Colombo',
        districtId: 1,
        postalCode: null,
        landmark: null,
        isDefault: false,
      };

      const calls: readonly [() => void, string, string][] = [
        [() => api.addresses().subscribe(), 'GET', '/api/account/addresses'],
        [() => api.addAddress(edit).subscribe(), 'POST', '/api/account/addresses'],
        [() => api.updateAddress(4, edit).subscribe(), 'PUT', '/api/account/addresses/4'],
        [() => api.deleteAddress(4).subscribe(), 'DELETE', '/api/account/addresses/4'],
        [() => api.setDefaultAddress(4).subscribe(), 'POST', '/api/account/addresses/4/default'],
      ];

      for (const [call, method, url] of calls) {
        call();
        const request = http.expectOne(url);
        expect(request.request.method, `${method} ${url}`).toBe(method);
        request.flush([]);
      }
    });

    /**
     * **`pageSize` is deliberately not sent.** The server's default is ten, it clamps anything outside
     * 1–50 back to ten, and it echoes the size back — so a number sent from here would be a second copy
     * of `GetCustomerOrdersQueryHandler.DefaultPageSize` and the two could drift apart. This is the test
     * that notices if one is ever added.
     */
    it('asks for a page of orders and sends no page size', () => {
      api.orders(3).subscribe();

      const request = http.expectOne((candidate) => candidate.url === '/api/account/orders');
      expect(request.request.params.get('page')).toBe('3');
      expect(request.request.params.get('pageSize')).toBeNull();

      request.flush({ orders: [], page: 3, pageSize: 10, totalCount: 0, hasMore: false });
    });

    it('never asks for a page below 1, however it is called', () => {
      api.orders(-4).subscribe();

      const request: TestRequest = http.expectOne(
        (candidate) => candidate.url === '/api/account/orders',
      );
      expect(request.request.params.get('page')).toBe('1');

      request.flush({ orders: [], page: 1, pageSize: 10, totalCount: 0, hasMore: false });
    });
  });

  describe('what a refusal means', () => {
    /**
     * **Both 400s and both 401s, told apart by `code` and not by the title.** A title is prose and
     * matching on it breaks silently when somebody improves the wording — `checkout-api.ts` records the
     * same lesson. These four are the only ambiguous statuses on this surface, and getting one wrong
     * means a spent reset link rendered beside a password box, or a shopper whose session expired being
     * told their password was wrong.
     */
    it('tells a validation problem from a spent link, both 400', () => {
      expect(accountFailure(problem(400, { errors: { password: ['Too short.'] } }))).toBe('invalid');
      expect(accountFailure(problem(400, { code: 'invalid_link' }))).toBe('invalidLink');
    });

    it('tells wrong credentials from a session that is gone, both 401', () => {
      expect(accountFailure(problem(401, { code: 'invalid_credentials' }))).toBe('credentials');
      expect(accountFailure(problem(401, null))).toBe('signedOut');
    });

    /** W3-01 Blocker 2: a confirmation link opened signed out is neither of those. */
    it('tells a confirmation that needs a session from both, by its code', () => {
      expect(accountFailure(problem(401, { code: 'sign_in_required' }))).toBe('signInRequired');
      expect(
        accountErrorMessage(problem(401, { code: 'sign_in_required', detail: 'Sign in, then open the link.' })),
      ).toBe('Sign in, then open the link.');
      expect(accountErrorMessage(problem(401, { code: 'sign_in_required' }))).toBe(
        'Sign in to your account, then open this link again.',
      );
    });

    it('classifies the rest by status', () => {
      expect(accountFailure(problem(423, { code: 'locked_out' }))).toBe('lockedOut');
      expect(accountFailure(problem(429, { code: 'rate_limited' }))).toBe('rateLimited');
      expect(accountFailure(problem(409, { code: 'address_limit_reached' }))).toBe('addressLimit');
      expect(accountFailure(problem(404, { code: 'address_not_found' }))).toBe('notFound');
      expect(accountFailure(problem(0, null))).toBe('offline');
      expect(accountFailure(problem(500, null))).toBe('failed');
      expect(accountFailure(new Error('not an HTTP failure'))).toBe('failed');
    });

    it('reads the field messages a 400 names, keyed by the API’s own field names', () => {
      const errors = fieldErrorsOf(
        problem(400, { errors: { districtId: ['Choose a district.'], phone: ['Enter a phone number.'] } }),
      );

      expect(errors.get('districtId')).toBe('Choose a district.');
      expect(errors.get('phone')).toBe('Enter a phone number.');
    });

    it('reads a lockout’s end time, and nothing else carries one', () => {
      const end = lockoutEndOf(
        problem(423, { code: 'locked_out', lockoutEndUtc: '2026-10-14T09:30:00.0000000Z' }),
      );

      expect(end?.toISOString()).toBe('2026-10-14T09:30:00.000Z');
      expect(lockoutEndOf(problem(401, { code: 'invalid_credentials' }))).toBeNull();
      expect(lockoutEndOf(problem(423, { code: 'locked_out', lockoutEndUtc: 'not a date' }))).toBeNull();
    });

    /**
     * The number comes from the problem and not from `SAVED_ADDRESS_LIMIT`, so the sentence a shopper
     * reads names the cap the server actually enforced even if the two ever disagree.
     */
    it('reads the address limit the server refused at', () => {
      expect(addressLimitOf(problem(409, { code: 'address_limit_reached', limit: 10 }))).toBe(10);
      expect(addressLimitOf(problem(409, { code: 'address_limit_reached' }))).toBeNull();
    });
  });

  describe('the sentence a shopper reads', () => {
    /**
     * **Four refusals pass the server's own `detail` through**, because the server is the thing that
     * knows the reason and has already been careful about the two properties this must not undo: it
     * never reveals whether an address has an account, and it never promises an email.
     */
    it('passes the server’s wording through for a spent link, a lockout, a rate limit and the cap', () => {
      expect(accountErrorMessage(problem(400, { code: 'invalid_link', detail: 'Already used.' }))).toBe(
        'Already used.',
      );
      expect(accountErrorMessage(problem(423, { code: 'locked_out', detail: 'Try again shortly.' }))).toBe(
        'Try again shortly.',
      );
      expect(accountErrorMessage(problem(429, { code: 'rate_limited', detail: 'Too many.' }))).toBe(
        'Too many.',
      );
      expect(
        accountErrorMessage(problem(409, { code: 'address_limit_reached', detail: 'Remove one.' })),
      ).toBe('Remove one.');
    });

    it('shows the field message a 400 names rather than a sentence of its own', () => {
      expect(accountErrorMessage(problem(400, { errors: { password: ['Use at least 10.'] } }))).toBe(
        'Use at least 10.',
      );
    });

    /**
     * **Doc 08 §3.1 DB10, at the last step.** The server answers one 401 for a wrong password, an unknown
     * address, a staff address and a deactivated account alike, each at the same password-hash cost. A
     * client sentence that said "we do not recognise that email" would undo the whole property after all
     * that care — so this test reads the message and asserts what is *not* in it, both when the server
     * worded the refusal and when the body arrived without one.
     */
    it('never says whether the address is known', () => {
      const worded = accountErrorMessage(
        problem(401, {
          code: 'invalid_credentials',
          detail: 'That email address and password do not match an account.',
        }),
      );
      const bare = accountErrorMessage(problem(401, { code: 'invalid_credentials' }));

      expect(worded).toBe('That email address and password do not match an account.');
      expect(bare).toBe('That email address and password do not match an account.');

      for (const message of [worded, bare]) {
        expect(message).not.toMatch(/unknown|not recognise|no account with|wrong password/i);
      }
    });

    /**
     * **A `detail` is trusted only when the API's own `code` is beside it.** ASP.NET's unhandled-500
     * problem details carry a `detail` of their own — "An error occurred while processing your request."
     * — and a reverse proxy can put anything in a body, so a function that printed every `detail` would
     * eventually show infrastructure prose to a shopper. This is the test that notices if the gate moves
     * from the code to the mere presence of the field.
     */
    it('ignores a detail that does not come with one of the API’s own codes', () => {
      expect(
        accountErrorMessage(problem(500, { detail: 'An error occurred while processing your request.' })),
      ).toBe('Something went wrong at our end. Please try again in a moment.');

      expect(accountErrorMessage(problem(401, { detail: 'Bearer realm=api' }))).toBe(
        'You are not signed in any more. Sign in again to carry on.',
      );
    });

    /**
     * **No message on these pages may promise an email.** W3-02 removed "we will email your order
     * number" from checkout because nothing could send it, and `checkout-page.spec.ts` pins its absence
     * with a live assertion. The same rule applies here, and the fallbacks are the only sentences this
     * task writes rather than passes through — so they are the ones worth sweeping.
     */
    it('promises no email in any sentence it writes itself', () => {
      const statuses: readonly [number, unknown][] = [
        [0, null],
        [401, null],
        [401, { code: 'invalid_credentials' }],
        [401, { code: 'sign_in_required' }],
        [404, { code: 'address_not_found' }],
        [500, null],
        [400, { errors: {} }],
      ];

      for (const [status, body] of statuses) {
        const message = accountErrorMessage(problem(status, body));

        expect(message, `${status}`).not.toMatch(/\bemail(ed)? (you|your|a link)\b/i);
        expect(message, `${status}`).not.toMatch(/check your (inbox|email)/i);
        expect(message, `${status}`).not.toMatch(/\bwe (have |)sent\b/i);
      }
    });

    it('says what to do when the session has gone, rather than refusing the shopper', () => {
      expect(accountErrorMessage(problem(401, null))).toBe(
        'You are not signed in any more. Sign in again to carry on.',
      );
    });

    it('says nothing about a status code or a stack trace', () => {
      expect(accountErrorMessage(problem(500, '<!DOCTYPE html><h1>Server Error</h1>'))).not.toMatch(
        /500|DOCTYPE|stack|Exception/i,
      );
    });
  });
});
