import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminAuthApi } from './admin-auth-api';

describe('AdminAuthApi', () => {
  let api: AdminAuthApi;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(AdminAuthApi);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('posts credentials to the login endpoint and returns the session on success', () => {
    const session = {
      userId: 1,
      email: 'admin@example.com',
      firstName: 'Ada',
      lastName: 'Lovelace',
      grantsAllPermissions: true,
      permissions: [],
      mustChangePassword: false,
    };
    let result: unknown;

    api.login('admin@example.com', 'secret').subscribe((value) => (result = value));

    const request = controller.expectOne(AdminAuthApi.loginUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'admin@example.com', password: 'secret' });
    request.flush(session);

    expect(result).toEqual(session);
  });

  it('sends the second-step code with a fresh antiforgery token (W1-10)', () => {
    let result: unknown;

    api.completeTwoFactorLogin('123 456', false).subscribe((value) => (result = value));

    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'the-token' });
    const request = controller.expectOne(AdminAuthApi.loginTwoFactorUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    expect(request.request.body).toEqual({ code: '123 456', isRecoveryCode: false });
    request.flush({ userId: 1 });

    expect(result).toEqual({ userId: 1 });
  });

  it.each([
    [401, 'invalid_two_factor_code', 'invalid_two_factor_code'],
    [401, 'two_factor_session_expired', 'two_factor_session_expired'],
    [423, 'locked_out', 'locked_out'],
    [500, undefined, 'unknown'],
  ])('maps a %s %s second-step reply to %s', (status, code, kind) => {
    let error: { kind: string } | undefined;

    api.completeTwoFactorLogin('000000', true).subscribe({ error: (e) => (error = e) });

    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 't' });
    controller.expectOne(AdminAuthApi.loginTwoFactorUrl).flush({ error: code }, { status, statusText: 'x' });

    expect(error?.kind).toBe(kind);
  });

  it('maps a 401 response to an invalid_credentials error with the server message', () => {
    let error: { kind: string; message: string } | undefined;

    api.login('admin@example.com', 'wrong').subscribe({ error: (e) => (error = e) });

    controller
      .expectOne(AdminAuthApi.loginUrl)
      .flush({ error: 'invalid_credentials', message: 'Invalid email or password.' }, { status: 401, statusText: 'Unauthorized' });

    expect(error).toEqual({ kind: 'invalid_credentials', message: 'Invalid email or password.' });
  });

  it('maps a 423 response to a locked_out error', () => {
    let error: { kind: string; message: string } | undefined;

    api.login('admin@example.com', 'wrong').subscribe({ error: (e) => (error = e) });

    controller
      .expectOne(AdminAuthApi.loginUrl)
      .flush({ error: 'locked_out', message: 'Try again later.' }, { status: 423, statusText: 'Locked' });

    expect(error?.kind).toBe('locked_out');
    expect(error?.message).toBe('Try again later.');
  });

  it('logout fetches an antiforgery token first and sends it as a header on the logout request', () => {
    api.logout().subscribe();

    const tokenRequest = controller.expectOne(AdminAuthApi.antiforgeryUrl);
    expect(tokenRequest.request.method).toBe('GET');
    tokenRequest.flush({ token: 'the-token' });

    const logoutRequest = controller.expectOne(AdminAuthApi.logoutUrl);
    expect(logoutRequest.request.method).toBe('POST');
    expect(logoutRequest.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    logoutRequest.flush(null);
  });

  it('changePassword fetches an antiforgery token first and posts both passwords with it', () => {
    let completed = false;
    api.changePassword('old', 'new').subscribe({ complete: () => (completed = true) });

    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'the-token' });
    const request = controller.expectOne(AdminAuthApi.changePasswordUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    expect(request.request.body).toEqual({ currentPassword: 'old', newPassword: 'new' });
    request.flush(null, { status: 204, statusText: 'No Content' });

    expect(completed).toBe(true);
  });

  it('changePassword maps the server error codes to error kinds', () => {
    const cases: { status: number; body: object | null; kind: string }[] = [
      { status: 400, body: { error: 'invalid_current_password' }, kind: 'invalid_current_password' },
      { status: 400, body: { error: 'new_password_rejected', errors: { newPassword: ['Too short.'] } }, kind: 'new_password_rejected' },
      { status: 423, body: { error: 'locked_out', lockoutEndUtc: '2026-09-29T10:00:00Z' }, kind: 'locked_out' },
      { status: 401, body: null, kind: 'session_expired' },
      { status: 500, body: null, kind: 'unknown' },
    ];

    for (const testCase of cases) {
      let error: { kind: string; rules?: string[] } | undefined;
      api.changePassword('old', 'new').subscribe({ error: (e) => (error = e) });
      controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 't' });
      controller
        .expectOne(AdminAuthApi.changePasswordUrl)
        .flush(testCase.body, { status: testCase.status, statusText: 'Error' });

      expect(error?.kind).toBe(testCase.kind);
      if (testCase.kind === 'new_password_rejected') {
        expect(error?.rules).toEqual(['Too short.']);
      }
    }
  });
});
