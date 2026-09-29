import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { AdminAuthApi, type AdminSession } from './admin-auth-api';
import { adminSessionGuard, signedInGuard, twoFactorSetupGuard } from './auth-guards';

@Component({ template: 'dashboard' })
class DashboardStub {}

@Component({ template: 'change password' })
class ChangePasswordStub {}

@Component({ template: 'login' })
class LoginStub {}

@Component({ template: 'two-factor setup' })
class TwoFactorSetupStub {}

function session(mustChangePassword: boolean, twoFactorEnrolmentRequired = false): AdminSession {
  return {
    userId: 1,
    email: 'admin@example.com',
    firstName: 'Ada',
    lastName: null,
    grantsAllPermissions: true,
    permissions: [],
    mustChangePassword,
    twoFactorEnabled: !twoFactorEnrolmentRequired,
    twoFactorEnrolmentRequired,
  };
}

describe('admin auth guards', () => {
  let controller: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardStub, canActivate: [adminSessionGuard] },
          // Unguarded here, so a redirect to it is one navigation with one who-am-I request.
          { path: 'change-password', component: ChangePasswordStub },
          { path: 'signed-in-only', component: ChangePasswordStub, canActivate: [signedInGuard] },
          { path: 'login', component: LoginStub },
          // Unguarded here for the same reason as change-password.
          { path: 'two-factor/setup', component: TwoFactorSetupStub },
          { path: 'setup-guarded', component: TwoFactorSetupStub, canActivate: [twoFactorSetupGuard] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    controller = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => controller.verify());

  async function navigate(url: string, respond: (request: ReturnType<HttpTestingController['expectOne']>) => void) {
    const harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(url);
    // The router runs guards asynchronously, so let it get as far as the who-am-I request first.
    await new Promise((resolve) => setTimeout(resolve));
    respond(controller.expectOne(AdminAuthApi.whoAmIUrl));
    await navigation.catch(() => undefined);
    return harness;
  }

  it('lets a signed-in user with no pending change reach the dashboard', async () => {
    await navigate('/', (request) => request.flush(session(false)));

    expect(router.url).toBe('/');
  });

  it('sends a user who must change their password to the change-password page instead of the dashboard', async () => {
    const harness = await navigate('/', (request) => request.flush(session(true)));

    expect(router.url).toBe('/change-password');
    expect(harness.routeNativeElement?.textContent).toContain('change password');
  });

  it('sends a Super Admin without two-factor authentication to the enrolment page (W1-10)', async () => {
    const harness = await navigate('/', (request) => request.flush(session(false, true)));

    expect(router.url).toBe('/two-factor/setup');
    expect(harness.routeNativeElement?.textContent).toContain('two-factor setup');
  });

  it('sends the password change first when both steps are pending, as the API orders them', async () => {
    await navigate('/', (request) => request.flush(session(true, true)));

    expect(router.url).toBe('/change-password');
  });

  it('twoFactorSetupGuard lets a user with the password change done reach enrolment', async () => {
    await navigate('/setup-guarded', (request) => request.flush(session(false, true)));

    expect(router.url).toBe('/setup-guarded');
  });

  it('twoFactorSetupGuard sends a user who must still change their password to change-password first', async () => {
    await navigate('/setup-guarded', (request) => request.flush(session(true, true)));

    expect(router.url).toBe('/change-password');
  });

  it('twoFactorSetupGuard keeps a signed-out visitor out', async () => {
    await navigate('/setup-guarded', (request) => request.flush(null, { status: 401, statusText: 'Unauthorized' }));

    expect(router.url).toBe('/login');
  });

  it('sends a signed-out visitor to the sign-in page', async () => {
    await navigate('/', (request) => request.flush(null, { status: 401, statusText: 'Unauthorized' }));

    expect(router.url).toBe('/login');
  });

  it('still shows the dashboard when the API cannot be reached, so its status panel can say so', async () => {
    await navigate('/', (request) => request.error(new ProgressEvent('error')));

    expect(router.url).toBe('/');
  });

  it('signedInGuard lets a user with a pending change through (it guards the change-password page)', async () => {
    await navigate('/signed-in-only', (request) => request.flush(session(true)));

    expect(router.url).toBe('/signed-in-only');
  });

  it('signedInGuard keeps a signed-out visitor out', async () => {
    await navigate('/signed-in-only', (request) => request.flush(null, { status: 401, statusText: 'Unauthorized' }));

    expect(router.url).toBe('/login');
  });
});
