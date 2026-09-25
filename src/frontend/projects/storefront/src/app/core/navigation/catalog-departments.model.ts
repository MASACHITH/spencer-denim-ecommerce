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
}
