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
  readonly imageUrl: string | null;
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
  readonly heroImageUrl: string | null;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
  readonly categories: readonly CategoryNodeDto[];
}
