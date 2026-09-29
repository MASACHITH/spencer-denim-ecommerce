import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { ChangePasswordPage } from './change-password-page';

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
        errors: { newPassword: ['Passwords must be at least 6 characters.', 'Passwords must have at least one digit.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const items = (fixture.nativeElement as HTMLElement).querySelectorAll('[role="alert"] li');
    expect(items.length).toBe(2);
    expect(items[1].textContent).toContain('at least one digit');
  });

  it('goes back to sign-in when the session has ended', async () => {
    const fixture = create();
    fill(fixture, 'Bootstrap-1!', 'Brand-New-2!');

    submitWithToken(fixture).flush(null, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/login');
  });
});
