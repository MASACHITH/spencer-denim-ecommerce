import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminSiteContentApi } from './admin-site-content-api';
import { MegaMenuCardRequest, cardStatus } from './admin-mega-menu.model';

/**
 * The mega-menu card client (#215), asserted on the wire as #192's are: URL, verb, antiforgery header, every key
 * of the body, and the quoted `If-Match` on a delete.
 */
describe('AdminSiteContentApi — mega-menu cards (#215)', () => {
  let api: AdminSiteContentApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    api = TestBed.inject(AdminSiteContentApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function grantToken(): void {
    http.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf-token' });
  }

  const card: MegaMenuCardRequest = {
    departmentId: 1,
    title: 'New in Women',
    subtitle: null,
    buttonLabel: 'Shop now',
    linkUrl: '/new-arrivals',
    imagePublicId: null,
    sortOrder: 0,
    isActive: false,
    startsAt: null,
    endsAt: null,
  };

  const KEYS = [
    'departmentId', 'title', 'subtitle', 'buttonLabel', 'linkUrl', 'imagePublicId', 'sortOrder', 'isActive',
    'startsAt', 'endsAt',
  ];

  it('reads the cards with a plain GET and no token', () => {
    api.megaMenu().subscribe();

    const request = http.expectOne('/api/admin/content/mega-menu');
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.has('X-XSRF-TOKEN')).toBe(false);
    request.flush({ departments: [], maxCardsPerDepartment: 6 });
  });

  it('adds a card with POST, a token and every key including the nulls, and no row version', () => {
    api.saveMegaMenuCard(null, card, null).subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/mega-menu/cards');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');

    const sent = JSON.parse(JSON.stringify(request.request.body)) as Record<string, unknown>;
    expect(Object.keys(sent).sort()).toEqual([...KEYS].sort());
    expect(sent['subtitle']).toBeNull();
    request.flush({ id: 5, rowVersion: 'rv' });
  });

  it('edits a card with PUT and its row version in the body', () => {
    api.saveMegaMenuCard(5, card, 'rv-5').subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/mega-menu/cards/5');
    expect(request.request.method).toBe('PUT');

    const sent = JSON.parse(JSON.stringify(request.request.body)) as Record<string, unknown>;
    expect(Object.keys(sent).sort()).toEqual([...KEYS, 'rowVersion'].sort());
    expect(sent['rowVersion']).toBe('rv-5');
    request.flush({ id: 5, rowVersion: 'rv-6' });
  });

  it('removes a card with its row version quoted in If-Match', () => {
    api.deleteMegaMenuCard(5, 'AAAA+/==').subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/mega-menu/cards/5');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"AAAA+/=="');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });
});

describe('cardStatus (#215)', () => {
  it.each([
    [{ isActive: true, isLiveNow: true, isShownNow: true }, 'On the menu now'],
    [{ isActive: true, isLiveNow: true, isShownNow: false }, 'Waiting its turn'],
    [{ isActive: true, isLiveNow: false, isShownNow: false }, 'Scheduled'],
    [{ isActive: false, isLiveNow: false, isShownNow: false }, 'Off'],
  ])('%o reads %s', (card, label) => {
    expect(cardStatus(card)).toBe(label);
  });
});
