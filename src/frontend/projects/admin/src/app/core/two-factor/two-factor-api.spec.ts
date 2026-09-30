import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { TwoFactorApi, type TwoFactorError } from './two-factor-api';

describe('TwoFactorApi', () => {
  let api: TwoFactorApi;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(TwoFactorApi);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function expectPost(url: string) {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'the-token' });
    const request = controller.expectOne(url);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    return request;
  }

  it('reads the status', () => {
    let enabled: boolean | undefined;
    api.status().subscribe((status) => (enabled = status.enabled));

    controller.expectOne(TwoFactorApi.statusUrl).flush({ enabled: true, required: true, canDisable: false, recoveryCodesLeft: 3 });

    expect(enabled).toBe(true);
  });

  it('posts every change with the antiforgery token and unwraps the recovery codes', () => {
    let enabledCodes: string[] = [];
    let regeneratedCodes: string[] = [];

    api.setup().subscribe();
    expectPost(TwoFactorApi.setupUrl).flush({ sharedKey: 'k', authenticatorUri: 'otpauth://totp/x' });

    api.enable('123456').subscribe((codes) => (enabledCodes = codes));
    const enable = expectPost(TwoFactorApi.enableUrl);
    expect(enable.request.body).toEqual({ code: '123456' });
    enable.flush({ recoveryCodes: ['A'] });

    api.regenerateRecoveryCodes('pw').subscribe((codes) => (regeneratedCodes = codes));
    const regenerate = expectPost(TwoFactorApi.recoveryCodesUrl);
    expect(regenerate.request.body).toEqual({ currentPassword: 'pw' });
    regenerate.flush({ recoveryCodes: ['B'] });

    api.disable('pw').subscribe();
    expect(expectPost(TwoFactorApi.disableUrl).request.body).toEqual({ currentPassword: 'pw' });

    expect(enabledCodes).toEqual(['A']);
    expect(regeneratedCodes).toEqual(['B']);
  });

  it.each([
    [400, 'invalid_code', 'invalid_code'],
    [400, 'invalid_current_password', 'invalid_current_password'],
    [423, 'locked_out', 'locked_out'],
    [409, 'two_factor_already_enabled', 'two_factor_already_enabled'],
    [403, 'two_factor_required', 'two_factor_required'],
    [401, undefined, 'session_expired'],
    [500, 'something_else', 'unknown'],
  ])('maps a %s %s reply to %s', (status, code, kind) => {
    let error: TwoFactorError | undefined;
    api.enable('000000').subscribe({ error: (e: TwoFactorError) => (error = e) });

    expectPost(TwoFactorApi.enableUrl).flush({ error: code, detail: 'The detail.' }, { status, statusText: 'x' });

    expect(error?.kind).toBe(kind);
  });
});
