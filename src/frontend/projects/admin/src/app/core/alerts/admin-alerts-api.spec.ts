import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminAlertsApi } from './admin-alerts-api';
import { AdminAlertBanner } from './admin-alert.model';

describe('AdminAlertsApi', () => {
  let api: AdminAlertsApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    api = TestBed.inject(AdminAlertsApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the banner from the one route, with no client-side filter', () => {
    // Doc 08 §3.8 R10 is the server's rule: no query parameter here decides what comes back, because a
    // client that could ask for "all types" would be a client that could be asked to.
    let received: AdminAlertBanner | null = null;
    api.open().subscribe((banner) => (received = banner));

    const request = http.expectOne(AdminAlertsApi.alertsUrl);
    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys()).toHaveLength(0);

    request.flush({
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

    expect(received!.moreThanShown).toBe(false);
    expect(received!.alerts[0].orderNumber).toBe('SD-2026-000123');
  });

  it('fetches an antiforgery token before acknowledging, and sends it in the header', () => {
    api.acknowledge(7, 'Rang the customer').subscribe();

    http.expectOne((r) => r.url.includes('antiforgery')).flush({ token: 'the-token' });

    const post = http.expectOne(`${AdminAlertsApi.alertsUrl}/7/acknowledge`);
    expect(post.request.method).toBe('POST');
    expect(post.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    expect(post.request.body).toEqual({ note: 'Rang the customer' });

    post.flush({
      alertId: 7,
      alertType: 'EmailDeliveryFailed',
      acknowledgedAtUtc: '2026-10-09T04:00:00Z',
      alreadyAcknowledged: false,
    });
  });

  it('sends a null note rather than an empty string', () => {
    // `ops.AdminAlerts.AcknowledgementNote` is nullable, and an empty string stored in it would make
    // "somebody left a note" true for a row that has nothing to read.
    api.acknowledge(7, null).subscribe();

    http.expectOne((r) => r.url.includes('antiforgery')).flush({ token: 'the-token' });

    const post = http.expectOne(`${AdminAlertsApi.alertsUrl}/7/acknowledge`);
    expect(post.request.body).toEqual({ note: null });

    post.flush({
      alertId: 7,
      alertType: 'EmailDeliveryFailed',
      acknowledgedAtUtc: '2026-10-09T04:00:00Z',
      alreadyAcknowledged: false,
    });
  });
});
