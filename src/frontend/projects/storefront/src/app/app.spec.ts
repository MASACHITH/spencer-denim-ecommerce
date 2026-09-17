import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { PingApi } from '@spencer-denim/shared';

import { App } from './app';
import { routes } from './app.routes';

describe('Storefront App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('renders the Spencer Denim wordmark', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const wordmark = (fixture.nativeElement as HTMLElement).querySelector('.wordmark');
    expect(wordmark?.textContent?.trim()).toBe('Spencer Denim');
  });

  it('home page shows the brand heading and checks the API', async () => {
    const harness = await RouterTestingHarness.create('/');
    const controller = TestBed.inject(HttpTestingController);

    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Spencer Denim');

    controller.expectOne(PingApi.url).flush({ status: 'ok', service: 'SpencerDenim.Api', version: '1.0.0', serverTimeUtc: '' });
    await harness.fixture.whenStable();

    expect(harness.routeNativeElement?.textContent).toContain('API connected');
    controller.verify();
  });
});
