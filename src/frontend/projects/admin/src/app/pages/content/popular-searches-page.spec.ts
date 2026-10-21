import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminPopularSearches, AdminPopularSearchesApi } from '../../core/content/admin-popular-searches-api';
import { PopularSearchesPage } from './popular-searches-page';

describe('PopularSearchesPage (#78, SRCH-01)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [PopularSearchesPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  const LIST: AdminPopularSearches = {
    shown: [
      { term: 'jeans', searches: 40 },
      { term: '<b>linen</b>', searches: 1 },
    ],
    hidden: [{ term: 'hidden term', hiddenAtUtc: '2026-10-21T06:30:00' }],
    minimumSearches: 5,
  };

  function create(list: AdminPopularSearches = LIST): ComponentFixture<PopularSearchesPage> {
    const fixture = TestBed.createComponent(PopularSearchesPage);
    fixture.detectChanges();
    controller.expectOne(AdminPopularSearchesApi.url).flush(list);
    fixture.detectChanges();
    return fixture;
  }

  function q<T extends HTMLElement>(fixture: ComponentFixture<PopularSearchesPage>, testId: string): T | null {
    return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-testid="${testId}"]`);
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
  }

  it('lists what shoppers see with the counts, as plain text, and what is hidden', () => {
    const fixture = create();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(q(fixture, 'ps-shown')?.textContent).toContain('jeans');
    expect(q(fixture, 'ps-shown')?.textContent).toContain('40 searches');
    expect(q(fixture, 'ps-shown')?.textContent).toContain('1 search');
    expect(q(fixture, 'ps-shown')?.querySelector('b')).toBeNull();
    expect(q(fixture, 'ps-hidden')?.textContent).toContain('hidden term');
    expect(text).toContain('at least 5 times');
  });

  it('says plainly when nothing has reached the list yet', () => {
    const fixture = create({ shown: [], hidden: [], minimumSearches: 5 });

    expect(q(fixture, 'ps-empty')).not.toBeNull();
    expect(q(fixture, 'ps-hidden-empty')).not.toBeNull();
  });

  it('hides a term with the antiforgery token and reloads the list', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'ps-hide-0')!.click();
    grantToken();
    const hide = controller.expectOne(`${AdminPopularSearchesApi.url}/hide`);
    expect(hide.request.body).toEqual({ term: 'jeans' });
    expect(hide.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf');
    hide.flush({ changed: true });

    controller.expectOne(AdminPopularSearchesApi.url).flush({
      shown: [LIST.shown[1]],
      hidden: [...LIST.hidden, { term: 'jeans', hiddenAtUtc: '2026-10-21T07:00:00' }],
      minimumSearches: 5,
    });
    fixture.detectChanges();

    expect(q(fixture, 'ps-said')?.textContent).toContain('“jeans” is hidden from shoppers.');
    expect(q(fixture, 'ps-hidden')?.textContent).toContain('jeans');
  });

  it('shows a hidden term again', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'ps-show-0')!.click();
    grantToken();
    const show = controller.expectOne(`${AdminPopularSearchesApi.url}/show`);
    expect(show.request.body).toEqual({ term: 'hidden term' });
    show.flush({ changed: true });
    controller.expectOne(AdminPopularSearchesApi.url).flush({ ...LIST, hidden: [] });
    fixture.detectChanges();

    expect(q(fixture, 'ps-said')?.textContent).toContain('can appear again');
  });

  it('hides a typed term, and shows the server’s reason when it is refused', () => {
    const fixture = create();
    const input = q<HTMLInputElement>(fixture, 'ps-term')!;

    input.value = 'someone@example.com';
    input.dispatchEvent(new Event('input'));
    q<HTMLButtonElement>(fixture, 'ps-hide-typed')!.click();
    grantToken();
    controller.expectOne(`${AdminPopularSearchesApi.url}/hide`).flush(
      { title: 'The term cannot be used.', errors: { term: ['Enter a search term of at least 2 characters.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(q(fixture, 'ps-error')?.textContent).toContain('at least 2 characters');
    expect(input.getAttribute('aria-invalid')).toBe('true');
  });

  it('asks for a term before sending an empty one', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'ps-hide-typed')!.click();
    fixture.detectChanges();

    expect(q(fixture, 'ps-error')?.textContent).toContain('Type the term to hide.');
  });

  it('says so when a hide changed nothing', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'ps-hide-0')!.click();
    grantToken();
    controller.expectOne(`${AdminPopularSearchesApi.url}/hide`).flush({ changed: false });
    controller.expectOne(AdminPopularSearchesApi.url).flush(LIST);
    fixture.detectChanges();

    expect(q(fixture, 'ps-said')?.textContent).toContain('already hidden');
  });

  it('shows the refusal to a role without the Content permission and offers no controls', () => {
    const fixture = TestBed.createComponent(PopularSearchesPage);
    fixture.detectChanges();
    controller.expectOne(AdminPopularSearchesApi.url).flush({}, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(q(fixture, 'ps-problem')?.textContent).toContain('Content permission');
    expect(q(fixture, 'ps-term')).toBeNull();
  });
});
