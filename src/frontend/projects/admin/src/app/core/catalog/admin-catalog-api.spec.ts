import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminCatalogApi, toCatalogFailure } from './admin-catalog-api';
import { emptyCategoryForm } from './admin-catalog.model';

describe('AdminCatalogApi', () => {
  let api: AdminCatalogApi;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    api = TestBed.inject(AdminCatalogApi);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  /** Answers the antiforgery fetch every mutating call makes first (doc 03 §3a). */
  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf-token' });
  }

  it('reads the departments from the admin route, not the storefront one', () => {
    api.departments().subscribe();

    const request = controller.expectOne(AdminCatalogApi.departmentsUrl);
    expect(request.request.method).toBe('GET');
    expect(request.request.url).toBe('/api/admin/catalog/departments');
    request.flush([]);
  });

  it('reads one department tree', () => {
    api.categoryTree(7).subscribe();

    controller.expectOne('/api/admin/catalog/departments/7/categories').flush({});
  });

  it('reads the deactivation impact before the switch is thrown', () => {
    api.deactivationImpact(42).subscribe();

    controller.expectOne('/api/admin/catalog/categories/42/deactivation-impact').flush({});
  });

  /**
   * Every mutating admin call carries a freshly fetched token. Without this the write is a 400 the admin
   * cannot do anything about, and an interceptor-free service is where that is easiest to forget.
   */
  it('fetches an antiforgery token before every write and sends it in the header', () => {
    api.createCategory(1, emptyCategoryForm()).subscribe();
    grantToken();

    const request = controller.expectOne(AdminCatalogApi.categoriesUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');
    request.flush({ id: 9 });
  });

  it('sends a category edit as a PUT with the row version in the body', () => {
    api.updateCategory(5, emptyCategoryForm(2), 'rv-5').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/catalog/categories/5');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.rowVersion).toBe('rv-5');
    expect(request.request.body.parentCategoryId).toBe(2);
    request.flush(null);
  });

  /**
   * A DELETE's precondition belongs in `If-Match` rather than a body, which is also what
   * `Category.RowVersion`'s own documentation calls the token.
   */
  it('sends a removal as a DELETE with the row version in If-Match', () => {
    api.removeCategory(5, 'rv-5').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/catalog/categories/5');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"rv-5"');
    request.flush(null);
  });

  it('sends a reorder as the whole list for one parent', () => {
    api.reorderCategories(1, 10, [3, 2, 1]).subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/catalog/categories/order');
    expect(request.request.body).toEqual({
      departmentId: 1,
      parentCategoryId: 10,
      orderedCategoryIds: [3, 2, 1],
    });
    request.flush(null);
  });

  it('sends a department reorder as the whole list', () => {
    api.reorderDepartments([3, 1, 2]).subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/catalog/departments/order');
    expect(request.request.body).toEqual({ orderedDepartmentIds: [3, 1, 2] });
    request.flush(null);
  });

  it('sends the active switch to its own route rather than the edit route', () => {
    api.setCategoryActive(5, false, 'rv-5').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/catalog/categories/5/active');
    expect(request.request.body).toEqual({ isActive: false, rowVersion: 'rv-5' });
    request.flush(null);
  });
});

describe('toCatalogFailure', () => {
  function response(status: number, body: unknown): HttpErrorResponse {
    return new HttpErrorResponse({ status, error: body, url: '/api/admin/catalog/categories' });
  }

  it('reads a 400 validation body as field errors the screen can attach to a control', () => {
    const failure = toCatalogFailure(
      response(400, {
        title: 'One or more fields are invalid.',
        errors: { slug: ['The URL slug may contain only lower-case letters…'] },
      }),
    );

    expect(failure.kind).toBe('validation');
    expect(failure.fieldErrors?.['slug'][0]).toContain('lower-case');
  });

  /**
   * A conflict is not a field problem, and telling an admin to "correct a field" when what they need to do
   * is reload is the mistake this classification exists to prevent.
   */
  it('reads a 409 as a conflict, with the code and the counts behind it', () => {
    const failure = toCatalogFailure(
      response(409, {
        detail: "'Skirts' still has 4 products in it.",
        code: 'category_in_use',
        linkedProducts: 4,
        childCategories: 0,
      }),
    );

    expect(failure.kind).toBe('conflict');
    expect(failure.code).toBe('category_in_use');
    expect(failure.message).toContain('4 products');
    expect(failure.details?.['linkedProducts']).toBe(4);
  });

  it('reads a stale row version as a conflict with its own code', () => {
    const failure = toCatalogFailure(
      response(409, { detail: 'Somebody else changed this…', code: 'stale_row_version' }),
    );

    expect(failure.kind).toBe('conflict');
    expect(failure.code).toBe('stale_row_version');
  });

  it('reads a 403 as a permission problem without leaking the body', () => {
    const failure = toCatalogFailure(response(403, { detail: 'nope' }));

    expect(failure.kind).toBe('forbidden');
    expect(failure.message).toContain('permission');
  });

  it('reads a 404 as something that has gone', () => {
    expect(toCatalogFailure(response(404, { detail: 'No category has the id 5.', code: 'not_found' })).kind).toBe(
      'notFound',
    );
  });

  it('reads status 0 as offline rather than as a server fault', () => {
    expect(toCatalogFailure(response(0, null)).kind).toBe('offline');
  });

  it('reads a malformed-body 400 with no errors dictionary as its detail', () => {
    const failure = toCatalogFailure(
      response(400, { detail: 'The request body is not valid JSON…', code: 'malformed_json' }),
    );

    expect(failure.kind).toBe('unknown');
    expect(failure.message).toContain('not valid JSON');
  });

  it('reads anything that is not an HTTP error as unknown', () => {
    expect(toCatalogFailure(new Error('boom')).kind).toBe('unknown');
  });
});
