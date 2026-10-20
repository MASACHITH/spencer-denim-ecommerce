import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminSiteContentApi } from '../../core/content/admin-site-content-api';
import {
  AdminMegaMenuCard,
  AdminMegaMenuContent,
  AdminMegaMenuDepartment,
} from '../../core/content/admin-mega-menu.model';
import { MegaMenuPage } from './mega-menu-page';

describe('MegaMenuPage (#215, GLB-05)', () => {
  let controller: HttpTestingController;

  const URL = AdminSiteContentApi.megaMenuUrl;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [MegaMenuPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function card(overrides: Partial<AdminMegaMenuCard> = {}): AdminMegaMenuCard {
    return {
      id: 1,
      departmentId: 1,
      title: 'New in Women',
      subtitle: 'Everyday essentials.',
      buttonLabel: 'Shop now',
      linkUrl: '/new-arrivals',
      image: { publicId: '11111111-0000-4000-8000-000000000001', url: '/media/a.webp', altText: 'A linen dress' },
      sortOrder: 0,
      isActive: true,
      startsAt: null,
      endsAt: null,
      isLiveNow: true,
      isShownNow: true,
      rowVersion: 'rv1',
      ...overrides,
    };
  }

  function department(overrides: Partial<AdminMegaMenuDepartment> = {}): AdminMegaMenuDepartment {
    return {
      id: 1,
      name: 'Women',
      slug: 'women',
      isActive: true,
      description: 'Everyday essentials.',
      heroImage: null,
      cards: [card()],
      ...overrides,
    };
  }

  function content(departments: AdminMegaMenuDepartment[] = [department()]): AdminMegaMenuContent {
    return { departments, maxCardsPerDepartment: 6 };
  }

  function create(body: AdminMegaMenuContent = content()): ComponentFixture<MegaMenuPage> {
    const fixture = TestBed.createComponent(MegaMenuPage);
    fixture.detectChanges();
    controller.expectOne(URL).flush(body);
    fixture.detectChanges();
    return fixture;
  }

  function q<T extends HTMLElement>(fixture: ComponentFixture<MegaMenuPage>, testId: string): T | null {
    return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-testid="${testId}"]`);
  }

  function type(input: HTMLInputElement | HTMLSelectElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
  }

  it('lists each department with its cards and their status, as plain text', () => {
    const fixture = create(
      content([
        department({
          cards: [
            card(),
            card({ id: 2, title: '<b>not bold</b>', isShownNow: false, rowVersion: 'rv2' }),
            card({ id: 3, isActive: true, isLiveNow: false, isShownNow: false, startsAt: '2030-01-01T09:00' }),
            card({ id: 4, isActive: false, isLiveNow: false, isShownNow: false, image: null }),
          ],
        }),
      ]),
    );

    expect(q(fixture, 'mm-status-1')?.textContent).toContain('On the menu now');
    expect(q(fixture, 'mm-status-2')?.textContent).toContain('Waiting its turn');
    expect(q(fixture, 'mm-status-3')?.textContent).toContain('Scheduled');
    expect(q(fixture, 'mm-status-4')?.textContent).toContain('Off');
    expect(q(fixture, 'mm-card-2')?.textContent).toContain('<b>not bold</b>');
    expect(q(fixture, 'mm-card-2')?.querySelector('b')).toBeNull();
    expect(q(fixture, 'mm-card-3')?.textContent).toContain('2030-01-01T09:00');
    expect(q(fixture, 'mm-card-4')?.textContent).toContain('No picture');
    expect(q(fixture, 'mm-fallback-women')).toBeNull();
  });

  it('says what the menu shows when no card is on', () => {
    const fixture = create(
      content([
        department({ cards: [card({ isActive: false, isLiveNow: false, isShownNow: false })] }),
        department({ id: 2, name: 'Men', slug: 'men', description: null, heroImage: null, cards: [] }),
      ]),
    );

    expect(q(fixture, 'mm-fallback-women')?.textContent).toContain('New in Women');
    expect(q(fixture, 'mm-fallback-women')?.textContent).toContain('Everyday essentials.');
    expect(q(fixture, 'mm-fallback-men')?.textContent).toContain('empty picture frame');
    expect(q(fixture, 'mm-empty-men')?.textContent).toContain('No cards yet');
  });

  it('adds a card for a department with every key, starting from today’s words', () => {
    const fixture = create(content([department({ cards: [] })]));

    q<HTMLButtonElement>(fixture, 'mm-add-women')!.click();
    fixture.detectChanges();
    expect(q<HTMLInputElement>(fixture, 'mm-title')?.value).toBe('New in Women');
    expect(q<HTMLInputElement>(fixture, 'mm-link')?.value).toBe('/new-arrivals');

    q<HTMLInputElement>(fixture, 'mm-active')!.click();
    type(q<HTMLInputElement>(fixture, 'mm-subtitle')!, '  Linen for the heat  ');
    q<HTMLButtonElement>(fixture, 'mm-save')!.click();
    grantToken();

    const request = controller.expectOne(`${URL}/cards`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      departmentId: 1,
      title: 'New in Women',
      subtitle: 'Linen for the heat',
      buttonLabel: 'Shop now',
      linkUrl: '/new-arrivals',
      imagePublicId: null,
      sortOrder: 0,
      isActive: false,
      startsAt: null,
      endsAt: null,
    });
    request.flush({ id: 9, rowVersion: 'rv9' });
    controller.expectOne(URL).flush(content());
    fixture.detectChanges();
    expect(q(fixture, 'mm-said')?.textContent).toContain('added');
  });

  it('edits a card with its row version and its picture', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'mm-edit-1')!.click();
    fixture.detectChanges();
    expect(q<HTMLInputElement>(fixture, 'mm-title')?.value).toBe('New in Women');
    expect(q<HTMLSelectElement>(fixture, 'mm-department')?.value).toBe('1');

    type(q<HTMLInputElement>(fixture, 'mm-starts')!, '2026-11-01T09:00');
    q<HTMLButtonElement>(fixture, 'mm-save')!.click();
    grantToken();

    const request = controller.expectOne(`${URL}/cards/1`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.rowVersion).toBe('rv1');
    expect(request.request.body.imagePublicId).toBe('11111111-0000-4000-8000-000000000001');
    expect(request.request.body.startsAt).toBe('2026-11-01T09:00');
    request.flush({ id: 1, rowVersion: 'rv1b' });
    controller.expectOne(URL).flush(content());
  });

  it('puts the server validation message against the control', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'mm-edit-1')!.click();
    fixture.detectChanges();
    type(q<HTMLInputElement>(fixture, 'mm-link')!, 'javascript:alert(1)');
    q<HTMLButtonElement>(fixture, 'mm-save')!.click();
    grantToken();
    controller
      .expectOne(`${URL}/cards/1`)
      .flush(
        { title: 'Invalid', status: 400, errors: { linkUrl: ['Use a page on this shop.'], imagePublicId: ['Needs a picture.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();

    expect(q(fixture, 'mm-error-linkUrl')?.textContent).toContain('Use a page on this shop.');
    expect(q(fixture, 'mm-image-error')?.textContent).toContain('Needs a picture.');
    expect(q<HTMLInputElement>(fixture, 'mm-link')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('says a stale card was changed by someone else and offers a reload', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'mm-edit-1')!.click();
    fixture.detectChanges();
    q<HTMLButtonElement>(fixture, 'mm-save')!.click();
    grantToken();
    controller.expectOne(`${URL}/cards/1`).flush({ code: 'stale_row_version' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(q(fixture, 'mm-problem')?.textContent).toContain('Someone else changed this card');

    q<HTMLButtonElement>(fixture, 'mm-reload')!.click();
    controller.expectOne(URL).flush(content());
  });

  it('removes on the second press with the row version quoted', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'mm-remove-1')!.click();
    fixture.detectChanges();
    expect(q(fixture, 'mm-keep-1')).not.toBeNull();
    q<HTMLButtonElement>(fixture, 'mm-remove-1')!.click();
    grantToken();

    const request = controller.expectOne(`${URL}/cards/1`);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"rv1"');
    request.flush(null, { status: 204, statusText: 'No Content' });
    controller.expectOne(URL).flush(content([department({ cards: [] })]));
  });

  it('stops offering Add once a department holds the most it can', () => {
    const full = Array.from({ length: 6 }, (_, index) => card({ id: index + 1, isShownNow: index === 0 }));
    const fixture = create(content([department({ cards: full })]));

    expect(q(fixture, 'mm-add-women')).toBeNull();
    expect(q(fixture, 'mm-dept-women')?.textContent).toContain('the most it can');
  });

  it('shows the refusal panel to a role without Content.View and hides the form', () => {
    const fixture = TestBed.createComponent(MegaMenuPage);
    fixture.detectChanges();
    controller.expectOne(URL).flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(q(fixture, 'mm-problem')?.textContent).toContain('does not include the mega menu cards');
    expect(q(fixture, 'mm-title')).toBeNull();
  });
});
