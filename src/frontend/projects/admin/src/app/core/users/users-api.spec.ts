import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { UsersApi, toStaffUserActionFailure } from './users-api';
import { emptyStaffUserListFilters } from './users.model';

describe('UsersApi', () => {
  let api: UsersApi;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(UsersApi);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('lists users with only page and pageSize when no filters are set', () => {
    api.listUsers(emptyStaffUserListFilters(), 1, 25).subscribe();

    const request = controller.expectOne((r) => r.url === UsersApi.usersUrl);
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe('25');
    expect(request.request.params.has('search')).toBe(false);
    expect(request.request.params.has('roleId')).toBe(false);
    expect(request.request.params.has('isActive')).toBe(false);
    request.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('trims and sends non-blank filters', () => {
    api.listUsers({ search: '  ada  ', roleId: '3', isActive: 'true' }, 2, 10).subscribe();

    const request = controller.expectOne((r) => r.url === UsersApi.usersUrl);
    expect(request.request.params.get('search')).toBe('ada');
    expect(request.request.params.get('roleId')).toBe('3');
    expect(request.request.params.get('isActive')).toBe('true');
    request.flush({ items: [], page: 2, pageSize: 10, totalCount: 0 });
  });

  it('fetches a fresh antiforgery token before creating a user', () => {
    api.createUser({ email: 'new@example.com', firstName: 'New', lastName: null, initialPassword: 'x', roleIds: [1] }).subscribe();

    const tokenRequest = controller.expectOne('/api/admin/auth/antiforgery-token');
    tokenRequest.flush({ token: 'tok-1' });

    const createRequest = controller.expectOne(UsersApi.usersUrl);
    expect(createRequest.request.headers.get('X-XSRF-TOKEN')).toBe('tok-1');
    createRequest.flush({
      id: 5,
      email: 'new@example.com',
      firstName: 'New',
      lastName: null,
      isActive: true,
      mustChangePassword: true,
      lastLoginAtUtc: null,
      createdAtUtc: '2026-09-30T00:00:00Z',
      grantsAllPermissions: false,
      roles: [],
      effectivePermissions: [],
    });
  });

  it('calls the deactivate endpoint with a fresh antiforgery token', () => {
    api.deactivate(5).subscribe();

    controller.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'tok-2' });
    const request = controller.expectOne(`${UsersApi.usersUrl}/5/deactivate`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('tok-2');
    request.flush(null);
  });

  it('calls the reactivate endpoint', () => {
    api.reactivate(5).subscribe();

    controller.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'tok-3' });
    const request = controller.expectOne(`${UsersApi.usersUrl}/5/reactivate`);
    expect(request.request.method).toBe('POST');
    request.flush(null);
  });

  it('lists roles', () => {
    api.listRoles().subscribe();

    controller.expectOne(UsersApi.rolesUrl).flush([]);
  });

  it('gets one role by id', () => {
    api.getRole(2).subscribe();

    controller.expectOne(`${UsersApi.rolesUrl}/2`).flush({
      id: 2,
      name: 'Order Staff',
      description: null,
      isSystem: true,
      grantsAllPermissions: false,
      permissions: ['Orders.View'],
      userCount: 0,
    });
  });
});

describe('toStaffUserActionFailure', () => {
  it('maps a 400 with a field-errors body to validation', () => {
    const failure = toStaffUserActionFailure(fakeError(400, { title: 'Invalid.', errors: { email: ['Email is required.'] } }));
    expect(failure.kind).toBe('validation');
    expect(failure.fieldErrors).toEqual({ email: ['Email is required.'] });
  });

  it('maps a 400 role_not_found body to not_found', () => {
    const failure = toStaffUserActionFailure(fakeError(400, { error: 'role_not_found', unknownRoleIds: [99] }));
    expect(failure.kind).toBe('not_found');
  });

  it('maps a 403 with a known reason to its specific message', () => {
    const failure = toStaffUserActionFailure(fakeError(403, { error: 'NotSubsetOfActorPermissions' }));
    expect(failure.kind).toBe('forbidden');
    expect(failure.reason).toBe('NotSubsetOfActorPermissions');
    expect(failure.message).toContain('permissions you do not hold');
  });

  it('maps a 404 to not_found', () => {
    expect(toStaffUserActionFailure(fakeError(404, null)).kind).toBe('not_found');
  });

  it('maps a 409 email_already_in_use to conflict', () => {
    const failure = toStaffUserActionFailure(fakeError(409, { error: 'email_already_in_use' }));
    expect(failure.kind).toBe('conflict');
    expect(failure.reason).toBe('email_already_in_use');
  });

  it('maps a 409 protected_account to conflict and shows the server sentence', () => {
    const failure = toStaffUserActionFailure(fakeError(409, { error: 'protected_account', detail: 'This account is protected and can never be deactivated.' }));
    expect(failure.kind).toBe('conflict');
    expect(failure.reason).toBe('protected_account');
    expect(failure.message).toBe('This account is protected and can never be deactivated.');
  });

  it('maps a 403 guarded_account to forbidden and shows the server sentence', () => {
    const failure = toStaffUserActionFailure(
      fakeError(403, { error: 'guarded_account', detail: "Only the technical owner's account can activate or deactivate this account." }),
    );
    expect(failure.kind).toBe('forbidden');
    expect(failure.reason).toBe('guarded_account');
    expect(failure.message).toBe("Only the technical owner's account can activate or deactivate this account.");
  });

  it('maps a 409 last_super_admin_guard to conflict with an explanatory message', () => {
    const failure = toStaffUserActionFailure(fakeError(409, { error: 'last_super_admin_guard' }));
    expect(failure.kind).toBe('conflict');
    expect(failure.message).toContain('Super Admin');
  });

  it('maps a status-0 network failure to offline', () => {
    expect(toStaffUserActionFailure(fakeError(0, null)).kind).toBe('offline');
  });

  it('maps a 500 to unknown', () => {
    expect(toStaffUserActionFailure(fakeError(500, null)).kind).toBe('unknown');
  });

  it('maps a non-HTTP error to unknown', () => {
    expect(toStaffUserActionFailure(new Error('boom')).kind).toBe('unknown');
  });
});

function fakeError(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}
