import { ChangeDetectionStrategy, Component, Injector, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { ReviewsApi } from '../../core/reviews/reviews-api';
import {
  AdminReview,
  AdminReviewListPage,
  DEFAULT_REVIEW_QUERY,
  MODERATION_NOTE_MAX,
  ModerationAction,
  REVIEW_PERMISSIONS,
  REVIEW_STATUS_FILTERS,
  ReviewQuery,
  ReviewStatus,
  ReviewStatusFilter,
  canModerate,
} from '../../core/reviews/reviews.model';
import { Announcer, showing } from '../reports/report-shared';

interface Access {
  readonly view: boolean;
  readonly approve: boolean;
  readonly hide: boolean;
}

/** A sentence beside one review: what the last action on it did, or why it did nothing. */
interface CardNote {
  readonly text: string;
  readonly error: boolean;
}

const STATUS_LABELS: Readonly<Record<ReviewStatusFilter, string>> = {
  Pending: 'Waiting for approval',
  Approved: 'Shown on the site',
  Hidden: 'Hidden',
  All: 'Every review',
};

const PILL_LABELS: Readonly<Record<ReviewStatus, string>> = {
  Pending: 'Pending',
  Approved: 'Approved',
  Hidden: 'Hidden',
};

const DATE = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'Asia/Colombo' });

/**
 * Review moderation (ADM-16; doc 11 P12 steps 4–6; issue #189).
 *
 * **Cards, not a table.** A review is a paragraph of someone's words, and a table column narrow enough to sit
 * beside six others at 320 px is unreadable; each card stacks the product, the stars, the text and the actions.
 *
 * **What a role cannot do is not offered** (the API enforces the same): `Reviews.View` sees the list, Approve
 * appears only with `Reviews.Approve` and Hide only with `Reviews.Edit`.
 *
 * **Review text is shown as text.** Interpolation escapes it, so markup a customer typed is visible as markup and
 * never runs; line breaks are kept with `white-space: pre-line`.
 *
 * **An action patches its card from the server's answer** and leaves it where it is, so the moderator sees what
 * happened even when the review no longer matches the filter. A 409 means somebody changed it meanwhile; the
 * card is replaced with the review as it now is and says so.
 */
