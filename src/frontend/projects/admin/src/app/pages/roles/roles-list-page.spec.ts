import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { UsersApi } from '../../core/users/users-api';
import { RoleSummary } from '../../core/users/users.model';
import { RolesListPage } from './roles-list-page';

describe('RolesListPage', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RolesListPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function create(): ComponentFixture<RolesListPage> {
    const fixture = TestBed.createComponent(RolesListPage);
    fixture.detectChanges();
    return fixture;
  }

  function role(overrides: Partial<RoleSummary> = {}): RoleSummary {
    return {
      id: 2,
      name: 'Order Staff',
      description: 'Order processing without refunds.',
      isSystem: true,
      grantsAllPermissions: false,
      permissionCount: 6,
      userCount: 3,
      ...overrides,
    };
  }

  it('shows a loading state before the response arrives', () => {
    const fixture = create();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Loading roles');
    controller.expectOne(UsersApi.rolesUrl).flush([]);
  });

  it('lists each role with its permission and user counts', () => {
    const fixture = create();
    controller.expectOne(UsersApi.rolesUrl).flush([role()]);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Order Staff');
    expect(root.textContent).toContain('6 permissions');
    expect(root.textContent).toContain('3 users');
  });

  it('shows the empty state when there are no roles', () => {
    const fixture = create();
    controller.expectOne(UsersApi.rolesUrl).flush([]);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No roles are defined yet');
  });

  it('fetches and shows the full permission list only when a row is expanded', () => {
    const fixture = create();
    controller.expectOne(UsersApi.rolesUrl).flush([role()]);
    fixture.detectChanges();

    controller.expectNone(`${UsersApi.rolesUrl}/2`);

    const toggle = (fixture.nativeElement as HTMLElement).querySelector('button.roles__row') as HTMLButtonElement;
    toggle.click();
    fixture.detectChanges();

    controller.expectOne(`${UsersApi.rolesUrl}/2`).flush({
      id: 2,
      name: 'Order Staff',
      description: 'Order processing without refunds.',
      isSystem: true,
      grantsAllPermissions: false,
      permissions: ['Orders.View', 'Orders.Edit'],
      userCount: 3,
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Orders.View');
    expect(root.textContent).toContain('Orders.Edit');
  });

  it('shows every permission grant for a GrantsAll role without fetching a permission list', () => {
    const fixture = create();
    controller.expectOne(UsersApi.rolesUrl).flush([role({ id: 1, name: 'Super Admin', grantsAllPermissions: true, permissionCount: 0 })]);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('All permissions');

    const toggle = (fixture.nativeElement as HTMLElement).querySelector('button.roles__row') as HTMLButtonElement;
    toggle.click();
    fixture.detectChanges();

    controller.expectOne(`${UsersApi.rolesUrl}/1`).flush({
      id: 1,
      name: 'Super Admin',
      description: null,
      isSystem: true,
      grantsAllPermissions: true,
      permissions: [],
      userCount: 1,
    });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Grants every permission');
  });
});
