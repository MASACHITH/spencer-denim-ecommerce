import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { PingApi } from '@spencer-denim/shared';

import { App } from './app';
import { routes } from './app.routes';

describe('Admin App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('renders the admin brand in the header', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const brand = (fixture.nativeElement as HTMLElement).querySelector('.admin-header__brand');
    expect(brand?.textContent?.trim()).toBe('Spencer Denim Admin');
  });

  it('dashboard shows the heading and a neutral message when the API is down', async () => {
    const harness = await RouterTestingHarness.create('/');
    const controller = TestBed.inject(HttpTestingController);

    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Spencer Denim Admin');

    controller.expectOne(PingApi.url).error(new ProgressEvent('error'));
    await harness.fixture.whenStable();

    expect(harness.routeNativeElement?.textContent).toContain('The API is not reachable right now.');
    controller.verify();
  });
});
