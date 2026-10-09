import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { PingApi } from '@spencer-denim/shared';

import { App } from './app';
import { routes } from './app.routes';
import { AdminAlertsApi } from './core/alerts/admin-alerts-api';
import { AdminAuthApi } from './core/auth/admin-auth-api';

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
    expect(brand?.textContent?.trim()).toBe('Colombo Wardrobe Admin');

    TestBed.inject(HttpTestingController)
      .match(AdminAlertsApi.alertsUrl)
      .forEach((request) => request.flush({ moreThanShown: false, alerts: [] }));
  });

  /**
   * **The alert banner is in the shell, not on the dashboard (W4-02a).**
   *
   * Doc 08 §3.8 calls `ops.AdminAlerts` "the dashboard banner" and ADM-04's dashboard does not exist —
   * `/` is W0-09's scaffold placeholder. Doc 11 §6.1 step 1's morning routine starts from these banners,
   * so a dead email has to reach a Super Admin on whatever admin screen they are on. Asserted here rather
   * than left as a template detail, because moving it back into one page is exactly the change that would
   * make a banner invisible on every other one.
   */
  it('puts the alert banner in the shell, above the outlet, on every admin screen', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;

    // The live region exists before any alert loads, which is what lets a confirmation survive the
    // banner's own disappearance (W3-05's two defects).
    expect(element.querySelector('[data-testid="alert-announcement"]')).not.toBeNull();

    const controller = TestBed.inject(HttpTestingController);
    controller.expectOne(AdminAlertsApi.alertsUrl).flush({
      moreThanShown: false,
      alerts: [
        {
          id: 7,
          alertType: 'EmailDeliveryFailed',
          severity: 2,
          title: 'Email not delivered: OrderPlaced for order SD-2026-000123 (outbox row 4821)',
          createdAtUtc: '2026-10-09T03:11:00Z',
          relatedEntityType: 'EmailOutbox',
          relatedEntityId: '4821',
          orderPublicId: '44444444-4444-4444-8444-444444444444',
          orderNumber: 'SD-2026-000123',
        },
      ],
    });

    fixture.detectChanges();
    await fixture.whenStable();

    const banner = element.querySelector('[data-testid="alert-banner"]');
    expect(banner).not.toBeNull();
    expect(banner?.textContent).toContain('SD-2026-000123');

    // Above the outlet rather than below it, so it is the first thing on the page after the header.
    const main = element.querySelector('#main');
    expect(banner!.compareDocumentPosition(main!) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
  });

  it('dashboard shows the heading and a neutral message when the API is down', async () => {
    const harness = await RouterTestingHarness.create();
    const controller = TestBed.inject(HttpTestingController);

    // The session guard asks who is signed in first; with the API down it lets the dashboard show anyway.
    const navigation = harness.navigateByUrl('/');
    await new Promise((resolve) => setTimeout(resolve));
    controller.expectOne(AdminAuthApi.whoAmIUrl).error(new ProgressEvent('error'));
    await navigation;

    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Colombo Wardrobe Admin');

    controller.expectOne(PingApi.url).error(new ProgressEvent('error'));
    await harness.fixture.whenStable();

    expect(harness.routeNativeElement?.textContent).toContain('The API is not reachable right now.');
    controller.verify();
  });
});
