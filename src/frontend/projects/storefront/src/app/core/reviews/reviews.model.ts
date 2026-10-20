/**
 * Reviews and ratings on the storefront (PDP-08; doc 11 P12; issue #189). Field for field the server's
 * `ProductReviewPageDto`, `MyProductReviewDto` and `ReviewSubmissionDto`.
 *
 * **Title and body are plain text.** Every template renders them by interpolation, which escapes them; nothing
 * binds them to `innerHTML`.
 */

export interface RatingBucket {
  readonly stars: number;
  readonly count: number;
}

export interface ReviewSummary {
  readonly average: number | null;
  readonly count: number;
  /** 5 stars down to 1. */
  readonly distribution: readonly RatingBucket[];
}

export interface PublicReview {
  readonly id: number;
  readonly rating: number;
  readonly title: string | null;
  readonly body: string | null;
  readonly displayName: string;
  readonly isVerifiedBuyer: boolean;
  readonly createdAtUtc: string;
  readonly editedAtUtc: string | null;
}

export interface ProductReviewPage {
  readonly summary: ReviewSummary;
  readonly items: readonly PublicReview[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
}

export type ReviewStatus = 'Pending' | 'Approved' | 'Hidden';

export interface MyReview {
  readonly id: number;
  readonly productId: number;
  readonly rating: number;
  readonly title: string | null;
  readonly body: string | null;
  readonly displayName: string;
  readonly isVerifiedBuyer: boolean;
  readonly status: ReviewStatus;
  readonly createdAtUtc: string;
  readonly editedAtUtc: string | null;
}

export interface MyProductReview {
  readonly review: MyReview | null;
  readonly wouldBeVerified: boolean;
}

export type ReviewSubmissionOutcome = 'Pending' | 'Published' | 'StillHidden';

export interface ReviewSubmission {
  readonly review: MyReview;
  readonly outcome: ReviewSubmissionOutcome;
}

export interface ReviewDraft {
  readonly rating: number | null;
  readonly title: string;
  readonly body: string;
}

/** The server's limits (`ReviewRules`), for the form's own pre-check and the counters. */
export const REVIEW_LIMITS = {
  titleMax: 120,
  bodyMax: 4000,
} as const;

/** How many reviews a page of the list loads. */
export const REVIEWS_PAGE_SIZE = 10;

/** What the customer is told after a save, by outcome. */
export function submissionMessage(outcome: ReviewSubmissionOutcome, edited: boolean): string {
  switch (outcome) {
    case 'Published':
      return edited ? 'Thank you. Your changes are now on this page.' : 'Thank you. Your review is now on this page.';
    case 'StillHidden':
      return 'Your changes are saved. This review is not shown on the site.';
    default:
      return edited
        ? 'Thank you. Your changes will appear once we have checked them.'
        : 'Thank you. Your review will appear once we have checked it.';
  }
}

/** What the customer's own review's status means, in their words. */
export function statusMessage(status: ReviewStatus): string {
  switch (status) {
    case 'Approved':
      return 'Your review is shown on this page.';
    case 'Hidden':
      return 'Your review is not shown on the site.';
    default:
      return 'Your review is waiting to be checked before it appears.';
  }
}

/**
 * The form's own checks, the server's rules where the browser can know them: a rating is chosen, the title is
 * one line within 120 characters and the body within 4,000 (both after trimming). The server still decides.
 */
export function draftProblems(draft: ReviewDraft): ReadonlyMap<string, string> {
  const problems = new Map<string, string>();

  if (draft.rating === null || !Number.isInteger(draft.rating) || draft.rating < 1 || draft.rating > 5) {
    problems.set('rating', 'Choose a star rating from 1 to 5.');
  }

  const title = draft.title.trim();
  if (/[\r\n]/.test(title)) {
    problems.set('title', 'Write the title on one line.');
  } else if (title.length > REVIEW_LIMITS.titleMax) {
    problems.set('title', `Keep this to ${REVIEW_LIMITS.titleMax} characters or fewer (it is ${title.length}).`);
  }

  const body = draft.body.replace(/\r\n?/g, '\n').trim();
  if (body.length > REVIEW_LIMITS.bodyMax) {
    problems.set('body', `Keep this to 4,000 characters or fewer (it is ${body.length.toLocaleString('en-GB')}).`);
  }

  return problems;
}

/** The body the API takes: empty text is sent as null. */
export function toRequest(draft: ReviewDraft): { rating: number | null; title: string | null; body: string | null } {
  const title = draft.title.trim();
  const body = draft.body.trim();

  return { rating: draft.rating, title: title === '' ? null : title, body: body === '' ? null : body };
}
