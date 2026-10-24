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

  it('shows the "New staff user" heading and no status section', async () => {
    const fixture = await create();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('New staff user');
    expect(root.querySelector('input[name="initialPassword"]')).not.toBeNull();
    expect(root.textContent).not.toContain('Account status');
  });

  // The server refuses a user with no role, and the form used to offer no way to choose one: pressing Create
  // answered with an error the form never drew, so nothing appeared to happen.
  it('offers the roles to give the new user, with nothing chosen yet', async () => {
    const fixture = await create();

    const roles = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="new-user-roles"]')!;
    expect(roles.textContent).toContain('Choose at least one');
    const boxes = roles.querySelectorAll<HTMLInputElement>('input[type="checkbox"]');
    expect(boxes).toHaveLength(1);
    expect(boxes[0].checked).toBe(false);
    expect(roles.textContent).toContain('Order Staff');
  });

  it('says so in the form, and sends nothing, when no role is chosen', async () => {
    const fixture = await create();

    const component = fixture.componentInstance as unknown as {
      email: { set(v: string): void };
      firstName: { set(v: string): void };
      initialPassword: { set(v: string): void };
      saveProfile(): void;
    };
    component.email.set('new@example.com');
    component.firstName.set('New');
    component.initialPassword.set('Temp-Password-1!');

    component.saveProfile();
    fixture.detectChanges();

    controller.expectNone(UsersApi.usersUrl);
    controller.expectNone('/api/admin/auth/antiforgery-token');
    const error = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="role-error"]');
    expect(error?.textContent).toContain('Choose at least one role');
  });

  it('sends the role that was ticked', async () => {
    const fixture = await create();
    const root = fixture.nativeElement as HTMLElement;

    const box = root.querySelector<HTMLInputElement>('[data-testid="new-user-roles"] input[type="checkbox"]')!;
    box.click();
    fixture.detectChanges();
    expect(box.checked).toBe(true);

    const component = fixture.componentInstance as unknown as {
      email: { set(v: string): void };
      firstName: { set(v: string): void };
      initialPassword: { set(v: string): void };
      saveProfile(): void;
    };
    component.email.set('new@example.com');
    component.firstName.set('New');
    component.initialPassword.set('Temp-Password-1!');
    component.saveProfile();

    flushAntiforgery(controller);
    const request = controller.expectOne(UsersApi.usersUrl);
    expect((request.request.body as { roleIds: number[] }).roleIds).toEqual([5]);
    request.flush(null, { status: 500, statusText: 'Server Error' });
  });

  it('draws a refusal the server names for a field the form has no line for, so none is silent', async () => {
    const fixture = await create();
    const component = fixture.componentInstance as unknown as {
      toggleRole(id: number): void;
      saveProfile(): void;
    };
    component.toggleRole(5);

    component.saveProfile();

    flushAntiforgery(controller);
    controller.expectOne(UsersApi.usersUrl).flush(
      { title: 'One or more fields are invalid.', errors: { lastName: ['Last name is too long.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Last name is too long.');
  });

  it('shows the server’s own sentence when it refuses the roles chosen for the new user', async () => {
    const fixture = await create();
    const component = fixture.componentInstance as unknown as {
      toggleRole(id: number): void;
      saveProfile(): void;
    };
    component.toggleRole(5);

    component.saveProfile();

    flushAntiforgery(controller);
    controller.expectOne(UsersApi.usersUrl).flush(
      { error: 'NotSubsetOfActorPermissions', detail: 'That action grants or affects permissions you do not hold yourself.' },
      { status: 403, statusText: 'Forbidden' },
    );
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('permissions you do not hold yourself');
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
    (component as unknown as { toggleRole(id: number): void }).toggleRole(5);

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
      roleIds: [5],
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

    (fixture.componentInstance as unknown as { toggleRole(id: number): void }).toggleRole(5);
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
    (component as unknown as { toggleRole(id: number): void }).toggleRole(5);

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

  async function create(grantsAll = true, isProtected = false, lockedForViewer = false, isActive = true): Promise<ComponentFixture<UserFormPage>> {
    const fixture = TestBed.createComponent(UserFormPage);
    fixture.detectChanges();

    flushWhoAmI(controller, grantsAll, grantsAll ? [] : ['Users.Manage']);
    controller.expectOne(UsersApi.rolesUrl).flush(oneRole);
    controller.expectOne(`${UsersApi.usersUrl}/7`).flush({
      id: 7,
      email: 'existing@example.com',
      firstName: 'Existing',
      lastName: 'User',
      isActive,
      mustChangePassword: false,
      lastLoginAtUtc: null,
      createdAtUtc: '2026-09-01T00:00:00Z',
      grantsAllPermissions: false,
      roles: [{ id: 5, name: 'Order Staff', grantsAllPermissions: false }],
      effectivePermissions: ['Orders.View'],
      isProtected,
      lockedForViewer,
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

  it('offers no Deactivate button for a protected account, says why, and fixes its email address', async () => {
    const fixture = await create(true, true);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('button.user-form__danger')).toBeNull();
    expect(root.querySelector('[data-testid="protected-note"]')?.textContent).toContain('can never be deactivated');
    expect((root.querySelector('input[name="email"]') as HTMLInputElement).disabled).toBe(true);
    expect(root.textContent).toContain('The email address of a protected account cannot be changed.');
  });

  it('offers no Deactivate button for an active account only the technical owner may change, and says why', async () => {
    const fixture = await create(true, false, true, true);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('button.user-form__danger')).toBeNull();
    expect(root.querySelector('[data-testid="locked-note"]')?.textContent).toContain('Only the technical owner');
  });

  it('offers no Reactivate button for a deactivated account only the technical owner may change', async () => {
    const fixture = await create(true, false, true, false);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('button.user-form__submit[type="button"]')).toBeNull();
    expect(root.textContent).not.toContain('Reactivate');
    expect(root.querySelector('[data-testid="locked-note"]')).not.toBeNull();
  });

  it('fixes the email address of such an account, and does not send it when the profile is saved', async () => {
    const fixture = await create(true, false, true);
    const root = fixture.nativeElement as HTMLElement;

    expect((root.querySelector('input[name="email"]') as HTMLInputElement).disabled).toBe(true);
    expect(root.textContent).toContain("Only the technical owner's account can change the email address of this account.");

    (fixture.componentInstance as unknown as { saveProfile(): void }).saveProfile();
    flushAntiforgery(controller);
    const request = controller.expectOne(`${UsersApi.usersUrl}/7`);
    expect(Object.keys(request.request.body as object)).not.toContain('email');
    request.flush({});
  });

  it('shows the server’s sentence when a deactivation of a guarded account is refused anyway', async () => {
    const fixture = await create();

    (fixture.componentInstance as unknown as { deactivate(): void }).deactivate();
    flushAntiforgery(controller);
    controller.expectOne(`${UsersApi.usersUrl}/7/deactivate`).flush(
      { error: 'guarded_account', detail: "Only the technical owner's account can activate or deactivate this account." },
      { status: 403, statusText: 'Forbidden' },
    );
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain("Only the technical owner's account can activate or deactivate");
  });

  it('offers Deactivate for an ordinary account', async () => {
    const fixture = await create(true, false);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('button.user-form__danger')?.textContent?.trim()).toBe('Deactivate');
    expect(root.querySelector('[data-testid="protected-note"]')).toBeNull();
  });

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
