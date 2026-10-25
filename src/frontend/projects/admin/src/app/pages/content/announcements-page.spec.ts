import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminSiteContentApi } from '../../core/content/admin-site-content-api';
import { AdminAnnouncement } from '../../core/content/admin-site-content.model';
import { AnnouncementsPage } from './announcements-page';

describe('AnnouncementsPage (#192, GLB-03)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AnnouncementsPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function row(overrides: Partial<AdminAnnouncement> = {}): AdminAnnouncement {
    return {
      id: 1,
      message: 'Free delivery over Rs. 10,000',
      linkUrl: '/delivery',
      sortOrder: 0,
      isActive: true,
      startsAt: null,
      endsAt: null,
      isLiveNow: true,
      version: 'v1',
      ...overrides,
    };
  }

  function create(rows: AdminAnnouncement[] = [row()]): ComponentFixture<AnnouncementsPage> {
    const fixture = TestBed.createComponent(AnnouncementsPage);
    fixture.detectChanges();
    controller.expectOne(AdminSiteContentApi.announcementsUrl).flush(rows);
    fixture.detectChanges();
    return fixture;
  }

  function q<T extends HTMLElement>(fixture: ComponentFixture<AnnouncementsPage>, testId: string): T | null {
    return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-testid="${testId}"]`);
  }

  function type(input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
  }

  it('lists the messages with their status, as plain text', () => {
    const fixture = create([
      row(),
      row({ id: 2, message: '<b>not bold</b>', isActive: false, isLiveNow: false, version: 'v2' }),
    ]);

    expect(q(fixture, 'ann-1')?.textContent).toContain('Free delivery');
    expect(q(fixture, 'ann-status-1')?.textContent).toContain('On the shop now');
    expect(q(fixture, 'ann-status-2')?.textContent).toContain('Off');
    expect(q(fixture, 'ann-2')?.textContent).toContain('<b>not bold</b>');
    expect(q(fixture, 'ann-2')?.querySelector('b')).toBeNull();
  });

  it('counts characters toward 200', () => {
    const fixture = create();

    type(q<HTMLInputElement>(fixture, 'ann-message')!, 'Hello');
    fixture.detectChanges();
    expect(q(fixture, 'ann-count')?.textContent).toContain('5 of 200');
  });

  it('hints, without refusing, when a message would run to several lines on a phone', () => {
    const fixture = create();
    const input = q<HTMLInputElement>(fixture, 'ann-message')!;

    type(input, 'x'.repeat(60));
    fixture.detectChanges();
    expect(q(fixture, 'ann-long-hint')).toBeNull();

    type(input, 'x'.repeat(61));
    fixture.detectChanges();
    expect(q(fixture, 'ann-long-hint')?.textContent).toContain('phones');
    expect(input.getAttribute('aria-describedby')).toBe('ann-count ann-long');
    expect(input.getAttribute('maxlength')).toBe('200');
  });

  it('adds a message with every key and no version', () => {
    const fixture = create([]);

    type(q<HTMLInputElement>(fixture, 'ann-message')!, '  New season  ');
    q<HTMLButtonElement>(fixture, 'ann-save')!.click();
    grantToken();

    const request = controller.expectOne(AdminSiteContentApi.announcementsUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      message: 'New season',
      linkUrl: null,
      sortOrder: 0,
      isActive: true,
      startsAt: null,
      endsAt: null,
    });
    request.flush({ id: 5, version: 'v5' });
    controller.expectOne(AdminSiteContentApi.announcementsUrl).flush([row({ id: 5 })]);
    fixture.detectChanges();
    expect(q(fixture, 'ann-said')?.textContent).toContain('added');
  });

  it('edits a message with its version', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'ann-edit-1')!.click();
    fixture.detectChanges();
    TestBed.tick();
    expect(q<HTMLInputElement>(fixture, 'ann-message')?.value).toBe('Free delivery over Rs. 10,000');
    // Edit sits on a row far below the form; without this the press looked like it did nothing.
    expect(document.activeElement).toBe(q<HTMLInputElement>(fixture, 'ann-message'));

    q<HTMLButtonElement>(fixture, 'ann-save')!.click();
    grantToken();
    const request = controller.expectOne('/api/admin/content/announcements/1');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.version).toBe('v1');
    expect(request.request.body.linkUrl).toBe('/delivery');
    request.flush({ id: 1, version: 'v1b' });
    controller.expectOne(AdminSiteContentApi.announcementsUrl).flush([row({ version: 'v1b' })]);
  });

  it('puts the server validation message against the control', () => {
    const fixture = create();

    type(q<HTMLInputElement>(fixture, 'ann-link')!, 'javascript:alert(1)');
    type(q<HTMLInputElement>(fixture, 'ann-message')!, 'Hi');
    q<HTMLButtonElement>(fixture, 'ann-save')!.click();
    grantToken();
    controller
      .expectOne(AdminSiteContentApi.announcementsUrl)
      .flush({ title: 'Invalid', status: 400, errors: { linkUrl: ['Use a page on this shop.'] } }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(q(fixture, 'ann-error-linkUrl')?.textContent).toContain('Use a page on this shop.');
  });

  it('says a stale message was changed by someone else', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'ann-edit-1')!.click();
    fixture.detectChanges();
    q<HTMLButtonElement>(fixture, 'ann-save')!.click();
    grantToken();
    controller
      .expectOne('/api/admin/content/announcements/1')
      .flush({ code: 'stale_row_version' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(q(fixture, 'ann-problem')?.textContent).toContain('Someone else changed this message');
  });

  it('removes on the second press with the version quoted', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'ann-remove-1')!.click();
    fixture.detectChanges();
    q<HTMLButtonElement>(fixture, 'ann-remove-1')!.click();
    grantToken();

    const request = controller.expectOne('/api/admin/content/announcements/1');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"v1"');
    request.flush(null, { status: 204, statusText: 'No Content' });
    controller.expectOne(AdminSiteContentApi.announcementsUrl).flush([]);
  });

  it('shows the refusal panel to a role without Content.View and hides the form', () => {
    const fixture = TestBed.createComponent(AnnouncementsPage);
    fixture.detectChanges();
    controller.expectOne(AdminSiteContentApi.announcementsUrl).flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(q(fixture, 'ann-problem')?.textContent).toContain('does not include the announcement bar');
    expect(q(fixture, 'ann-message')).toBeNull();
  });
});
