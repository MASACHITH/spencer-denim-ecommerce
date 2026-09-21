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
    };
    let result: unknown;

    api.login('admin@example.com', 'secret').subscribe((value) => (result = value));

    const request = controller.expectOne(AdminAuthApi.loginUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'admin@example.com', password: 'secret' });
    request.flush(session);

    expect(result).toEqual(session);
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
});
