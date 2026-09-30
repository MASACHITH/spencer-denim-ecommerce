import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { LoginPage } from './login-page';

describe('LoginPage', () => {
  let controller: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => controller.verify());

  it('redirects to the dashboard after a successful sign-in', async () => {
    const fixture = TestBed.createComponent(LoginPage);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
    fixture.detectChanges();

    fixture.componentInstance['email'] = 'admin@example.com';
    fixture.componentInstance['password'] = 'correct-password';
    fixture.componentInstance['submit']();

    controller.expectOne(AdminAuthApi.loginUrl).flush({
      userId: 1,
      email: 'admin@example.com',
      firstName: 'Ada',
      lastName: null,
      grantsAllPermissions: true,
      permissions: [],
      mustChangePassword: false,
    });
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/');
  });

  it('goes to the change-password page instead when the account must change its password', async () => {
    const fixture = TestBed.createComponent(LoginPage);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
    fixture.detectChanges();

    fixture.componentInstance['email'] = 'admin@example.com';
    fixture.componentInstance['password'] = 'bootstrap-password';
    fixture.componentInstance['submit']();

    controller.expectOne(AdminAuthApi.loginUrl).flush({
      userId: 1,
      email: 'admin@example.com',
      firstName: 'Ada',
      lastName: null,
      grantsAllPermissions: true,
      permissions: [],
      mustChangePassword: true,
    });
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/change-password');
    expect(navigateSpy).not.toHaveBeenCalledWith('/');
  });

  it('goes to the second sign-in step when the account uses two-factor authentication (W1-10)', async () => {
    const fixture = TestBed.createComponent(LoginPage);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
    fixture.detectChanges();

    fixture.componentInstance['email'] = 'admin@example.com';
    fixture.componentInstance['password'] = 'correct-password';
    fixture.componentInstance['submit']();

    controller.expectOne(AdminAuthApi.loginUrl).flush({ twoFactorRequired: true });
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/login/two-factor');
    expect(navigateSpy).not.toHaveBeenCalledWith('/');
  });

  it('goes to two-factor enrolment when a Super Admin has not set it up yet', async () => {
    const fixture = TestBed.createComponent(LoginPage);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
    fixture.detectChanges();

    fixture.componentInstance['email'] = 'admin@example.com';
    fixture.componentInstance['password'] = 'correct-password';
    fixture.componentInstance['submit']();

    controller.expectOne(AdminAuthApi.loginUrl).flush({
      userId: 1,
      email: 'admin@example.com',
      firstName: 'Ada',
      lastName: null,
      grantsAllPermissions: true,
      permissions: [],
      mustChangePassword: false,
      twoFactorEnabled: false,
      twoFactorEnrolmentRequired: true,
      twoFactorRequired: false,
    });
    await fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledWith('/two-factor/setup');
  });

  it('shows the server error message and does not navigate on failure', async () => {
    const fixture = TestBed.createComponent(LoginPage);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
    fixture.detectChanges();

    fixture.componentInstance['email'] = 'admin@example.com';
    fixture.componentInstance['password'] = 'wrong-password';
    fixture.componentInstance['submit']();

    controller
      .expectOne(AdminAuthApi.loginUrl)
      .flush({ error: 'invalid_credentials', message: 'Invalid email or password.' }, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.login__error')?.textContent).toContain('Invalid email or password.');
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('renders both inputs and a submit button', () => {
    const fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('input[name="email"]')).not.toBeNull();
    expect(root.querySelector('input[name="password"]')).not.toBeNull();
    expect(root.querySelector('button[type="submit"]')).not.toBeNull();
  });
});
