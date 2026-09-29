import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { TwoFactorLoginPage } from './two-factor-login-page';

describe('TwoFactorLoginPage', () => {
  let controller: HttpTestingController;
  let navigateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TwoFactorLoginPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
    navigateSpy = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
  });

  afterEach(() => controller.verify());

  function create(): ComponentFixture<TwoFactorLoginPage> {
    const fixture = TestBed.createComponent(TwoFactorLoginPage);
    fixture.detectChanges();
    return fixture;
  }

  function codeInput(fixture: ComponentFixture<TwoFactorLoginPage>): HTMLInputElement {
    return (fixture.nativeElement as HTMLElement).querySelector('input[name="code"]') as HTMLInputElement;
  }

  function submit(fixture: ComponentFixture<TwoFactorLoginPage>, code: string) {
    fixture.componentInstance['code'] = code;
    fixture.componentInstance['submit']();
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'the-token' });
    return controller.expectOne(AdminAuthApi.loginTwoFactorUrl);
  }

  function session(overrides: Record<string, unknown> = {}) {
    return {
      userId: 1,
      email: 'admin@example.com',
      firstName: 'Ada',
      lastName: null,
      grantsAllPermissions: true,
      permissions: [],
      mustChangePassword: false,
      twoFactorEnabled: true,
      twoFactorEnrolmentRequired: false,
      twoFactorRequired: false,
      ...overrides,
    };
  }

  it('asks for a one-time code with a labelled numeric field that phones can autofill', () => {
    const input = codeInput(create());

    expect(input.getAttribute('autocomplete')).toBe('one-time-code');
    expect(input.getAttribute('inputmode')).toBe('numeric');
    expect(input.closest('label')?.textContent).toContain('Authentication code');
    expect(input.getAttribute('aria-describedby')).toBe('step-lead');
  });

  it('switches to a recovery code field that autofill leaves alone', () => {
    const fixture = create();

    fixture.componentInstance['toggleMode']();
    fixture.detectChanges();

    const input = codeInput(fixture);
    expect(input.getAttribute('autocomplete')).toBe('off');
    expect(input.getAttribute('inputmode')).toBe('text');
    expect(input.closest('label')?.textContent).toContain('Recovery code');
  });

  it('posts the code as an authenticator code and goes to the dashboard', async () => {
    const fixture = create();

    const request = submit(fixture, '123456');
    expect(request.request.body).toEqual({ code: '123456', isRecoveryCode: false });
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    request.flush(session());
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/');
  });

  it('posts a recovery code as one', () => {
    const fixture = create();
    fixture.componentInstance['toggleMode']();

    const request = submit(fixture, 'ABCDE-FGHIJ');

    expect(request.request.body).toEqual({ code: 'ABCDE-FGHIJ', isRecoveryCode: true });
    request.flush(session());
  });

  it('goes to change-password next when the account still has to change it', async () => {
    const fixture = create();

    submit(fixture, '123456').flush(session({ mustChangePassword: true }));
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/change-password');
  });

  it('marks a wrong code invalid, links the error to it and puts focus back on it', async () => {
    const fixture = create();

    submit(fixture, '000000').flush(
      { error: 'invalid_two_factor_code', message: 'x' },
      { status: 401, statusText: 'Unauthorized' },
    );
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();

    const input = codeInput(fixture);
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('step-lead step-error');
    expect((fixture.nativeElement as HTMLElement).querySelector('#step-error')?.textContent).toContain('not valid');
    expect(document.activeElement).toBe(input);
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('explains an expired sign-in, offers the way back and moves focus to the message', async () => {
    const fixture = create();

    submit(fixture, '123456').flush(
      { error: 'two_factor_session_expired', message: 'x' },
      { status: 401, statusText: 'Unauthorized' },
    );
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    const summary = root.querySelector('#step-error');
    expect(summary?.textContent).toContain('expired');
    expect(summary?.querySelector('a')?.getAttribute('href')).toBe('/login');
    expect(codeInput(fixture).getAttribute('aria-invalid')).toBeNull();
    expect(document.activeElement).toBe(summary);
    expect((root.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('says until when the account is locked', async () => {
    const fixture = create();

    submit(fixture, '000000').flush(
      { error: 'locked_out', message: 'Locked.', lockoutEndUtc: '2026-09-29T12:15:00Z' },
      { status: 423, statusText: 'Locked' },
    );
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('#step-error')?.textContent).toContain('locked until');
  });

  it('sends one request even when submitted twice quickly', () => {
    const fixture = create();
    fixture.componentInstance['code'] = '123456';

    fixture.componentInstance['submit']();
    fixture.componentInstance['submit']();

    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 't' });
    controller.expectOne(AdminAuthApi.loginTwoFactorUrl).flush(session());
  });
});
