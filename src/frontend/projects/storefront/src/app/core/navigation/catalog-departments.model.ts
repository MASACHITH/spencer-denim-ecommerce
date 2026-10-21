/**
 * The payload of `GET /api/catalog/departments`.
 *
 * These interfaces mirror `SpencerDenim.Application.Catalog.Contracts.DepartmentDto` and
 * `CategoryNodeDto` field for field (camel-cased by System.Text.Json). Keeping the shape exact is
 * what makes the swap in `NavigationService` a one-line change once W1-02 merges.
 */
export interface CategoryNodeDto {
  readonly id: number;
  readonly name: string;
  readonly slug: string;
  /** 1 for a top-level category, up to 3. */
  readonly depth: number;
  readonly description: string | null;
  /** The **card** rendition; `imageSrcSet` is its candidate list, `null` without renditions. */
  readonly imageUrl: string | null;
  readonly imageSrcSet: string | null;
  readonly showInMegaMenu: boolean;
  /** Mega-menu column 1–4, when the category is listed. */
  readonly megaMenuColumn: number | null;
  readonly ageGroupSlug: string | null;
  readonly children: readonly CategoryNodeDto[];
}

/**
 * One mega-menu card the API says may be drawn right now (GLB-05, ADM-17; #215). Mirrors
 * `SpencerDenim.Application.Catalog.Contracts.MegaMenuPromoCardDto`. Every string is plain text typed by staff
 * and is only ever interpolated, never bound as HTML; the link is vetted again by `toLink` before it reaches an
 * `href`.
 */
export interface MegaMenuPromoCardDto {
  readonly id: number;
  readonly title: string;
  readonly subtitle: string | null;
  readonly buttonLabel: string;
  /** A path on this site or a full http(s) address. */
  readonly linkUrl: string;
  /** The **card** rendition; `imageSrcSet` is its candidate list, `null` without renditions. */
  readonly imageUrl: string | null;
  readonly imageSrcSet: string | null;
  /** The picture's alt text from the media library. */
  readonly imageAlt: string | null;
  /**
   * The card's window, ISO 8601 UTC, or null for open-ended. The feed only sends cards live when it answered; the
   * menu re-checks these when it opens, so a page left open does not keep a card past its end (#215 tester).
   */
  readonly startsAtUtc?: string | null;
  readonly endsAtUtc?: string | null;
}

export interface DepartmentDto {
  readonly id: number;
  readonly name: string;
  readonly slug: string;
  readonly description: string | null;
  /** The **large** rendition: a hero runs the full width of the page, so it is the big one. */
  readonly heroImageUrl: string | null;
  readonly heroImageSrcSet: string | null;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
  readonly categories: readonly CategoryNodeDto[];
  /**
   * The cards that may be drawn now, best first (#215). **Optional on purpose:** the frozen fallback tree and
   * an API from before #215 have none, and a department without one keeps the card it always had.
   */
  readonly promoCards?: readonly MegaMenuPromoCardDto[];
}
