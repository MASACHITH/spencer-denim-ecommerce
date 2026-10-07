/**
 * `GET /api/admin/content/pages` and `GET /api/admin/contact-enquiries` (ADM-17, GLB-10).
 *
 * These interfaces are a hand-written copy of
 * `SpencerDenim.Application.Content.Admin.Contracts`. That copy is a hazard rather than a convenience —
 * W1-12's blocker was a form model that had never listed three columns, so every save cleared them — so
 * the backend pins the pairing with `AdminContentShapeTests` rather than leaving it to review.
 */

/** One content page as the list shows it. */
export interface AdminContentPageListItem {
  readonly id: number;
  /** `about`, `contact`, `returns`, `shipping`, `privacy`, `terms`, or null for a custom page. */
  readonly systemKey: string | null;
  readonly slug: string;
  readonly title: string;
  readonly isPublished: boolean;
  /** False for a page the client has not written yet, which is how all six system pages ship. */
  readonly hasBody: boolean;
  /** Above zero means publishing will be refused (doc 08 §3.3, GLB-12). */
  readonly imagesMissingAltText: number;
  readonly publishedAtUtc: string | null;
  readonly updatedAtUtc: string | null;
  /** Doc 11 P13: "System pages cannot be deleted." Sent rather than inferred from `systemKey`. */
  readonly canBeDeleted: boolean;
  readonly rowVersion: string;
}

/** One content page as the editor loads it. */
export interface AdminContentPage extends AdminContentPageListItem {
  /** The stored, already-sanitised HTML. What the editor loads is what the storefront renders. */
  readonly bodyHtml: string;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
}

/**
 * What a page save answers with.
 *
 * `removedFromBody` is shown to the editor **even though the save succeeded**: a save that silently
 * dropped part of a paste is the defect shape this project has shipped three times, and the sanitiser's
 * allow-list is strict enough that a paste from Word or from another website loses something most times.
 */
export interface ContentPageSaved {
  readonly id: number;
  readonly removedFromBody: readonly string[];
}

/**
 * A content page's editable fields, as the API reads them.
 *
 * **A field left `undefined` is one the API leaves alone; a field set to `null` is one it clears.**
 * Those are two different instructions, and collapsing them is how W1-12 wiped every category image.
 * `JSON.stringify` drops an `undefined`, which is what makes the distinction travel.
 */
export interface ContentPageValues {
  readonly title?: string | null;
  readonly slug?: string | null;
  readonly bodyHtml?: string | null;
  readonly seoTitle?: string | null;
  readonly seoDescription?: string | null;
}

/** `content.ContactSubmissions.Status` (doc 08 §3.7), by name: the API serialises enums as their names. */
export type ContactEnquiryStatus = 'New' | 'Read' | 'Replied' | 'Spam';

/** One enquiry as the inbox lists it. The message body is deliberately not here — only a preview. */
export interface AdminContactEnquiryListItem {
  readonly id: number;
  readonly name: string;
  readonly email: string;
  readonly subject: string | null;
  readonly preview: string;
  readonly orderNumber: string | null;
  readonly status: ContactEnquiryStatus;
  readonly handledByUserEmail: string | null;
  readonly handledAtUtc: string | null;
  readonly createdAtUtc: string;
}

/** A page of the inbox, newest first. */
export interface AdminContactEnquiries {
  readonly items: readonly AdminContactEnquiryListItem[];
  readonly total: number;
  /** Across the whole inbox rather than the filter: it is the badge's number. */
  readonly newCount: number;
}

/** One enquiry, opened. */
export interface AdminContactEnquiry {
  readonly id: number;
  readonly name: string;
  readonly email: string;
  readonly phone: string | null;
  readonly subject: string | null;
  /**
   * What the visitor wrote, as **plain text**.
   *
   * Rendered by interpolation and never as markup: it is a visitor's free text on an authenticated
   * screen, so binding it as HTML would be a stored-XSS surface aimed at staff.
   */
  readonly message: string;
  readonly orderNumber: string | null;
  readonly status: ContactEnquiryStatus;
  readonly handledByUserEmail: string | null;
  readonly handledAtUtc: string | null;
  /**
   * Where it came from, or null — which means either that the server could not see an address **or**
   * that the ninety-day retention step has cleared it (doc 08 §8.4). The screen says it cannot tell.
   */
  readonly ipAddress: string | null;
  readonly createdAtUtc: string;
}

/** The statuses staff can set. `New` is deliberately absent: "unread it" is not a transition. */
export const SETTABLE_ENQUIRY_STATUSES: readonly ContactEnquiryStatus[] = ['Read', 'Replied', 'Spam'];

/** Every status, for the filter. */
export const ENQUIRY_STATUSES: readonly ContactEnquiryStatus[] = ['New', 'Read', 'Replied', 'Spam'];
