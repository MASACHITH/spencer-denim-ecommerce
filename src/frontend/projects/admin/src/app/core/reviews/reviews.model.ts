/**
 * Review moderation (ADM-16, doc 11 P12; issue #189). Field for field the server's `AdminReviewDto`,
 * `AdminReviewListPage` and `AdminModerationResponse`.
 */

/** Doc 11 P12's permissions: the list, approving (and approving a hidden review again), hiding. */
export const REVIEW_PERMISSIONS = {
  view: 'Reviews.View',
  approve: 'Reviews.Approve',
  edit: 'Reviews.Edit',
} as const;

export type ReviewStatus = 'Pending' | 'Approved' | 'Hidden';

export type ReviewStatusFilter = ReviewStatus | 'All';

export const REVIEW_STATUS_FILTERS: readonly ReviewStatusFilter[] = ['Pending', 'Approved', 'Hidden', 'All'];

/** `?verified=`: every review, only verified buyers, or only reviews without the badge. */
export type ReviewVerifiedFilter = 'Any' | 'Verified' | 'Unverified';

export interface AdminReview {
  readonly id: number;
  readonly productId: number;
  readonly productName: string;
  readonly productCode: string;
  readonly productSlug: string;
  readonly rating: number;
  readonly title: string | null;
  readonly body: string | null;
  readonly displayName: string;
  readonly isVerifiedBuyer: boolean;
  readonly status: ReviewStatus;
  readonly createdAtUtc: string;
  readonly editedAtUtc: string | null;
  readonly moderatedAtUtc: string | null;
  readonly moderatedByName: string | null;
  readonly moderationNote: string | null;
  readonly rowVersion: string;
}

export interface ReviewStatusCounts {
  readonly pending: number;
  readonly approved: number;
  readonly hidden: number;
}

export interface AdminReviewListPage {
  readonly items: readonly AdminReview[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly counts: ReviewStatusCounts;
}

export interface AdminModerationResponse {
  readonly review: AdminReview;
  readonly changed: boolean;
}

export interface ReviewQuery {
  readonly status: ReviewStatusFilter;
  readonly verified: ReviewVerifiedFilter;
  readonly search: string;
  readonly page: number;
  readonly pageSize: number;
}

export type ModerationAction = 'approve' | 'hide';

/** The server's moderation-note limit (`ReviewRules.ModerationNoteMaxLength`). */
export const MODERATION_NOTE_MAX = 500;

export const DEFAULT_REVIEW_QUERY: ReviewQuery = {
  status: 'Pending',
  verified: 'Any',
  search: '',
  page: 1,
  pageSize: 25,
};

/** What each action may move a review from (doc 11 P12: Pending → Approved ↔ Hidden, or Pending → Hidden). */
export function canModerate(action: ModerationAction, status: ReviewStatus): boolean {
  return action === 'approve' ? status !== 'Approved' : status !== 'Hidden';
}
