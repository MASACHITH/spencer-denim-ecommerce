import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { LoginPage } from './login-page';

/** Stands in for the ActivatedRoute the router would give this page when it was reached via `?returnUrl=…`. */
function activatedRouteWithReturnUrl(returnUrl: string | null) {
  return {
    snapshot: { queryParamMap: convertToParamMap(returnUrl ? { returnUrl } : {}) },
  };
}

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

  /**
   * Rebuilds the testing module with a stand-in ActivatedRoute carrying `?returnUrl=…`, for the tests below
   * that need one. `TestBed.overrideProvider` cannot be used here: the outer `beforeEach` above already
   * instantiated the module (its own `TestBed.inject` calls), and Angular refuses to override a provider
   * after that.
   */
  function configureWithReturnUrl(returnUrl: string): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: activatedRouteWithReturnUrl(returnUrl) },
      ],
    });
    controller = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  }

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

  // W1-11, #8: the route guard sends a signed-out visitor here with `?returnUrl=…` so they land back on the
  // admin page they originally meant to reach, instead of always on the dashboard.
  describe('with a returnUrl', () => {
    it('goes to returnUrl instead of the dashboard after a successful sign-in', async () => {
      configureWithReturnUrl('/reports');
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

      expect(navigateSpy).toHaveBeenCalledWith('/reports');
    });

    it('carries returnUrl forward onto the change-password page when a change is forced first', async () => {
      configureWithReturnUrl('/reports');
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

      expect(navigateSpy).toHaveBeenCalledWith('/change-password?returnUrl=%2Freports');
    });

    it('never sign-in navigates to a returnUrl that is not an in-app path (open-redirect guard)', async () => {
      configureWithReturnUrl('https://evil.example/phish');
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
  });
});
