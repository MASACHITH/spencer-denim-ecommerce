import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';

import { AccountStore } from '../../core/account/account.store';
import { fieldErrorsOf } from '../../core/checkout/checkout-api';
import { ReviewsApi } from '../../core/reviews/reviews-api';
import {
  MyProductReview,
  ProductReviewPage,
  PublicReview,
  REVIEWS_PAGE_SIZE,
  REVIEW_LIMITS,
  ReviewDraft,
  draftProblems,
  statusMessage,
  submissionMessage,
} from '../../core/reviews/reviews.model';

const DATE = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'Asia/Colombo' });

const EMPTY_DRAFT: ReviewDraft = { rating: null, title: '', body: '' };

/**
 * Reviews and ratings on the product page (PDP-08; doc 11 P12; issue #189): the rating summary, the approved
 * reviews with their Verified buyer badge, and the write-a-review form.
 *
 * **What the server renders is the same for every visitor.** The summary and the list are public (approved
 * reviews only) and load during SSR, so a crawler and a first paint see them. Whether the shopper is signed in is
 * not known on the server (`AccountStore` never asks there), so the form area renders nothing until the browser
 * has asked: a signed-out shopper then sees a sign-in link, a signed-in one the form. Nothing about one person
 * reaches the server-rendered HTML, which is what keeps the product page cacheable.
 *
 * **Review text is shown as text.** Interpolation escapes it, so markup a customer typed is visible as the
 * characters they typed and never runs; line breaks are kept with `white-space: pre-line`.
 */
