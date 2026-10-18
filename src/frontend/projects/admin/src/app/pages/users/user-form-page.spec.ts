import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { STAFF_PASSWORD_RULE } from '../../core/auth/password-rule';
import { UsersApi } from '../../core/users/users-api';
import { UserFormPage } from './user-form-page';

function activatedRouteWithId(id: string | null): ActivatedRoute {
  return { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } as unknown as ActivatedRoute;
}

function flushWhoAmI(controller: HttpTestingController, grantsAllPermissions = true, permissions: string[] = []): void {
  controller.expectOne(AdminAuthApi.whoAmIUrl).flush({
    userId: 1,
    email: 'viewer@example.com',
    firstName: 'Viewer',
    lastName: null,
    grantsAllPermissions,
    permissions,
    mustChangePassword: false,
  });
}

const oneRole = [{ id: 5, name: 'Order Staff', description: null, isSystem: true, grantsAllPermissions: false, permissionCount: 1, userCount: 0 }];

/** Every mutating UsersApi call fetches a fresh antiforgery token first (see users-api.ts). */
function flushAntiforgery(controller: HttpTestingController): void {
  controller.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'test-token' });
}

describe('UserFormPage (create mode)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [UserFormPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: activatedRouteWithId(null) },
      ],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  async function create(): Promise<ComponentFixture<UserFormPage>> {
    const fixture = TestBed.createComponent(UserFormPage);
    fixture.detectChanges();

    flushWhoAmI(controller);
    controller.expectOne(UsersApi.rolesUrl).flush(oneRole);
    controller.expectOne(`${UsersApi.rolesUrl}/5`).flush({
      id: 5,
      name: 'Order Staff',
      description: null,
      isSystem: true,
      grantsAllPermissions: false,
      permissions: ['Orders.View'],
      userCount: 0,
    });

    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('shows the "New staff user" heading and no roles/status sections', async () => {
    const fixture = await create();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('New staff user');
    expect(root.querySelector('input[name="initialPassword"]')).not.toBeNull();
    expect(root.textContent).not.toContain('Account status');
  });

  it('submits a create request with the entered fields', async () => {
    const fixture = await create();

    const component = fixture.componentInstance as unknown as {
      email: { set(v: string): void };
      firstName: { set(v: string): void };
      lastName: { set(v: string): void };
      initialPassword: { set(v: string): void };
      saveProfile(): void;
    };
    component.email.set('new@example.com');
    component.firstName.set('New');
    component.lastName.set('Staff');
    component.initialPassword.set('Temp-Password-1!');

    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigate').mockImplementation(() => Promise.resolve(true));

    component.saveProfile();

    flushAntiforgery(controller);
    const request = controller.expectOne(UsersApi.usersUrl);
    expect(request.request.body).toEqual({
      email: 'new@example.com',
      firstName: 'New',
      lastName: 'Staff',
      initialPassword: 'Temp-Password-1!',
      roleIds: [],
    });
    request.flush({
      id: 9,
      email: 'new@example.com',
      firstName: 'New',
      lastName: 'Staff',
      isActive: true,
      mustChangePassword: true,
      lastLoginAtUtc: null,
      createdAtUtc: '2026-09-30T00:00:00Z',
      grantsAllPermissions: false,
      roles: [],
      effectivePermissions: [],
    });

    expect(navigateSpy).toHaveBeenCalledWith(['/users', 9]);
  });

  it('shows field-level validation errors from the API', async () => {
    const fixture = await create();

    (fixture.componentInstance as unknown as { saveProfile(): void }).saveProfile();

    flushAntiforgery(controller);
    controller.expectOne(UsersApi.usersUrl).flush(
      { title: 'One or more fields are invalid.', errors: { email: ['Email is required.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Email is required.');
  });

  // Issue #200: the initial password needs the same ten characters a customer's does, and the form says so
  // before anything is typed (it used to state no rule at all, so the admin learned it from a refusal).
  it('states the password rule beside the initial password and ties it to the field', async () => {
    const fixture = await create();

    const root = fixture.nativeElement as HTMLElement;
    const hint = root.querySelector('#initial-password-rule');
    expect(hint?.textContent?.trim()).toBe(STAFF_PASSWORD_RULE);
    expect(hint?.textContent).toContain('at least 10 characters');
    expect(root.querySelector('input[name="initialPassword"]')?.getAttribute('aria-describedby')).toBe(
      'initial-password-rule',
    );
  });

  it('shows the rule the API refused the initial password with, under the form', async () => {
    const fixture = await create();

    const component = fixture.componentInstance as unknown as {
      email: { set(v: string): void };
      firstName: { set(v: string): void };
      initialPassword: { set(v: string): void };
      saveProfile(): void;
    };
    component.email.set('new@example.com');
    component.firstName.set('New');
    component.initialPassword.set('Aa1!xxxxx');

    component.saveProfile();

    flushAntiforgery(controller);
    controller.expectOne(UsersApi.usersUrl).flush(
      {
        title: 'The initial password does not meet the password rules.',
        errors: { initialPassword: [STAFF_PASSWORD_RULE] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    const alerts = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('[role="alert"]')).map(
      (element) => element.textContent,
    );
    expect(alerts.some((text) => text?.includes('at least 10 characters'))).toBe(true);
  });
});

describe('UserFormPage (edit mode)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [UserFormPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: activatedRouteWithId('7') },
      ],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  async function create(grantsAll = true): Promise<ComponentFixture<UserFormPage>> {
    const fixture = TestBed.createComponent(UserFormPage);
    fixture.detectChanges();

    flushWhoAmI(controller, grantsAll, grantsAll ? [] : ['Users.Manage']);
    controller.expectOne(UsersApi.rolesUrl).flush(oneRole);
    controller.expectOne(`${UsersApi.usersUrl}/7`).flush({
      id: 7,
      email: 'existing@example.com',
      firstName: 'Existing',
      lastName: 'User',
      isActive: true,
      mustChangePassword: false,
      lastLoginAtUtc: null,
      createdAtUtc: '2026-09-01T00:00:00Z',
      grantsAllPermissions: false,
      roles: [{ id: 5, name: 'Order Staff', grantsAllPermissions: false }],
      effectivePermissions: ['Orders.View'],
    });
    controller.expectOne(`${UsersApi.rolesUrl}/5`).flush({
      id: 5,
      name: 'Order Staff',
      description: null,
      isSystem: true,
      grantsAllPermissions: false,
      permissions: ['Orders.View'],
      userCount: 1,
    });

    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('shows "Edit staff user" and pre-fills the profile fields and roles', async () => {
    const fixture = await create();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.textContent).toContain('Edit staff user');
    expect((root.querySelector('input[name="email"]') as HTMLInputElement).value).toBe('existing@example.com');
    expect((root.querySelector('input[name="firstName"]') as HTMLInputElement).value).toBe('Existing');
    expect((root.querySelector('input[type="checkbox"]') as HTMLInputElement).checked).toBe(true);
    expect(root.textContent).toContain('Account status');
    expect(root.textContent).toContain('active');
  });

  it('disables the email field for a viewer without GrantsAll', async () => {
    const fixture = await create(false);
    const root = fixture.nativeElement as HTMLElement;

    expect((root.querySelector('input[name="email"]') as HTMLInputElement).disabled).toBe(true);
    expect(root.textContent).toContain("Only a Super Admin can change a staff user's email");
  });

  it('sends the roles form as its own request', async () => {
    const fixture = await create();

    (fixture.componentInstance as unknown as { saveRoles(): void }).saveRoles();

    flushAntiforgery(controller);
    const request = controller.expectOne(`${UsersApi.usersUrl}/7/roles`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ roleIds: [5] });
    request.flush(null);
  });

  it('deactivates the user and flips the displayed status', async () => {
    const fixture = await create();

    (fixture.componentInstance as unknown as { deactivate(): void }).deactivate();

    flushAntiforgery(controller);
    controller.expectOne(`${UsersApi.usersUrl}/7/deactivate`).flush(null);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('deactivated');
  });

  it('shows a conflict message when the last-Super-Admin guard rejects a deactivation', async () => {
    const fixture = await create();

    (fixture.componentInstance as unknown as { deactivate(): void }).deactivate();

    flushAntiforgery(controller);
    controller.expectOne(`${UsersApi.usersUrl}/7/deactivate`).flush(
      { error: 'last_super_admin_guard' },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('no active Super Admin');
  });
});
