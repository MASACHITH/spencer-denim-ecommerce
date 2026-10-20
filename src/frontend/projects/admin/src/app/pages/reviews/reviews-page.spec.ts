import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ReviewsApi } from '../../core/reviews/reviews-api';
import { AdminReview, AdminReviewListPage, canModerate } from '../../core/reviews/reviews.model';
import { ReviewsPage } from './reviews-page';

const WHOAMI = '/api/admin/auth/whoami';
const TOKEN = '/api/admin/auth/antiforgery-token';
const ALL = ['Reviews.View', 'Reviews.Approve', 'Reviews.Edit'];

function review(overrides: Partial<AdminReview> = {}): AdminReview {
  return {
    id: 5,
    productId: 3,
    productName: 'Linen shirt',
    productCode: 'SD-0003',
    productSlug: 'linen-shirt',
    rating: 4,
    title: 'Lovely',
    body: 'Fits well.',
    displayName: 'Nimal P.',
    isVerifiedBuyer: true,
    status: 'Pending',
    createdAtUtc: '2026-10-19T04:30:00Z',
    editedAtUtc: null,
    moderatedAtUtc: null,
    moderatedByName: null,
    moderationNote: null,
    rowVersion: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

function page(items: AdminReview[] = [review()]): AdminReviewListPage {
  return { items, page: 1, pageSize: 25, totalCount: items.length, counts: { pending: 3, approved: 7, hidden: 1 } };
}

/** Review moderation (ADM-16, #189): permissions shape the screen, actions patch their card, text stays text. */
describe('ReviewsPage (ADM-16)', () => {
  let fixture: ComponentFixture<ReviewsPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  function listRequest(): TestRequest {
    return http.expectOne((candidate) => candidate.url === ReviewsApi.url && candidate.method === 'GET');
  }

  async function open(permissions: string[], payload: AdminReviewListPage = page()): Promise<void> {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ReviewsPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();

    http.expectOne(WHOAMI).flush({ grantsAllPermissions: false, permissions });
    await settle();

    if (permissions.includes('Reviews.View')) {
      const request = listRequest();
      expect(request.request.params.get('status')).toBe('Pending');
      request.flush(payload);
      await settle();
    }
  }

  async function moderation(path: string): Promise<TestRequest> {
    http.expectOne(TOKEN).flush({ token: 't' });
    await settle();
    const request = http.expectOne(`${ReviewsApi.url}/${path}`);
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('t');
    return request;
  }

  afterEach(() => http.verify());

  it('fetches nothing for a role without Reviews.View, and says so', async () => {
    await open(['Orders.View']);

    expect(byTestId('reviews-forbidden')).not.toBeNull();
  });

  it('shows a view-only role the reviews and no actions', async () => {
    await open(['Reviews.View']);

    expect(byTestId('review-5')).not.toBeNull();
    expect(byTestId('approve-5')).toBeNull();
    expect(byTestId('hide-5')).toBeNull();
    expect(byTestId('reviews-read-only')).not.toBeNull();
    expect(byTestId('reviews-counts')?.textContent).toContain('3 waiting');
    expect(byTestId('review-verified-5')).not.toBeNull();
  });

  it('offers approve only with Reviews.Approve and hide only with Reviews.Edit', async () => {
    await open(['Reviews.View', 'Reviews.Edit']);

    expect(byTestId('approve-5')).toBeNull();
    expect(byTestId('hide-5')).not.toBeNull();
  });

  it('renders review text as text', async () => {
    await open(['Reviews.View'], page([review({ title: '<b>x</b>', body: '<img src=x onerror=alert(1)>' })]));

    expect(byTestId('review-5')?.querySelector('img')).toBeNull();
    expect(byTestId('review-5')?.querySelector('b')).toBeNull();
    expect(byTestId('review-body-5')?.textContent).toBe('<img src=x onerror=alert(1)>');
  });

  it('approves with the row version and a trimmed note, then patches the card', async () => {
    await open(ALL);

    const note = byTestId<HTMLTextAreaElement>('review-note-5')!;
    note.value = '  Fine  ';
    note.dispatchEvent(new Event('input'));
    byTestId<HTMLButtonElement>('approve-5')!.click();

    const request = await moderation('5/approve');
    expect(request.request.body).toEqual({ rowVersion: 'AAAAAAAAB9E=', note: 'Fine' });
    request.flush({ review: review({ status: 'Approved', moderatedAtUtc: '2026-10-20T04:30:00Z', moderatedByName: 'Ann Staff', moderationNote: 'Fine' }), changed: true });
    await settle();

    expect(byTestId('review-status-5')?.textContent).toContain('Approved');
    expect(byTestId('review-said-5')?.textContent).toContain('counts towards the rating');
    expect(byTestId('review-moderated-5')?.textContent).toContain('Ann Staff');
    expect(byTestId('approve-5')).toBeNull();
    expect(byTestId('hide-5')).not.toBeNull();
  });

  it('offers a hidden review "Show again"', async () => {
    await open(ALL, page([review({ status: 'Hidden' })]));

    expect(byTestId('approve-5')?.textContent).toContain('Show again');
    expect(byTestId('hide-5')).toBeNull();
  });

  it('replaces the card with the current review on a 409 and says why', async () => {
    await open(ALL);

    byTestId<HTMLButtonElement>('hide-5')!.click();
    const request = await moderation('5/hide');
    request.flush(
      { code: 'stale_review', detail: 'changed', review: review({ title: 'Edited by author', rowVersion: 'AAAAAAAAB9I=' }) },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();

    expect(byTestId('review-title-5')?.textContent).toContain('Edited by author');
    expect(byTestId('review-said-5')?.textContent).toContain('Somebody changed this review');
    expect(byTestId('review-said-5')?.getAttribute('role')).toBe('alert');
  });

  it('sends the filters the moderator chose', async () => {
    await open(ALL);

    const status = byTestId<HTMLSelectElement>('reviews-status')!;
    status.value = 'Hidden';
    status.dispatchEvent(new Event('change'));
    const verified = byTestId<HTMLSelectElement>('reviews-verified')!;
    verified.value = 'Unverified';
    verified.dispatchEvent(new Event('change'));
    const search = byTestId<HTMLInputElement>('reviews-search')!;
    search.value = ' linen ';
    search.dispatchEvent(new Event('input'));
    byTestId<HTMLButtonElement>('reviews-apply')!.click();
    await settle();

    const request = listRequest();
    expect(request.request.params.get('status')).toBe('Hidden');
    expect(request.request.params.get('verified')).toBe('false');
    expect(request.request.params.get('search')).toBe('linen');
    request.flush(page([]));
    await settle();

    expect(byTestId('reviews-empty')).not.toBeNull();
  });

  it('knows doc 11 P12 transitions', () => {
    expect(canModerate('approve', 'Pending')).toBe(true);
    expect(canModerate('approve', 'Hidden')).toBe(true);
    expect(canModerate('approve', 'Approved')).toBe(false);
    expect(canModerate('hide', 'Pending')).toBe(true);
    expect(canModerate('hide', 'Approved')).toBe(true);
    expect(canModerate('hide', 'Hidden')).toBe(false);
  });
});
