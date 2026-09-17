import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { API_BASE_URL, apiBaseUrlInterceptor } from './api-base-url';

function setup(baseUrl?: string) {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([apiBaseUrlInterceptor])),
      provideHttpClientTesting(),
      ...(baseUrl === undefined ? [] : [{ provide: API_BASE_URL, useValue: baseUrl }]),
    ],
  });
  return { http: TestBed.inject(HttpClient), controller: TestBed.inject(HttpTestingController) };
}

describe('apiBaseUrlInterceptor', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('keeps relative API URLs unchanged in the browser (no base URL)', () => {
    const { http, controller } = setup();

    http.get('/api/ping').subscribe();

    controller.expectOne('/api/ping').flush({});
  });

  it('prefixes relative /api URLs with the configured base URL, without a double slash', () => {
    const { http, controller } = setup('http://api:8080/');

    http.get('/api/ping').subscribe();
    http.get('/health/ready').subscribe();

    controller.expectOne('http://api:8080/api/ping').flush({});
    controller.expectOne('http://api:8080/health/ready').flush({});
  });

  it('leaves non-API and absolute URLs alone', () => {
    const { http, controller } = setup('http://api:8080');

    http.get('/assets/logo.svg').subscribe();
    http.get('https://example.com/api/ping').subscribe();

    controller.expectOne('/assets/logo.svg').flush({});
    controller.expectOne('https://example.com/api/ping').flush({});
  });
});
