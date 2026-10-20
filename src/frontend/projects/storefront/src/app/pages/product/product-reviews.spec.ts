import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ProductReviews } from './product-reviews';
import { AccountStore } from '../../core/account/account.store';
import { MyReview, ProductReviewPage, PublicReview } from '../../core/reviews/reviews.model';

const PAGE_URL = '/api/catalog/products/linen-shirt/reviews';
const MINE_URL = '/api/account/reviews/42';

function review(overrides: Partial<PublicReview> = {}): PublicReview {
  return {
    id: 1,
    rating: 4,
    title: 'Lovely linen',
    body: 'Fits well.\nWashes well.',
    displayName: 'Nimal P.',
    isVerifiedBuyer: false,
    createdAtUtc: '2026-10-18T04:30:00Z',
    editedAtUtc: null,
    ...overrides,
  };
}

function page(items: PublicReview[], totalCount = items.length): ProductReviewPage {
  const count = totalCount;
  return {
    summary: {
      average: count === 0 ? null : 4.5,
      count,
      distribution: [
        { stars: 5, count: count === 0 ? 0 : 1 },
        { stars: 4, count: count === 0 ? 0 : count - 1 },
        { stars: 3, count: 0 },
        { stars: 2, count: 0 },
        { stars: 1, count: 0 },
      ],
    },
    items,
    page: 1,
    pageSize: 10,
    totalCount,
  };
}

function mine(overrides: Partial<MyReview> = {}): MyReview {
  return {
    id: 9,
    productId: 42,
    rating: 3,
    title: 'Mine',
    body: null,
    displayName: 'Nimal P.',
    isVerifiedBuyer: false,
    status: 'Pending',
    createdAtUtc: '2026-10-19T04:30:00Z',
    editedAtUtc: null,
    ...overrides,
  };
}

/**
 * The product page's reviews (PDP-08, #189): what everyone sees, what a signed-out and a signed-in shopper are
 * offered, that review text is text, and that nothing about one person is rendered on the server.
 */