@Component({
  selector: 'sda-reviews-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="rev">
      <h1 class="rev__title">Reviews</h1>
      <p class="sr-only" role="status" data-testid="reviews-said">{{ announcer.text() }}</p>

      @if (access() === null) {
        <p class="adm-muted">Loading…</p>
      } @else if (!access()!.view) {
        <p class="adm-state" data-testid="reviews-forbidden">
          Your role does not include reviews. <a class="adm-link" routerLink="/">Back to the dashboard</a>
        </p>
      } @else {
        <section class="adm-panel" aria-labelledby="rev-find">
          <h2 class="adm-panel__title" id="rev-find">Find reviews</h2>
          <form class="rev__filters" role="search" aria-labelledby="rev-find" (submit)="apply($event)">
            <label class="adm-field">
              <span class="adm-label">Show</span>
              <select class="adm-input" name="status" data-testid="reviews-status" (change)="edit({ status: $any(read($event)) })">
                @for (option of statuses; track option) {
                  <option [value]="option" [selected]="draft().status === option">{{ statusLabel(option) }}</option>
                }
              </select>
            </label>
            <label class="adm-field">
              <span class="adm-label">Buyer</span>
              <select class="adm-input" name="verified" data-testid="reviews-verified" (change)="edit({ verified: $any(read($event)) })">
                <option value="Any" [selected]="draft().verified === 'Any'">Everyone</option>
                <option value="Verified" [selected]="draft().verified === 'Verified'">Verified buyers</option>
                <option value="Unverified" [selected]="draft().verified === 'Unverified'">Not verified</option>
              </select>
            </label>
            <label class="adm-field">
              <span class="adm-label">Product, reviewer or words</span>
              <input class="adm-input" type="search" name="search" maxlength="100" autocomplete="off" data-testid="reviews-search" [value]="draft().search" (input)="edit({ search: read($event) })" />
            </label>
            <div class="adm-actions rev__go">
              <button class="adm-btn--primary" type="submit" data-testid="reviews-apply">Show</button>
              <button class="adm-btn" type="button" data-testid="reviews-clear" (click)="clear()">Clear</button>
            </div>
          </form>
          @if (page(); as answered) {
            <p class="adm-panel__hint" data-testid="reviews-counts">
              {{ answered.counts.pending }} waiting · {{ answered.counts.approved }} shown · {{ answered.counts.hidden }} hidden.
              Only shown reviews count towards a product's rating.
            </p>
          }
          @if (!access()!.approve && !access()!.hide) {
            <p class="adm-muted" data-testid="reviews-read-only">Your role can read reviews but not approve or hide them.</p>
          }
        </section>

        @if (problem(); as failure) {
          <div class="adm-state adm-state--error" role="alert" data-testid="reviews-problem">
            <p>{{ failure.message }}</p>
            @if (failure.kind !== 'forbidden') {
              <div class="adm-actions"><button class="adm-btn" type="button" (click)="load(query().page)">Try again</button></div>
            }
          </div>
        } @else if (page(); as answered) {
          @if (answered.totalCount === 0) {
            <p class="adm-muted" data-testid="reviews-empty">No review matches these filters.</p>
          } @else {
            <ul class="rev__list" aria-label="Reviews">
              @for (review of answered.items; track review.id) {
                <li class="adm-panel rev__card" [attr.data-testid]="'review-' + review.id">
                  <div class="rev__head">
                    <p class="rev__product">
                      <strong>{{ review.productName }}</strong>
                      <span class="adm-muted"> · {{ review.productCode }}</span>
                    </p>
                    <span class="adm-pill" [class.adm-pill--hidden]="review.status === 'Hidden'" [class.rev__pending]="review.status === 'Pending'" [attr.data-testid]="'review-status-' + review.id">{{ pillLabel(review.status) }}</span>
                  </div>
                  <p class="rev__stars">
                    <span aria-hidden="true">{{ stars(review.rating) }}</span>
                    <span class="sr-only">Rated {{ review.rating }} out of 5</span>
                  </p>
                  @if (review.title) {
                    <h2 class="rev__review-title" [attr.data-testid]="'review-title-' + review.id">{{ review.title }}</h2>
                  }
                  @if (review.body) {
                    <p class="rev__body" [attr.data-testid]="'review-body-' + review.id">{{ review.body }}</p>
                  }
                  <p class="adm-muted rev__meta">
                    <bdi class="rev__name" [attr.data-testid]="'review-name-' + review.id">{{ review.displayName }}</bdi>
                    @if (review.isVerifiedBuyer) {
                      <span class="rev__verified" [attr.data-testid]="'review-verified-' + review.id">Verified buyer</span>
                    }
                    · {{ date(review.createdAtUtc) }}{{ review.editedAtUtc ? ' · edited ' + date(review.editedAtUtc) : '' }}
                  </p>
                  @if (review.moderatedAtUtc) {
                    <p class="adm-muted rev__meta" [attr.data-testid]="'review-moderated-' + review.id">
                      Last decided {{ date(review.moderatedAtUtc) }}{{ review.moderatedByName ? ' by ' + review.moderatedByName : '' }}{{ review.moderationNote ? ': ' + review.moderationNote : '' }}
                    </p>
                  }

                  @if (access()!.approve || access()!.hide) {
                    <div class="rev__decide">
                      <label class="adm-field rev__note">
                        <span class="adm-label">Note for staff (optional)</span>
                        <textarea class="adm-input" rows="2" [attr.maxlength]="noteMax" [attr.data-testid]="'review-note-' + review.id" [value]="noteFor(review.id)" (input)="setNote(review.id, read($event))" [attr.aria-describedby]="'review-note-hint-' + review.id"></textarea>
                        <span class="adm-panel__hint" [id]="'review-note-hint-' + review.id">Kept on the review; the customer never sees it.</span>
                      </label>
                      <div class="adm-actions">
                        @if (access()!.approve && canModerate('approve', review.status)) {
                          <button class="adm-btn--primary" type="button" [attr.data-testid]="'approve-' + review.id" [attr.aria-disabled]="busy() === review.id ? 'true' : null" (click)="moderate(review, 'approve')">
                            {{ review.status === 'Hidden' ? 'Show again' : 'Approve' }}
                          </button>
                        }
                        @if (access()!.hide && canModerate('hide', review.status)) {
                          <button class="adm-btn" type="button" [attr.data-testid]="'hide-' + review.id" [attr.aria-disabled]="busy() === review.id ? 'true' : null" (click)="moderate(review, 'hide')">Hide</button>
                        }
                      </div>
                    </div>
                  }
                  @if (cardNotes()[review.id]; as note) {
                    <p class="rev__said" [class.rev__said--error]="note.error" [attr.role]="note.error ? 'alert' : null" [attr.data-testid]="'review-said-' + review.id">{{ note.text }}</p>
                  }
                </li>
              }
            </ul>

            <nav class="adm-actions rev__paging" aria-label="Pages">
              <button class="adm-btn" type="button" data-testid="reviews-prev" [attr.aria-disabled]="answered.page <= 1 ? 'true' : null" (click)="go(answered.page - 1)">Previous</button>
              <p class="adm-muted" data-testid="reviews-paging">{{ range(answered) }}</p>
              <button class="adm-btn" type="button" data-testid="reviews-next" [attr.aria-disabled]="answered.page * answered.pageSize >= answered.totalCount ? 'true' : null" (click)="go(answered.page + 1)">Next</button>
            </nav>
          }
        } @else {
          <p class="adm-muted">Loading reviews…</p>
        }
      }
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .rev { display: grid; gap: var(--space-3); max-width: 1100px; margin-inline: auto; min-width: 0; }
    .rev > *, .rev .adm-panel > * { min-width: 0; }
    .rev__title { font-size: var(--text-h2); }
    .rev__filters { display: grid; gap: var(--space-2); align-items: end; grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr)); }
    .rev__go, .rev__paging { align-items: center; }
    .rev__list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--space-2); }
    .rev__card { display: grid; gap: var(--space-1); overflow-wrap: anywhere; }
    .rev__head { display: flex; flex-wrap: wrap; justify-content: space-between; gap: var(--space-1); align-items: baseline; }
    .rev__product { margin: 0; }
    .rev__stars { margin: 0; letter-spacing: 0.1em; }
    .rev__review-title { margin: 0; font-size: var(--text-body); font-weight: 600; }
    .rev__body { margin: 0; white-space: pre-line; }
    .rev__meta { margin: 0; font-size: var(--text-small); }
    .rev__verified { display: inline-block; margin-inline-start: var(--space-0-5); padding: 0 var(--space-0-5); border: 1px solid currentColor; font-size: var(--text-small); }
    /* Isolated from the badge and date beside it (#189 D1). */
    .rev__name { unicode-bidi: isolate; }
    .rev__pending { color: var(--color-warning); border-color: var(--color-warning); }
    .rev__decide { display: grid; gap: var(--space-1); }
    .rev__note textarea { resize: vertical; }
    .rev__said { margin: 0; }
    .rev__said--error { color: var(--color-sale); }
  `,
})
export class ReviewsPage {
  private readonly api = inject(ReviewsApi);
  private readonly auth = inject(AdminAuthApi);
  private readonly injector = inject(Injector);

  protected readonly announcer = new Announcer(this.injector);
  protected readonly statuses = REVIEW_STATUS_FILTERS;
  protected readonly noteMax = MODERATION_NOTE_MAX;
  protected readonly canModerate = canModerate;

  protected readonly access = signal<Access | null>(null);
  protected readonly draft = signal<ReviewQuery>(DEFAULT_REVIEW_QUERY);
  protected readonly query = signal<ReviewQuery>(DEFAULT_REVIEW_QUERY);
  protected readonly page = signal<AdminReviewListPage | null>(null);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly busy = signal<number | null>(null);
  protected readonly cardNotes = signal<Readonly<Record<number, CardNote>>>({});

  private readonly notes = signal<Readonly<Record<number, string>>>({});
  private requests = 0;

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) => {
        const holds = (permission: string) => session.grantsAllPermissions || session.permissions.includes(permission);
        this.start({
          view: holds(REVIEW_PERMISSIONS.view),
          approve: holds(REVIEW_PERMISSIONS.approve),
          hide: holds(REVIEW_PERMISSIONS.edit),
        });
      },
      // The session could not be read: let the API decide, as the other screens do.
      error: () => this.start({ view: true, approve: false, hide: false }),
    });
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement | null)?.value ?? '';
  }

  protected statusLabel(status: ReviewStatusFilter): string {
    return STATUS_LABELS[status];
  }

  protected pillLabel(status: ReviewStatus): string {
    return PILL_LABELS[status];
  }

  protected stars(rating: number): string {
    const whole = Math.max(0, Math.min(5, Math.round(rating)));
    return '★'.repeat(whole) + '☆'.repeat(5 - whole);
  }

  protected date(iso: string): string {
    const value = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : `${iso}Z`);
    return Number.isNaN(value.getTime()) ? '' : DATE.format(value);
  }

  protected range(answered: AdminReviewListPage): string {
    return showing(answered.page, answered.pageSize, answered.totalCount);
  }

  protected edit(change: Partial<ReviewQuery>): void {
    this.draft.update((current) => ({ ...current, ...change }));
  }

  protected noteFor(reviewId: number): string {
    return this.notes()[reviewId] ?? '';
  }

  protected setNote(reviewId: number, value: string): void {
    this.notes.update((current) => ({ ...current, [reviewId]: value }));
  }

  protected apply(event: Event): void {
    event.preventDefault();
    this.query.set({ ...this.draft(), page: 1 });
    this.load(1);
  }

  protected clear(): void {
    this.draft.set(DEFAULT_REVIEW_QUERY);
    this.query.set(DEFAULT_REVIEW_QUERY);
    this.load(1);
  }

  protected go(target: number): void {
    const answered = this.page();

    if (!answered || target < 1 || (target - 1) * answered.pageSize >= answered.totalCount) {
      return;
    }

    this.load(target);
  }

  load(target: number): void {
    const request = ++this.requests;
    const query = { ...this.query(), page: target };
    this.query.set(query);
    this.problem.set(null);

    this.api.list(query).subscribe({
      next: (answered) => {
        if (request !== this.requests) {
          return;
        }

        this.page.set(answered);
        this.cardNotes.set({});
        this.announcer.say(`${answered.totalCount} ${answered.totalCount === 1 ? 'review' : 'reviews'}. Showing ${this.range(answered)}.`);
      },
      error: (error: unknown) => {
        if (request === this.requests) {
          const failure = toCatalogFailure(error);
          this.page.set(null);
          this.problem.set(failure);
          this.announcer.say(failure.message);
        }
      },
    });
  }

  protected moderate(review: AdminReview, action: ModerationAction): void {
    if (this.busy() !== null) {
      return;
    }

    const note = this.noteFor(review.id).trim();

    if (note.length > MODERATION_NOTE_MAX) {
      this.say(review.id, `Keep the note to ${MODERATION_NOTE_MAX} characters or fewer.`, true);
      return;
    }

    this.busy.set(review.id);

    this.api.moderate(review.id, action, review.rowVersion, note === '' ? null : note).subscribe({
      next: (answer) => {
        this.busy.set(null);
        this.patch(answer.review);
        this.notes.update((current) => ({ ...current, [review.id]: '' }));

        const text = !answer.changed
          ? 'It was already like that; nothing changed.'
          : action === 'approve'
            ? 'Approved. It is shown on the product page and counts towards the rating.'
            : 'Hidden. It is off the product page and no longer counts towards the rating.';

        this.say(review.id, text, false);
      },
      error: (error: unknown) => {
        this.busy.set(null);
        const failure = toCatalogFailure(error);
        const current = failure.details?.['review'] as AdminReview | undefined;

        if (failure.kind === 'conflict' && current) {
          this.patch(current);
          this.say(review.id, 'Somebody changed this review since the page loaded — another moderator, or its author. Nothing was changed. Check it again and decide.', true);
          return;
        }

        const message = failure.fieldErrors
          ? Object.values(failure.fieldErrors).flat()[0] ?? failure.message
          : failure.message;

        this.say(review.id, message, true);
      },
    });
  }

  private patch(review: AdminReview): void {
    this.page.update((answered) =>
      answered ? { ...answered, items: answered.items.map((item) => (item.id === review.id ? review : item)) } : answered,
    );
  }

  private say(reviewId: number, text: string, error: boolean): void {
    this.cardNotes.update((current) => ({ ...current, [reviewId]: { text, error } }));
    this.announcer.say(text);
  }

  private start(access: Access): void {
    this.access.set(access);

    if (access.view) {
      this.load(1);
    }
  }
}

