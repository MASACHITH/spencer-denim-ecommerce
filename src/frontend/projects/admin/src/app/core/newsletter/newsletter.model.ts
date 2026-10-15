/**
 * The newsletter subscriber screen's payloads (ADM-21, doc 11 P15 step 4), field for field with
 * `SpencerDenim.Application.Marketing.Newsletter.Admin`. `AdminCustomerShapeTests` pins the pairing.
 */

/** `marketing.NewsletterSubscribers.Status`, by name. */
export type NewsletterSubscriberStatus = 'Subscribed' | 'Unsubscribed';

export const NEWSLETTER_STATUSES: readonly NewsletterSubscriberStatus[] = ['Subscribed', 'Unsubscribed'];

/** One page of the list. The two counts are over the whole list and ignore the filters. */
export interface NewsletterSubscriberPage {
  readonly rows: readonly NewsletterSubscriberRow[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly subscribedCount: number;
  readonly unsubscribedCount: number;
}

/** One subscriber. No IP address: it is proof of consent, not something a list shows. */
export interface NewsletterSubscriberRow {
  readonly subscriberId: number;
  readonly email: string;
  readonly status: NewsletterSubscriberStatus;
  readonly source: string;
  /** When the current consent was given. */
  readonly consentAtUtc: string;
  readonly unsubscribedAtUtc: string | null;
}

/** The list's filters. Dates are Colombo calendar days, `yyyy-MM-dd`, both inclusive. */
export interface NewsletterFilter {
  readonly search: string;
  readonly status: NewsletterSubscriberStatus | null;
  readonly from: string;
  readonly to: string;
}

/** The downloaded file, ready to save. */
export interface NewsletterExport {
  readonly blob: Blob;
  readonly fileName: string;
}
