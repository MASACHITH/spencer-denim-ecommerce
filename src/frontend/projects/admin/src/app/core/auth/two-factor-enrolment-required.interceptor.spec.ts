import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { twoFactorEnrolmentRequiredInterceptor } from './two-factor-enrolment-required.interceptor';

describe('twoFactorEnrolmentRequiredInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  let router: Router;
  let navigateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([twoFactorEnrolmentRequiredInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    navigateSpy = vi.spyOn(router, 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
  });

  afterEach(() => controller.verify());

  it('sends the user to enrolment on a 403 two_factor_enrolment_required, and still fails the call', () => {
    let failed = false;
    http.get('/api/admin/anything').subscribe({ error: () => (failed = true) });

    controller
      .expectOne('/api/admin/anything')
      .flush({ status: 403, error: 'two_factor_enrolment_required' }, { status: 403, statusText: 'Forbidden' });

    expect(navigateSpy).toHaveBeenCalledWith('/two-factor/setup');
    expect(failed).toBe(true);
  });

  it('does not navigate again when the user is already on the enrolment page', () => {
    vi.spyOn(router, 'url', 'get').mockReturnValue('/two-factor/setup');
    http.get('/api/admin/anything').subscribe({ error: () => undefined });

    controller
      .expectOne('/api/admin/anything')
      .flush({ status: 403, error: 'two_factor_enrolment_required' }, { status: 403, statusText: 'Forbidden' });

    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('leaves the password-change code and an ordinary 403 to others', () => {
    http.get('/api/admin/one').subscribe({ error: () => undefined });
    http.get('/api/admin/two').subscribe({ error: () => undefined });

    controller
      .expectOne('/api/admin/one')
      .flush({ status: 403, error: 'password_change_required' }, { status: 403, statusText: 'Forbidden' });
    controller.expectOne('/api/admin/two').flush(null, { status: 403, statusText: 'Forbidden' });

    expect(navigateSpy).not.toHaveBeenCalled();
  });
});
