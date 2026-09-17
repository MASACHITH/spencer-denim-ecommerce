import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { PingApi } from '../api/ping-api';
import { ApiStatus } from './api-status';

describe('ApiStatus', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ApiStatus],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function render() {
    const fixture = TestBed.createComponent(ApiStatus);
    fixture.detectChanges();
    return fixture;
  }

  it('shows a loading message while the ping is in flight', () => {
    const fixture = render();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Checking the API');
    controller.expectOne(PingApi.url).flush({ status: 'ok', service: 'SpencerDenim.Api', version: '1.0.0', serverTimeUtc: '' });
  });

  it('shows the API version when the ping succeeds', async () => {
    const fixture = render();

    controller
      .expectOne({ method: 'GET', url: PingApi.url })
      .flush({ status: 'ok', service: 'SpencerDenim.Api', version: '1.2.3', serverTimeUtc: '2026-09-17T10:00:00Z' });
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('API connected');
    expect(text).toContain('v1.2.3');
  });

  it('fails to a neutral message when the API is down', async () => {
    const fixture = render();

    controller.expectOne(PingApi.url).flush('Bad gateway', { status: 502, statusText: 'Bad Gateway' });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('The API is not reachable right now.');
  });

  it('treats an unexpected payload as offline', async () => {
    const fixture = render();

    controller.expectOne(PingApi.url).flush({ unexpected: true });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('The API is not reachable right now.');
  });
});
