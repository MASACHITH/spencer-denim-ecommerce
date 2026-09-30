import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { UsersApi } from '../../core/users/users-api';
import { StaffUserSummary } from '../../core/users/users.model';
import { UsersListPage } from './users-list-page';

describe('UsersListPage', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [UsersListPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function create(): ComponentFixture<UsersListPage> {
    const fixture = TestBed.createComponent(UsersListPage);
    fixture.detectChanges();
    return fixture;
  }

  function flushWhoAmI(grantsAllPermissions = false, permissions: string[] = []): void {
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

  function flushRoles(): void {
    controller.expectOne(UsersApi.rolesUrl).flush([]);
  }

  function user(overrides: Partial<StaffUserSummary> = {}): StaffUserSummary {
    return {
      id: 1,
      email: 'staff@example.com',
      firstName: 'Staff',
      lastName: 'One',
      isActive: true,
      mustChangePassword: false,
      lastLoginAtUtc: null,
      roles: [{ id: 5, name: 'Order Staff', grantsAllPermissions: false }],
      ...overrides,
    };
  }

  it('requests the first page with no filters on load', () => {
    create();
    flushWhoAmI();
    flushRoles();

    const request = controller.expectOne((r) => r.url === UsersApi.usersUrl);
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe('25');
    request.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('shows a loading state before the response arrives', () => {
    const fixture = create();
    flushWhoAmI();
    flushRoles();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Loading staff users');
    controller.expectOne((r) => r.url === UsersApi.usersUrl).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('renders one row per user with their name, email and roles', () => {
    const fixture = create();
    flushWhoAmI();
    flushRoles();
    controller.expectOne((r) => r.url === UsersApi.usersUrl).flush({ items: [user()], page: 1, pageSize: 25, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('tbody tr').length).toBe(1);
    expect(root.textContent).toContain('staff@example.com');
    expect(root.textContent).toContain('Order Staff');
  });

  it('shows the empty state when no rows match', () => {
    const fixture = create();
    flushWhoAmI();
    flushRoles();
    controller.expectOne((r) => r.url === UsersApi.usersUrl).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No staff users match');
  });

  it('hides the New user link and write actions when the viewer lacks Users.Manage', () => {
    const fixture = create();
    flushWhoAmI(false, []);
    flushRoles();
    controller.expectOne((r) => r.url === UsersApi.usersUrl).flush({ items: [user()], page: 1, pageSize: 25, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('a[href="/users/new"]')).toBeNull();
    expect(root.querySelector('button.users__toggle')).toBeNull();
    expect(root.textContent).toContain('View');
  });

  it('shows the New user link and write actions when the viewer holds Users.Manage', () => {
    const fixture = create();
    flushWhoAmI(false, ['Users.Manage']);
    flushRoles();
    controller.expectOne((r) => r.url === UsersApi.usersUrl).flush({ items: [user()], page: 1, pageSize: 25, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('a[href="/users/new"]')).not.toBeNull();
    expect(root.querySelector('button.users__toggle')?.textContent?.trim()).toBe('Deactivate');
  });

  it('shows a server error state and can retry', () => {
    const fixture = create();
    flushWhoAmI();
    flushRoles();
    controller.expectOne((r) => r.url === UsersApi.usersUrl).flush('error', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('We could not load the staff user list');

    const retryButton = (fixture.nativeElement as HTMLElement).querySelector('button.users__submit') as HTMLButtonElement;
    retryButton.click();
    controller.expectOne((r) => r.url === UsersApi.usersUrl).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });
});