describe('ProductReviews (PDP-08)', () => {
  let fixture: ComponentFixture<ProductReviews>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let account: AccountStore;

  function setUp(platform: 'browser' | 'server' = 'browser'): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: PLATFORM_ID, useValue: platform },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    account = TestBed.inject(AccountStore);
  }

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(ProductReviews);
    fixture.componentRef.setInput('productId', 42);
    fixture.componentRef.setInput('slug', 'linen-shirt');
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function query(testId: string): HTMLElement | null {
    return element.querySelector(`[data-testid="${testId}"]`);
  }

  function answerPage(body: ProductReviewPage): void {
    http.expectOne((request) => request.url === PAGE_URL).flush(body);
  }

  afterEach(() => http.verify());

  it('shows the summary, the bars and each review with its badge', async () => {
    setUp();
    account.accept(null);
    await render();
    answerPage(page([review({ isVerifiedBuyer: true }), review({ id: 2, displayName: 'Kamala S.' })]));
    await settle();

    expect(query('reviews-summary')?.textContent).toContain('4.5');
    expect(query('reviews-count')?.textContent).toContain('Based on 2 reviews');
    expect(element.querySelectorAll('.rv__bar-row').length).toBe(5);
    expect(query('review-1')?.querySelector('[data-testid="review-verified"]')?.textContent).toContain('Verified buyer');
    expect(query('review-2')?.querySelector('[data-testid="review-verified"]')).toBeNull();
    expect(query('review-1')?.textContent).toContain('Rated 4 out of 5');
  });

  it('renders markup in a review as text and never as elements', async () => {
    setUp();
    account.accept(null);
    await render();
    answerPage(page([review({ title: '<b>Bold</b>', body: '<img src=x onerror="alert(1)"><script>alert(2)</script>' })]));
    await settle();

    const item = query('review-1')!;
    expect(item.querySelector('img')).toBeNull();
    expect(item.querySelector('script')).toBeNull();
    expect(item.querySelector('b')).toBeNull();
    expect(query('review-text')?.textContent).toBe('<img src=x onerror="alert(1)"><script>alert(2)</script>');
    expect(item.textContent).toContain('<b>Bold</b>');
  });

  it('says there are no reviews yet when there are none', async () => {
    setUp();
    account.accept(null);
    await render();
    answerPage(page([]));
    await settle();

    expect(query('reviews-none')?.textContent).toContain('No reviews yet');
    expect(query('reviews-list')).toBeNull();
  });

  it('offers a signed-out shopper a sign-in link that comes back here', async () => {
    setUp();
    account.accept(null);
    await render();
    answerPage(page([]));
    await settle();

    const link = query('reviews-sign-in')?.querySelector('a');
    expect(link?.getAttribute('href')).toContain('/account/sign-in?returnTo=');
    expect(query('reviews-write')).toBeNull();
    expect(query('review-form')).toBeNull();
  });

  it('renders neither the form nor the sign-in prompt on the server, and asks nothing about the visitor', async () => {
    setUp('server');
    await render();
    answerPage(page([review()]));
    await settle();

    expect(query('reviews-sign-in')).toBeNull();
    expect(query('reviews-write')).toBeNull();
    expect(query('review-form')).toBeNull();
    http.expectNone(MINE_URL);
    http.expectNone('/api/account/session');
  });

  it('lets a signed-in customer write a review and says it waits for approval', async () => {
    setUp();
    account.accept({ userId: 7, email: 'n@example.invalid', firstName: 'Nimal', lastName: 'Perera', emailConfirmed: true });
    await render();
    answerPage(page([]));
    http.expectOne(MINE_URL).flush({ review: null, wouldBeVerified: true });
    await settle();

    (query('reviews-write') as HTMLButtonElement).click();
    await settle();

    expect(query('review-form')?.textContent).toContain('Verified buyer badge');

    (query('review-submit') as HTMLButtonElement).click();
    await settle();
    expect(query('review-error-rating')?.textContent).toContain('Choose a star rating');

    (query('rating-5') as HTMLInputElement).click();
    const title = query('review-title') as HTMLInputElement;
    title.value = '  Great  ';
    title.dispatchEvent(new Event('input'));
    await settle();

    (query('review-submit') as HTMLButtonElement).click();
    const sent = http.expectOne((request) => request.url === MINE_URL && request.method === 'POST');
    expect(sent.request.body).toEqual({ rating: 5, title: 'Great', body: null });
    sent.flush({ review: mine({ rating: 5, title: 'Great', isVerifiedBuyer: true }), outcome: 'Pending' });
    await settle();

    expect(query('reviews-said')?.textContent).toContain('will appear once we have checked it');
    expect(query('review-form')).toBeNull();
    expect(query('reviews-my-status')?.textContent).toContain('waiting to be checked');
  });

  it('reloads the list when a review is published at once', async () => {
    setUp();
    account.accept({ userId: 7, email: 'n@example.invalid', firstName: 'Nimal', lastName: null, emailConfirmed: true });
    await render();
    answerPage(page([]));
    http.expectOne(MINE_URL).flush({ review: null, wouldBeVerified: false });
    await settle();

    (query('reviews-write') as HTMLButtonElement).click();
    await settle();
    (query('rating-4') as HTMLInputElement).click();
    (query('review-submit') as HTMLButtonElement).click();
    http.expectOne((request) => request.method === 'POST').flush({ review: mine({ status: 'Approved', rating: 4 }), outcome: 'Published' });
    await settle();

    expect(query('reviews-said')?.textContent).toContain('now on this page');
    answerPage(page([review({ id: 9, rating: 4 })]));
    await settle();
    expect(query('review-9')).not.toBeNull();
  });

  it('opens an existing review for editing and sends it with PUT', async () => {
    setUp();
    account.accept({ userId: 7, email: 'n@example.invalid', firstName: 'Nimal', lastName: null, emailConfirmed: true });
    await render();
    answerPage(page([]));
    http.expectOne(MINE_URL).flush({ review: mine({ status: 'Hidden' }), wouldBeVerified: false });
    await settle();

    expect(query('reviews-my-status')?.textContent).toContain('not shown on the site');
    (query('reviews-edit') as HTMLButtonElement).click();
    await settle();

    expect((query('review-title') as HTMLInputElement).value).toBe('Mine');
    expect((query('rating-3') as HTMLInputElement).checked).toBe(true);

    (query('review-submit') as HTMLButtonElement).click();
    const sent = http.expectOne((request) => request.method === 'PUT');
    sent.flush({ review: mine({ status: 'Hidden' }), outcome: 'StillHidden' });
    await settle();
    answerPage(page([]));
    await settle();

    expect(query('reviews-said')?.textContent).toContain('not shown on the site');
  });

  it('turns a 409 from another tab into an edit of the saved review', async () => {
    setUp();
    account.accept({ userId: 7, email: 'n@example.invalid', firstName: 'Nimal', lastName: null, emailConfirmed: true });
    await render();
    answerPage(page([]));
    http.expectOne(MINE_URL).flush({ review: null, wouldBeVerified: false });
    await settle();

    (query('reviews-write') as HTMLButtonElement).click();
    await settle();
    (query('rating-2') as HTMLInputElement).click();
    (query('review-submit') as HTMLButtonElement).click();
    http
      .expectOne((request) => request.method === 'POST')
      .flush({ code: 'already_reviewed', detail: 'Already', review: mine({ title: 'From the other tab' }) }, { status: 409, statusText: 'Conflict' });
    await settle();

    expect(query('reviews-said')?.textContent).toContain('already reviewed');
    expect((query('review-title') as HTMLInputElement).value).toBe('From the other tab');
    expect(query('review-submit')?.textContent).toContain('Save changes');
  });

  it('puts the server field errors and the rate-limit sentence where the shopper reads them', async () => {
    setUp();
    account.accept({ userId: 7, email: 'n@example.invalid', firstName: 'Nimal', lastName: null, emailConfirmed: true });
    await render();
    answerPage(page([]));
    http.expectOne(MINE_URL).flush({ review: null, wouldBeVerified: false });
    await settle();

    (query('reviews-write') as HTMLButtonElement).click();
    await settle();
    (query('rating-5') as HTMLInputElement).click();
    (query('review-submit') as HTMLButtonElement).click();
    http
      .expectOne((request) => request.method === 'POST')
      .flush({ errors: { body: ['This contains a hidden control character. Please retype it.'] } }, { status: 400, statusText: 'Bad Request' });
    await settle();
    expect(query('review-error-body')?.textContent).toContain('hidden control character');

    (query('review-submit') as HTMLButtonElement).click();
    http
      .expectOne((request) => request.method === 'POST')
      .flush({ code: 'rate_limited', detail: 'You have written or edited a lot of reviews in the last hour.' }, { status: 429, statusText: 'Too Many Requests' });
    await settle();
    expect(query('reviews-said')?.textContent).toContain('lot of reviews');
  });

  it('loads more reviews and appends them', async () => {
    setUp();
    account.accept(null);
    await render();
    const first = Array.from({ length: 10 }, (_, index) => review({ id: index + 1 }));
    answerPage(page(first, 12));
    await settle();

    (query('reviews-more') as HTMLButtonElement).click();
    const next = http.expectOne((request) => request.url === PAGE_URL && request.params.get('page') === '2');
    next.flush({ ...page([review({ id: 11 }), review({ id: 12 })], 12), page: 2 });
    await settle();

    expect(element.querySelectorAll('.rv__item').length).toBe(12);
    expect(query('reviews-more')).toBeNull();
  });
});