@Component({
  selector: 'sd-product-reviews',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="rv" aria-labelledby="rv-title" data-testid="reviews">
      <h2 class="rv__title" id="rv-title">Reviews</h2>

      @if (page(); as answered) {
        <div class="rv__layout">
          <div class="rv__summary" data-testid="reviews-summary">
            @if (answered.summary.count > 0) {
              <p class="rv__average">
                <span class="rv__average-number" aria-hidden="true">{{ format(answered.summary.average) }}</span>
                <span class="rv__stars" aria-hidden="true">{{ stars(answered.summary.average) }}</span>
                <span class="sr-only">Rated {{ format(answered.summary.average) }} out of 5</span>
              </p>
              <p class="rv__count" data-testid="reviews-count">
                Based on {{ answered.summary.count }} {{ answered.summary.count === 1 ? 'review' : 'reviews' }}
              </p>
              <ul class="rv__bars" aria-label="How many reviews gave each rating">
                @for (bucket of answered.summary.distribution; track bucket.stars) {
                  <li class="rv__bar-row">
                    <span class="rv__bar-label">{{ bucket.stars }} star</span>
                    <span class="rv__bar" aria-hidden="true">
                      <span class="rv__bar-fill" [style.width.%]="share(bucket.count, answered.summary.count)"></span>
                    </span>
                    <span class="rv__bar-count">
                      {{ bucket.count }}<span class="sr-only"> {{ bucket.count === 1 ? 'review' : 'reviews' }}</span>
                    </span>
                  </li>
                }
              </ul>
            } @else {
              <p class="rv__none" data-testid="reviews-none">No reviews yet.</p>
            }

            <div class="rv__write">
              @if (account.isSignedOut()) {
                <p class="rv__prompt" data-testid="reviews-sign-in">
                  <a class="btn btn--secondary" routerLink="/account/sign-in" [queryParams]="{ returnTo: returnTo() }">
                    Sign in to write a review
                  </a>
                </p>
              } @else if (account.isSignedIn() && !formOpen()) {
                @if (mine(); as current) {
                  @if (current.review; as review) {
                    <p class="acc-notice" data-testid="reviews-my-status">{{ statusText(review.status) }}</p>
                    <button class="btn btn--secondary" type="button" data-testid="reviews-edit" (click)="openForm()">
                      Edit your review
                    </button>
                  } @else {
                    <button class="btn btn--primary" type="button" data-testid="reviews-write" (click)="openForm()">
                      Write a review
                    </button>
                  }
                }
              }
              <p class="acc-notice" [class.is-on]="said() !== ''" [class.acc-notice--ok]="!saidIsError()" [class.acc-notice--alert]="saidIsError()" role="status" data-testid="reviews-said">{{ said() }}</p>
            </div>
          </div>

          <div class="rv__main">
            @if (formOpen()) {
              <form class="acc-form rv__form" novalidate (submit)="submit($event)" data-testid="review-form" aria-labelledby="rv-form-title">
                <h3 class="rv__form-title" id="rv-form-title" tabindex="-1">{{ editing() ? 'Edit your review' : 'Write a review' }}</h3>
                @if (mine()?.wouldBeVerified) {
                  <p class="acc-hint">You bought this, so your review will show the Verified buyer badge.</p>
                }

                <fieldset class="acc-form__group" [attr.aria-describedby]="error('rating') ? 'rv-rating-error' : null">
                  <legend class="acc-form__legend">Your rating</legend>
                  <div class="rv__pick">
                    @for (value of ratingValues; track value) {
                      <input
                        class="rv__radio"
                        type="radio"
                        name="rating"
                        [id]="'rv-rating-' + value"
                        [value]="value"
                        [checked]="draft().rating === value"
                        [attr.data-testid]="'rating-' + value"
                        (change)="patch({ rating: value })"
                      />
                      <label class="rv__star" [for]="'rv-rating-' + value" [class.is-on]="(draft().rating ?? 0) >= value">
                        <span aria-hidden="true">★</span>
                        <span class="sr-only">{{ value }} {{ value === 1 ? 'star' : 'stars' }}</span>
                      </label>
                    }
                  </div>
                  @if (error('rating'); as message) {
                    <p class="acc-error" id="rv-rating-error" data-testid="review-error-rating">{{ message }}</p>
                  }
                </fieldset>

                <div class="acc-field">
                  <label class="acc-label" for="rv-title-input">Title <span class="acc-optional">(optional)</span></label>
                  <input
                    class="acc-input"
                    id="rv-title-input"
                    name="title"
                    type="text"
                    autocomplete="off"
                    data-testid="review-title"
                    [attr.maxlength]="limits.titleMax"
                    [attr.aria-invalid]="error('title') ? 'true' : null"
                    [attr.aria-describedby]="error('title') ? 'rv-title-error' : null"
                    [value]="draft().title"
                    (input)="patch({ title: read($event) })"
                  />
                  @if (error('title'); as message) {
                    <p class="acc-error" id="rv-title-error" data-testid="review-error-title">{{ message }}</p>
                  }
                </div>

                <div class="acc-field">
                  <label class="acc-label" for="rv-body-input">Your review <span class="acc-optional">(optional)</span></label>
                  <textarea
                    class="acc-input rv__textarea"
                    id="rv-body-input"
                    name="body"
                    rows="5"
                    data-testid="review-body"
                    [attr.maxlength]="limits.bodyMax"
                    [attr.aria-invalid]="error('body') ? 'true' : null"
                    aria-describedby="rv-body-hint"
                    [value]="draft().body"
                    (input)="patch({ body: read($event) })"
                  ></textarea>
                  <p class="acc-hint" id="rv-body-hint">
                    Plain text, up to 4,000 characters. Your first name and the initial of your last name are shown with it.
                  </p>
                  @if (error('body'); as message) {
                    <p class="acc-error" data-testid="review-error-body">{{ message }}</p>
                  }
                </div>

                <div class="acc-actions rv__actions">
                  <button class="btn btn--primary" type="submit" data-testid="review-submit" [attr.aria-disabled]="saving() ? 'true' : null">
                    {{ saving() ? 'Sending…' : editing() ? 'Save changes' : 'Send review' }}
                  </button>
                  <button class="btn btn--secondary" type="button" data-testid="review-cancel" (click)="closeForm()">Cancel</button>
                </div>
              </form>
            }

            @if (answered.items.length > 0) {
              <ul class="rv__list" aria-label="Customer reviews" data-testid="reviews-list">
                @for (review of items(); track review.id) {
                  <li class="rv__item" [attr.data-testid]="'review-' + review.id">
                    <p class="rv__item-stars">
                      <span class="rv__stars" aria-hidden="true">{{ stars(review.rating) }}</span>
                      <span class="sr-only">Rated {{ review.rating }} out of 5</span>
                    </p>
                    @if (review.title) {
                      <h3 class="rv__item-title">{{ review.title }}</h3>
                    }
                    @if (review.body) {
                      <p class="rv__item-body" data-testid="review-text">{{ review.body }}</p>
                    }
                    <p class="rv__item-meta">
                      <bdi class="rv__name" data-testid="review-name">{{ review.displayName }}</bdi>
                      @if (review.isVerifiedBuyer) {
                        <span class="rv__verified" data-testid="review-verified">Verified buyer</span>
                      }
                      <span class="rv__muted">{{ date(review.createdAtUtc) }}</span>
                    </p>
                  </li>
                }
              </ul>
              @if (items().length < answered.totalCount) {
                <button class="btn btn--secondary rv__more" type="button" data-testid="reviews-more" [attr.aria-disabled]="loadingMore() ? 'true' : null" (click)="more()">
                  {{ loadingMore() ? 'Loading…' : 'Show more reviews' }}
                </button>
              }
            }
          </div>
        </div>
      } @else if (failed()) {
        <p class="rv__none" data-testid="reviews-failed">
          We could not load the reviews just now.
          <button class="btn btn--secondary" type="button" (click)="reload()">Try again</button>
        </p>
      } @else {
        <p class="rv__none">Loading reviews…</p>
      }
    </section>
  `,
  styles: `
    :host { display: block; }
    .rv { display: grid; gap: var(--space-3); padding-top: var(--space-5, 3rem); border-top: 1px solid var(--color-border); min-width: 0; }
    .rv__title { margin: 0; font-family: var(--font-display, inherit); font-size: var(--text-h2); font-weight: var(--weight-regular, 400); }
    .rv__layout { display: grid; gap: var(--space-4, 2rem); grid-template-columns: minmax(0, 1fr); }
    @media (min-width: 768px) { .rv__layout { grid-template-columns: minmax(0, 18rem) minmax(0, 1fr); } }
    .rv__summary, .rv__main { display: grid; gap: var(--space-2); align-content: start; min-width: 0; }
    .rv__average { display: flex; align-items: baseline; gap: var(--space-1); margin: 0; }
    .rv__average-number { font-size: var(--text-h2); line-height: 1; }
    .rv__stars { letter-spacing: 0.12em; }
    .rv__count, .rv__none { margin: 0; font-size: var(--text-small); }
    .rv__bars { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--space-0-5); }
    .rv__bar-row { display: grid; grid-template-columns: 3.5rem minmax(0, 1fr) 2.5rem; gap: var(--space-1); align-items: center; font-size: var(--text-small); }
    .rv__bar { display: block; height: 0.5rem; background: var(--color-bg-alt); }
    .rv__bar-fill { display: block; height: 100%; background: var(--color-text); }
    .rv__bar-count { text-align: end; }
    .rv__write { display: grid; gap: var(--space-1); justify-items: start; }
    .rv__prompt { margin: 0; }
    .rv__form { padding: var(--space-2); border: 1px solid var(--color-border); }
    .rv__form-title { margin: 0; font-size: var(--text-body); font-weight: var(--weight-medium); }
    .rv__pick { display: flex; flex-wrap: wrap; gap: var(--space-0-5); }
    .rv__radio { position: absolute; opacity: 0; width: 1px; height: 1px; pointer-events: none; }
    .rv__star { display: inline-flex; align-items: center; justify-content: center; min-width: var(--tap, 44px); min-height: var(--tap, 44px); font-size: 1.5rem; color: var(--color-border-strong); cursor: pointer; }
    .rv__star.is-on { color: var(--color-text); }
    .rv__radio:focus-visible + .rv__star { outline: 2px solid var(--color-text); outline-offset: 2px; }
    .rv__textarea { resize: vertical; min-height: 7rem; }
    .rv__actions { flex-wrap: wrap; }
    .rv__list { list-style: none; margin: 0; padding: 0; display: grid; }
    .rv__item { display: grid; gap: var(--space-1); padding-block: var(--space-2); border-bottom: 1px solid var(--color-border); overflow-wrap: anywhere; }
    .rv__item:first-child { padding-top: 0; }
    .rv__item-stars, .rv__item-meta { margin: 0; }
    .rv__item-title { margin: 0; font-size: var(--text-body); font-weight: var(--weight-medium); }
    .rv__item-body { margin: 0; white-space: pre-line; line-height: 1.6; }
    .rv__item-meta { display: flex; flex-wrap: wrap; gap: var(--space-0-5) var(--space-1); font-size: var(--text-small); }
    .rv__verified { padding: 0 var(--space-0-5); border: 1px solid currentColor; font-size: var(--text-label); letter-spacing: var(--tracking-label); text-transform: uppercase; }
    /* A name is isolated from the badge and date beside it, so no character in it can reorder them (#189 D1). */
    .rv__name { unicode-bidi: isolate; }
    .rv__muted { color: var(--color-text-muted); }
    .rv__more { justify-self: start; }
  `,
})
export class ProductReviews {
  readonly productId = input.required<number>();
  readonly slug = input.required<string>();

  protected readonly account = inject(AccountStore);
  private readonly api = inject(ReviewsApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);

  protected readonly limits = REVIEW_LIMITS;
  protected readonly ratingValues = [1, 2, 3, 4, 5];

  protected readonly page = signal<ProductReviewPage | null>(null);
  protected readonly items = signal<readonly PublicReview[]>([]);
  protected readonly failed = signal(false);
  protected readonly loadingMore = signal(false);
  protected readonly mine = signal<MyProductReview | null>(null);
  protected readonly formOpen = signal(false);
  protected readonly draft = signal<ReviewDraft>(EMPTY_DRAFT);
  protected readonly errors = signal<ReadonlyMap<string, string>>(new Map());
  protected readonly saving = signal(false);
  protected readonly said = signal('');
  protected readonly saidIsError = signal(false);

  protected readonly editing = computed(() => this.mine()?.review != null);
  protected readonly returnTo = computed(() => this.router.url.split('#')[0]);

  private pageSubscription: Subscription | null = null;
  private mineFor: number | null = null;

  constructor() {
    // The list follows the product: a client-side navigation from one product to another reuses this component.
    effect(() => {
      const slug = this.slug();
      untracked(() => this.loadFirstPage(slug));
    });

    // The customer's own review, only in the browser and only once signed in.
    effect(() => {
      const signedIn = this.account.isSignedIn();
      const productId = this.productId();

      untracked(() => {
        if (!signedIn) {
          this.mine.set(null);
          this.mineFor = null;
          this.formOpen.set(false);
          return;
        }

        if (this.mineFor !== productId) {
          this.loadMine(productId);
        }
      });
    });

    afterNextRender(() => {
      this.account.load().subscribe();
    });

    inject(DestroyRef).onDestroy(() => this.pageSubscription?.unsubscribe());
  }

  protected stars(rating: number | null): string {
    const whole = Math.max(0, Math.min(5, Math.round(rating ?? 0)));
    return '★'.repeat(whole) + '☆'.repeat(5 - whole);
  }

  protected format(average: number | null): string {
    return average === null ? '' : average.toFixed(1);
  }

  protected share(count: number, total: number): number {
    return total === 0 ? 0 : Math.round((count / total) * 100);
  }

  protected date(iso: string): string {
    const value = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(iso) ? iso : `${iso}Z`);
    return Number.isNaN(value.getTime()) ? '' : DATE.format(value);
  }

  protected statusText(status: 'Pending' | 'Approved' | 'Hidden'): string {
    return statusMessage(status);
  }

  protected error(field: string): string | null {
    return this.errors().get(field) ?? null;
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement | null)?.value ?? '';
  }

  protected patch(change: Partial<ReviewDraft>): void {
    this.draft.update((current) => ({ ...current, ...change }));

    const touched = Object.keys(change);
    if (touched.some((field) => this.errors().has(field))) {
      const next = new Map(this.errors());
      touched.forEach((field) => next.delete(field));
      this.errors.set(next);
    }
  }

  protected openForm(): void {
    const review = this.mine()?.review;
    this.draft.set(review ? { rating: review.rating, title: review.title ?? '', body: review.body ?? '' } : EMPTY_DRAFT);
    this.errors.set(new Map());
    this.say('', false);
    this.formOpen.set(true);

    afterNextRender(() => this.document.getElementById('rv-form-title')?.focus(), { injector: this.injector });
  }

  protected closeForm(): void {
    this.formOpen.set(false);
    this.errors.set(new Map());
  }

  protected submit(event: Event): void {
    event.preventDefault();

    if (this.saving()) {
      return;
    }

    const problems = draftProblems(this.draft());
    this.errors.set(problems);

    if (problems.size > 0) {
      this.say('Please correct the highlighted fields.', true);
      return;
    }

    const editing = this.editing();
    const productId = this.productId();
    this.saving.set(true);

    (editing ? this.api.edit(productId, this.draft()) : this.api.create(productId, this.draft())).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.mine.set({ review: saved.review, wouldBeVerified: this.mine()?.wouldBeVerified ?? saved.review.isVerifiedBuyer });
        this.formOpen.set(false);
        this.say(submissionMessage(saved.outcome, editing), false);

        if (saved.outcome === 'Published' || editing) {
          this.loadFirstPage(this.slug());
        }
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.handleFailure(error);
      },
    });
  }

  protected more(): void {
    const answered = this.page();

    if (!answered || this.loadingMore()) {
      return;
    }

    const nextPage = Math.floor(this.items().length / REVIEWS_PAGE_SIZE) + 1;
    this.loadingMore.set(true);

    this.api.page(this.slug(), nextPage, REVIEWS_PAGE_SIZE).subscribe({
      next: (next) => {
        this.loadingMore.set(false);
        const known = new Set(this.items().map((item) => item.id));
        this.items.update((current) => [...current, ...next.items.filter((item) => !known.has(item.id))]);
        this.page.set(next);
      },
      error: () => {
        this.loadingMore.set(false);
        this.say('We could not load more reviews just now. Please try again.', true);
      },
    });
  }

  protected reload(): void {
    this.loadFirstPage(this.slug());
  }

  private loadFirstPage(slug: string): void {
    this.pageSubscription?.unsubscribe();
    this.failed.set(false);

    this.pageSubscription = this.api.page(slug, 1, REVIEWS_PAGE_SIZE).subscribe({
      next: (answered) => {
        this.page.set(answered);
        this.items.set(answered.items);
      },
      error: () => {
        this.page.set(null);
        this.items.set([]);
        this.failed.set(true);
      },
    });
  }

  private loadMine(productId: number): void {
    this.mineFor = productId;

    this.api.mine(productId).subscribe({
      next: (mine) => {
        if (this.mineFor === productId) {
          this.mine.set(mine);
        }
      },
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 401) {
          this.account.accept(null);
        }

        this.mineFor = null;
      },
    });
  }

  private handleFailure(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.say('We could not send your review just now. Please try again.', true);
      return;
    }

    const body = (error.error ?? {}) as { code?: string; detail?: string; review?: MyProductReview['review'] };

    if (error.status === 400) {
      const fields = fieldErrorsOf(error);
      this.errors.set(fields);
      this.say(fields.size > 0 ? 'Please correct the highlighted fields.' : (body.detail ?? 'Please check your review and try again.'), true);
      return;
    }

    if (error.status === 409 && body.code === 'already_reviewed' && body.review) {
      // Another tab got there first: offer the saved review to edit.
      this.mine.set({ review: body.review, wouldBeVerified: this.mine()?.wouldBeVerified ?? false });
      this.draft.set({ rating: body.review.rating, title: body.review.title ?? '', body: body.review.body ?? '' });
      this.say('You have already reviewed this product. Your saved review is in the form below — edit it if you like.', true);
      return;
    }

    if (error.status === 401) {
      this.account.accept(null);
      this.formOpen.set(false);
      this.say('Your session has ended. Please sign in again to write a review.', true);
      return;
    }

    if (error.status === 404) {
      this.say(body.code === 'not_reviewed' ? 'There is no review of yours to edit.' : 'This product can no longer be reviewed.', true);
      return;
    }

    if (error.status === 429) {
      this.say(body.detail ?? 'You have written a lot of reviews recently. Please try again later.', true);
      return;
    }

    this.say(
      error.status === 0
        ? 'We could not reach the store. Check your connection and try again.'
        : 'We could not send your review just now. Please try again.',
      true,
    );
  }

  private say(text: string, isError: boolean): void {
    this.saidIsError.set(isError);
    this.said.set(text);
  }
}
