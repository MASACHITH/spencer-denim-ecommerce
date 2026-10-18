import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { STAFF_PASSWORD_RULE } from '../../core/auth/password-rule';
import { ChangePasswordPage } from './change-password-page';

/** Stands in for the ActivatedRoute the router would give this page when it was reached via `?returnUrl=…`. */
function activatedRouteWithReturnUrl(returnUrl: string | null) {
  return {
    snapshot: { queryParamMap: convertToParamMap(returnUrl ? { returnUrl } : {}) },
  };
}

describe('ChangePasswordPage', () => {
  let controller: HttpTestingController;
  let navigateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ChangePasswordPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
    navigateSpy = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
  });

  afterEach(() => controller.verify());

  function create(mustChangePassword = true): ComponentFixture<ChangePasswordPage> {
    const fixture = TestBed.createComponent(ChangePasswordPage);
    fixture.detectChanges();
    controller.expectOne(AdminAuthApi.whoAmIUrl).flush({
      userId: 1,
      email: 'admin@example.com',
      firstName: 'Ada',
      lastName: null,
      grantsAllPermissions: true,
      permissions: [],
      mustChangePassword,
    });
    fixture.detectChanges();
    return fixture;
  }

  function fill(fixture: ComponentFixture<ChangePasswordPage>, current: string, next: string, confirm = next): void {
    fixture.componentInstance['currentPassword'] = current;
    fixture.componentInstance['newPassword'] = next;
    fixture.componentInstance['confirmPassword'] = confirm;
  }

  function submitWithToken(fixture: ComponentFixture<ChangePasswordPage>) {
    fixture.componentInstance['submit']();
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'the-token' });
    return controller.expectOne(AdminAuthApi.changePasswordUrl);
  }

  /**
   * Rebuilds the testing module with a stand-in ActivatedRoute carrying `?returnUrl=…`, for the tests below that
   * need one. `TestBed.overrideProvider` cannot be used here: the outer `beforeEach` above already instantiated
   * the module (its own `TestBed.inject` calls), and Angular refuses to override a provider after that.
   */
  function configureWithReturnUrl(returnUrl: string): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [ChangePasswordPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: activatedRouteWithReturnUrl(returnUrl) },
      ],
    });
    controller = TestBed.inject(HttpTestingController);
    navigateSpy = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
  }

  it('explains that a temporary password must be replaced when the change is required', () => {
    const fixture = create(true);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('temporary password');
  });

  it('labels every field for assistive technology', () => {
    const root = create().nativeElement as HTMLElement;

    for (const name of ['currentPassword', 'newPassword', 'confirmPassword']) {
      const input = root.querySelector(`input[name="${name}"]`);
      expect(input?.closest('label')?.textContent?.trim().length).toBeGreaterThan(0);
    }
  });

  it('posts both passwords with the antiforgery token and goes to the dashboard on success', async () => {
    const fixture = create();
    fill(fixture, 'Bootstrap-1!', 'Brand-New-2!');

    const request = submitWithToken(fixture);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    expect(request.request.body).toEqual({ currentPassword: 'Bootstrap-1!', newPassword: 'Brand-New-2!' });
    request.flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/');
  });

  it('refuses mismatched new passwords without calling the API', () => {
    const fixture = create();
    fill(fixture, 'Bootstrap-1!', 'Brand-New-2!', 'Something-Else-3!');

    fixture.componentInstance['submit']();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('do not match');
    expect(root.querySelector('input[name="confirmPassword"]')?.getAttribute('aria-invalid')).toBe('true');
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('announces a wrong current password and stays on the page', async () => {
    const fixture = create();
    fill(fixture, 'Wrong-1!', 'Brand-New-2!');

    submitWithToken(fixture).flush(
      { status: 400, error: 'invalid_current_password', detail: 'The current password is incorrect.' },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('current password is incorrect');
    expect(root.querySelector('input[name="currentPassword"]')?.getAttribute('aria-invalid')).toBe('true');
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('lists the password rules the server rejected', async () => {
    const fixture = create();
    fill(fixture, 'Bootstrap-1!', 'short');

    submitWithToken(fixture).flush(
      {
        status: 400,
        error: 'new_password_rejected',
        errors: {
          newPassword: [
            'Use at least 10 characters, including an upper-case letter, a lower-case letter, a number and a symbol.',
            'Passwords must have at least one digit.',
          ],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const items = (fixture.nativeElement as HTMLElement).querySelectorAll('[role="alert"] li');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('at least 10 characters');
    expect(items[1].textContent).toContain('at least one digit');
  });

  // Issue #200: staff passwords now need the same ten characters customers do, and the screen must say so
  // BEFORE the person types — the old hint promised six, which the server now refuses.
  it('states the ten-character rule above the new-password box and ties it to the field', () => {
    const fixture = create();

    const root = fixture.nativeElement as HTMLElement;
    const hint = root.querySelector('#change-rules');
    expect(hint?.textContent?.trim()).toBe(STAFF_PASSWORD_RULE);
    expect(hint?.textContent).toContain('at least 10 characters');
    expect(hint?.textContent).not.toContain('6 characters');
    expect(root.querySelector('input[name="newPassword"]')?.getAttribute('aria-describedby')).toContain('change-rules');
  });

  it('keeps the rule linked to the field while an error is showing', async () => {
    const fixture = create();
    fill(fixture, 'Bootstrap-1!', 'Aa1!xxxxx');

    submitWithToken(fixture).flush(
      {
        status: 400,
        error: 'new_password_rejected',
        errors: { newPassword: [STAFF_PASSWORD_RULE] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const describedBy = (fixture.nativeElement as HTMLElement)
      .querySelector('input[name="newPassword"]')
      ?.getAttribute('aria-describedby');
    expect(describedBy).toBe('change-rules change-error');
  });

  function input(fixture: ComponentFixture<ChangePasswordPage>, name: string): HTMLInputElement {
    return (fixture.nativeElement as HTMLElement).querySelector(`input[name="${name}"]`) as HTMLInputElement;
  }

  async function failWith(fixture: ComponentFixture<ChangePasswordPage>, status: number, body: object) {
    submitWithToken(fixture).flush(body, { status, statusText: 'Error' });
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('links the error only to the field it is about', async () => {
    const fixture = create();
    fill(fixture, 'Wrong-1!', 'Brand-New-2!');

    await failWith(fixture, 400, { error: 'invalid_current_password' });

    expect(input(fixture, 'currentPassword').getAttribute('aria-describedby')).toBe('change-error');
    expect(input(fixture, 'newPassword').getAttribute('aria-describedby')).toBe('change-rules');
    expect(input(fixture, 'newPassword').getAttribute('aria-invalid')).toBeNull();
    expect(input(fixture, 'confirmPassword').getAttribute('aria-describedby')).toBeNull();
  });

  it('describes a rejected new password by both the rules hint and the error', async () => {
    const fixture = create();
    fill(fixture, 'Bootstrap-1!', 'short');

    await failWith(fixture, 400, { error: 'new_password_rejected', errors: { newPassword: ['Too short.'] } });

    expect(input(fixture, 'newPassword').getAttribute('aria-describedby')).toBe('change-rules change-error');
    expect(input(fixture, 'newPassword').getAttribute('aria-invalid')).toBe('true');
    expect(input(fixture, 'currentPassword').getAttribute('aria-describedby')).toBeNull();
  });

  it('links the mismatch error to the confirm field and focuses it', async () => {
    const fixture = create();
    document.body.appendChild(fixture.nativeElement as HTMLElement);
    fill(fixture, 'Bootstrap-1!', 'Brand-New-2!', 'Something-Else-3!');

    fixture.componentInstance['submit']();
    fixture.detectChanges();
    await fixture.whenStable();

    const confirm = input(fixture, 'confirmPassword');
    expect(confirm.getAttribute('aria-describedby')).toBe('change-error');
    expect(document.activeElement).toBe(confirm);
    (fixture.nativeElement as HTMLElement).remove();
  });

  it('moves focus back to the field in error after a server error', async () => {
    const fixture = create();
    document.body.appendChild(fixture.nativeElement as HTMLElement);
    fill(fixture, 'Wrong-1!', 'Brand-New-2!');

    await failWith(fixture, 400, { error: 'invalid_current_password' });

    expect(document.activeElement).toBe(input(fixture, 'currentPassword'));
    (fixture.nativeElement as HTMLElement).remove();
  });

  it('moves focus to the error summary when no single field is at fault', async () => {
    const fixture = create();
    document.body.appendChild(fixture.nativeElement as HTMLElement);
    fill(fixture, 'Bootstrap-1!', 'Brand-New-2!');

    await failWith(fixture, 423, { error: 'locked_out', detail: 'Locked.' });

    expect(document.activeElement?.id).toBe('change-error');
    (fixture.nativeElement as HTMLElement).remove();
  });

  it('goes back to sign-in when the session has ended', async () => {
    const fixture = create();
    fill(fixture, 'Bootstrap-1!', 'Brand-New-2!');

    submitWithToken(fixture).flush(null, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/login');
  });

  // W1-11, #8: the login page carries a `returnUrl` forward onto this page when a forced change came from a
  // route guard redirect, so the visitor lands back where they meant to go once the change is done.
  describe('with a returnUrl', () => {
    it('goes to returnUrl instead of the dashboard once the change succeeds', async () => {
      configureWithReturnUrl('/reports');
      const fixture = create();
      fill(fixture, 'Bootstrap-1!', 'Brand-New-2!');

      submitWithToken(fixture).flush(null, { status: 204, statusText: 'No Content' });
      await fixture.whenStable();

      expect(navigateSpy).toHaveBeenCalledWith('/reports');
    });

    it('never navigates to a returnUrl that is not an in-app path (open-redirect guard)', async () => {
      configureWithReturnUrl('https://evil.example/phish');
      const fixture = create();
      fill(fixture, 'Bootstrap-1!', 'Brand-New-2!');

      submitWithToken(fixture).flush(null, { status: 204, statusText: 'No Content' });
      await fixture.whenStable();

      expect(navigateSpy).toHaveBeenCalledWith('/');
    });
  });
});
