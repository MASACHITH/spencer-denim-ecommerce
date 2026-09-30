import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { passwordChangeRequiredInterceptor } from './password-change-required.interceptor';

describe('passwordChangeRequiredInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  let navigateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([passwordChangeRequiredInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
    navigateSpy = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
  });

  afterEach(() => controller.verify());

  it('sends the user to the change-password page on a 403 password_change_required, and still fails the call', () => {
    let failed = false;
    http.get('/api/admin/anything').subscribe({ error: () => (failed = true) });

    controller
      .expectOne('/api/admin/anything')
      .flush({ status: 403, error: 'password_change_required' }, { status: 403, statusText: 'Forbidden' });

    expect(navigateSpy).toHaveBeenCalledWith('/change-password');
    expect(failed).toBe(true);
  });

  it('leaves an ordinary 403 (missing permission) alone', () => {
    http.get('/api/admin/anything').subscribe({ error: () => undefined });

    controller.expectOne('/api/admin/anything').flush(null, { status: 403, statusText: 'Forbidden' });

    expect(navigateSpy).not.toHaveBeenCalled();
  });
});
