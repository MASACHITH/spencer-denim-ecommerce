/**
 * The content-page payloads of `GET /api/content/pages` and `GET /api/content/pages/{slug}` (GLB-10).
 *
 * These mirror `SpencerDenim.Application.Content.Contracts` field for field.
 */

/** One published page, as the API returns it. */
export interface ContentPageDto {
  readonly slug: string;
  readonly title: string;
  /**
   * **Already-sanitised HTML** (doc 08 §3.7): cleaned against the allow-list when it was saved.
   *
   * It is still bound with `[innerHTML]` rather than `bypassSecurityTrustHtml`, so Angular's own DOM
   * sanitiser runs over it again on render. That costs nothing and it is the layer that holds if a row
   * is ever written by a support script or a restore — neither of which goes through the save path that
   * cleans it.
   */
  readonly bodyHtml: string;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
  /** `about`, `contact`, `returns`, `shipping`, `privacy`, `terms`, or null for a custom page. */
  readonly systemKey: string | null;
  readonly publishedAtUtc: string | null;
  readonly updatedAtUtc: string | null;
}

/** One published page as something linking to it needs it. */
export interface ContentPageLinkDto {
  readonly slug: string;
  readonly title: string;
  readonly systemKey: string | null;
  readonly updatedAtUtc: string | null;
}

/**
 * `GET /api/content/pages`.
 *
 * An object rather than a bare array, deliberately: the storefront has to tell "the shop has published
 * nothing" from "this could not be read", because the first answers 404 for an unknown slug and the
 * second has to answer 503 (#30). A bare array cannot say which it was.
 */
export interface ContentPageLinksDto {
  readonly pages: readonly ContentPageLinkDto[];
}

/**
 * What the storefront knows about the published pages, and **how** it knows it.
 *
 * `source` is the whole of #30's lesson, in the same shape `CatalogTree` carries it:
 *
 * - `live`: the API answered. A slug that is not in `pages` is genuinely not a page, so a real 404.
 * - `unknown`: the API did not answer, or answered with something else. A slug that is not in `pages`
 *   may be a perfectly real published page, so the honest answer is **503 with `Retry-After`**, which a
 *   crawler retries and which keeps the URL. A 404 here would drop a live policy page out of the index
 *   for the length of an API wobble.
 *
 * W2-10's own note says a mistyped address must answer a real 404 rather than quietly showing the home
 * page; this is the other half of the same requirement — a real address must not answer 404 because the
 * feed blinked.
 */
export interface ContentPageIndex {
  readonly pages: readonly ContentPageLink[];
  readonly source: 'live' | 'unknown';
}

/** One published page the footer and the router can use. */
export interface ContentPageLink {
  readonly slug: string;
  readonly title: string;
  readonly systemKey: string | null;
}

/** The six system keys, so nothing matches on a slug the client may rename. */
export const CONTENT_PAGE_KEYS = {
  about: 'about',
  contact: 'contact',
  returns: 'returns',
  shipping: 'shipping',
  privacy: 'privacy',
  terms: 'terms',
} as const;

/** What the storefront knows when the content feed has not answered. */
export const UNKNOWN_CONTENT_PAGES: ContentPageIndex = { pages: [], source: 'unknown' };
