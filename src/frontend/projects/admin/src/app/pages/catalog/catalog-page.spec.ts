import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminCatalogApi } from '../../core/catalog/admin-catalog-api';
import { AdminCategoryTree, AdminDepartment } from '../../core/catalog/admin-catalog.model';
import { CatalogPage } from './catalog-page';

describe('CatalogPage', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CatalogPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function department(overrides: Partial<AdminDepartment> = {}): AdminDepartment {
    return {
      id: 1,
      name: 'Women',
      slug: 'women',
      description: null,
      heroImageAssetId: null,
      sortOrder: 0,
      isActive: true,
      seoTitle: null,
      seoDescription: null,
      rowVersion: 'rv-1',
      categoryCount: 10,
      storefrontProductCount: 20,
      ...overrides,
    };
  }

  const departments: AdminDepartment[] = [
    department(),
    department({ id: 2, name: 'Men', slug: 'men', sortOrder: 1, rowVersion: 'rv-2' }),
    department({
      id: 3,
      name: 'Kids',
      slug: 'kids',
      sortOrder: 2,
      rowVersion: 'rv-3',
      isActive: false,
      storefrontProductCount: 10,
    }),
  ];

  const emptyTree: AdminCategoryTree = {
    departmentId: 1,
    departmentName: 'Women',
    departmentSlug: 'women',
    departmentIsActive: true,
    categories: [],
  };

  /** Loads the page: the department list, then the first department's tree. */
  function create(rows: AdminDepartment[] = departments): ComponentFixture<CatalogPage> {
    const fixture = TestBed.createComponent(CatalogPage);
    fixture.detectChanges();

    controller.expectOne(AdminCatalogApi.departmentsUrl).flush(rows);
    fixture.detectChanges();

    if (rows.length > 0) {
      controller
        .expectOne(`${AdminCatalogApi.departmentsUrl}/${rows[0].id}/categories`)
        .flush(emptyTree);
      fixture.detectChanges();
    }

    return fixture;
  }

  function text(fixture: ComponentFixture<CatalogPage>): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  function button(fixture: ComponentFixture<CatalogPage>, testId: string): HTMLButtonElement {
    const element = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      `[data-testid="${testId}"]`,
    );
    if (!element) {
      throw new Error(`no [data-testid="${testId}"] on the page`);
    }

    return element;
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
  }

  /**
   * Answers the reads the page makes after a write: it reloads the department list, and loading that list
   * opens the selected department's tree, which is a second read. Draining them in a loop keeps each test
   * about the write it is testing — and `controller.verify()` in `afterEach` still fails a test that fired
   * a request nothing here answered.
   */
  function settle(fixture: ComponentFixture<CatalogPage>, rows: AdminDepartment[] = departments): void {
    for (let round = 0; round < 5; round++) {
      const pending = controller.match((request) => request.method === 'GET');
      if (pending.length === 0) {
        return;
      }

      for (const request of pending) {
        if (request.request.url === AdminCatalogApi.departmentsUrl) {
          request.flush(rows);
        } else if (request.request.url.endsWith('/categories')) {
          request.flush(emptyTree);
        } else {
          throw new Error(`unexpected read: ${request.request.url}`);
        }
      }

      fixture.detectChanges();
    }
  }

  it('shows a loading state before the departments arrive', () => {
    const fixture = TestBed.createComponent(CatalogPage);
    fixture.detectChanges();

    expect(text(fixture)).toContain('Loading the catalogue structure');
    controller.expectOne(AdminCatalogApi.departmentsUrl).flush([]);
  });

  /**
   * The whole reason this screen reads its own API: the storefront's reader drops a deactivated department,
   * so a screen built on that payload could never show one, let alone switch it back on.
   */
  it('lists a deactivated department, marked as hidden', () => {
    const fixture = create();

    expect(text(fixture)).toContain('Kids');
    expect(button(fixture, 'dept-toggle-3').textContent).toContain('Show');
    expect(button(fixture, 'dept-toggle-1').textContent).toContain('Hide');
  });

  it('shows each department with its own counts', () => {
    const fixture = create();

    expect(text(fixture)).toContain('10 categories');
    expect(text(fixture)).toContain('20 published products');
  });

  it('opens the first department’s categories without being asked', () => {
    const fixture = create();

    expect(text(fixture)).toContain('Categories in Women');
  });

  it('loads another department’s tree when its button is pressed', () => {
    const fixture = create();

    button(fixture, 'select-2').click();
    fixture.detectChanges();

    controller
      .expectOne(`${AdminCatalogApi.departmentsUrl}/2/categories`)
      .flush({ ...emptyTree, departmentId: 2, departmentName: 'Men', departmentSlug: 'men' });
    fixture.detectChanges();

    expect(text(fixture)).toContain('Categories in Men');
  });

  // ---- reorder ---------------------------------------------------------------------------------

  /**
   * A move is staged, not sent. The API takes a whole list, and one request per keypress would make every
   * intermediate arrangement an audit row — and a chance to collide with another admin mid-rearrangement.
   */
  it('stages a move without sending anything, then sends the whole list once', () => {
    const fixture = create();

    button(fixture, 'dept-down-1').click();
    fixture.detectChanges();

    // Nothing has been sent: `controller.verify()` in afterEach would fail if it had.
    expect(text(fixture)).toContain('The navigation order is not saved yet');

    button(fixture, 'save-dept-order').click();
    fixture.detectChanges();

    grantToken();
    const request = controller.expectOne(`${AdminCatalogApi.departmentsUrl}/order`);
    expect(request.request.body).toEqual({ orderedDepartmentIds: [2, 1, 3] });
    request.flush(null);
    fixture.detectChanges();

    // And the list is reloaded, so the screen shows what the server now holds rather than what was staged.
    settle(fixture, [departments[1], departments[0], departments[2]]);

    expect(text(fixture)).not.toContain('not saved yet');
    expect(button(fixture, 'dept-up-2').disabled).toBe(true);
  });

  it('discards a staged order without sending anything', () => {
    const fixture = create();

    button(fixture, 'dept-down-1').click();
    fixture.detectChanges();
    expect(text(fixture)).toContain('not saved yet');

    button(fixture, 'discard-dept-order').click();
    fixture.detectChanges();

    expect(text(fixture)).not.toContain('not saved yet');
  });

  it('cannot move the first department up or the last one down', () => {
    const fixture = create();

    expect(button(fixture, 'dept-up-1').disabled).toBe(true);
    expect(button(fixture, 'dept-down-3').disabled).toBe(true);
    expect(button(fixture, 'dept-down-1').disabled).toBe(false);
  });

  /**
   * A keyboard user has to be able to press the same key twice. If focus were lost on the first move, the
   * second one would need a fresh Tab walk down the list.
   */
  it('keeps focus on the button that moved a department, so the move can be repeated', async () => {
    const fixture = create();

    const down = button(fixture, 'dept-down-1');
    down.focus();
    down.click();
    fixture.detectChanges();
    await new Promise((resolve) => queueMicrotask(() => resolve(null)));

    expect(document.activeElement).toBe(button(fixture, 'dept-down-1'));
  });

  /**
   * At the end of a list the button that was pressed becomes disabled, and a disabled button cannot hold
   * focus — so focus moves to the one control on that row that still does something.
   */
  it('moves focus to the other direction when the pressed button runs out of list', async () => {
    const fixture = create();

    button(fixture, 'dept-up-2').focus();
    button(fixture, 'dept-up-2').click();
    fixture.detectChanges();
    await new Promise((resolve) => queueMicrotask(() => resolve(null)));

    expect(button(fixture, 'dept-up-2').disabled).toBe(true);
    expect(document.activeElement).toBe(button(fixture, 'dept-down-2'));
  });

  it('announces a move for a screen reader, with the position and what to do next', () => {
    const fixture = create();

    button(fixture, 'dept-down-1').click();
    fixture.detectChanges();

    const status = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
    expect(status?.textContent).toContain('Women moved to position 2 of 3');
    expect(status?.textContent).toContain('Save order');
  });

  // ---- editing ---------------------------------------------------------------------------------

  it('edits a department and reloads the list afterwards', async () => {
    const fixture = create();

    button(fixture, 'dept-edit-1').click();
    fixture.detectChanges();

    // NgModel writes the control's value on a microtask, so the box is empty until the queue drains.
    await Promise.resolve();
    fixture.detectChanges();

    const slug = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '[data-testid="dept-field-slug"]',
    )!;
    expect(slug.value).toBe('women');

    button(fixture, 'dept-save').click();
    fixture.detectChanges();

    grantToken();
    const request = controller.expectOne(`${AdminCatalogApi.departmentsUrl}/1`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.rowVersion).toBe('rv-1');
    request.flush(null);
    fixture.detectChanges();
    settle(fixture);
  });

  it('shows a rejected slug against the slug box rather than as a page error', () => {
    const fixture = create();

    button(fixture, 'dept-edit-1').click();
    fixture.detectChanges();

    button(fixture, 'dept-save').click();
    fixture.detectChanges();
    grantToken();

    controller.expectOne(`${AdminCatalogApi.departmentsUrl}/1`).flush(
      {
        title: 'One or more fields are invalid.',
        errors: { slug: ['The URL slug may contain only lower-case letters, digits and single hyphens.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    const message = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="dept-slug-error"]',
    );
    expect(message?.textContent).toContain('lower-case');
    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('[data-testid="dept-field-slug"]')
        ?.getAttribute('aria-invalid'),
    ).toBe('true');
  });

  /**
   * A stale row version means "reload", not "fix a field", so it must not land on a control as if the admin
   * had typed something wrong.
   */
  it('shows a stale row version as a page-level conflict with no field blamed', () => {
    const fixture = create();

    button(fixture, 'dept-edit-1').click();
    fixture.detectChanges();
    button(fixture, 'dept-save').click();
    fixture.detectChanges();
    grantToken();

    controller.expectOne(`${AdminCatalogApi.departmentsUrl}/1`).flush(
      { detail: 'Somebody else changed this while you were editing it.', code: 'stale_row_version' },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="page-error"]')?.textContent,
    ).toContain('Somebody else changed this');
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="dept-slug-error"]'),
    ).toBeNull();
  });

  it('switches a department off through its own route, not the edit form', () => {
    const fixture = create();

    button(fixture, 'dept-toggle-1').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(`${AdminCatalogApi.departmentsUrl}/1/active`);
    expect(request.request.body).toEqual({ isActive: false, rowVersion: 'rv-1' });
    request.flush(null);
    fixture.detectChanges();
    settle(fixture);
  });

  it('shows the refusal and no retry button when the user lacks the permission', () => {
    const fixture = TestBed.createComponent(CatalogPage);
    fixture.detectChanges();

    controller
      .expectOne(AdminCatalogApi.departmentsUrl)
      .flush({ detail: 'nope' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const error = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="page-error"]');
    expect(error?.textContent).toContain('permission');
    expect(error?.querySelector('button')).toBeNull();
  });
});
