/**
 * The payloads of the read-only catalogue API
 * (`src/backend/SpencerDenim.Api/Endpoints/Catalog/CatalogEndpoints.cs`).
 *
 * These mirror `SpencerDenim.Application.Catalog.Contracts` field for field. The API camel-cases
 * property names and writes enums as their C# member names
 * (`ConfigureHttpJsonOptions(... new JsonStringEnumConverter())` in `Program.cs`), which is why the
 * badge and stock types below are the PascalCase member names rather than slugs.
 */

/** `SpencerDenim.Domain.Catalog.ProductBadge`. `LowStock` is computed at read time only. */
export type ProductBadge = 'New' | 'Sale' | 'OnlineExclusive' | 'BestSeller' | 'LowStock';

/** `SpencerDenim.Domain.Inventory.StockStatus`. */
export type StockStatus = 'OutOfStock' | 'LowStock' | 'InStock';

export interface PriceDto {
  readonly current: number;
  readonly original: number;
  /** Already floored to a whole percent by the API (doc 08 §6 step 4). */
  readonly discountPercent: number;
  readonly hasDiscount: boolean;
  /** Always `LKR` in v1. */
  readonly currency: string;
}

/** `average` is null until a product has an approved review. */
export interface RatingDto {
  readonly average: number | null;
  readonly count: number;
}

export interface ColourSwatchDto {
  readonly id: number;
  readonly name: string;
  readonly familyName: string;
  readonly familySlug: string;
  readonly hexCode: string | null;
  readonly swatchImageUrl: string | null;
}

/** Everything CARD-01 renders, from one projection. */
export interface ProductCardDto {
  readonly id: number;
  readonly productCode: string;
  readonly name: string;
  readonly slug: string;
  readonly departmentName: string;
  readonly departmentSlug: string;
  readonly categoryName: string;
  readonly categorySlug: string;
  readonly imageUrl: string | null;
  readonly hoverImageUrl: string | null;
  readonly imageAltText: string | null;
  readonly price: PriceDto;
  readonly rating: RatingDto;
  readonly badge: ProductBadge | null;
  readonly stockStatus: StockStatus;
  readonly isInStock: boolean;
  readonly colours: readonly ColourSwatchDto[];
}

/**
 * One page of a listing. `PagedResult<T>` also serialises its computed `totalPages`,
 * `hasPreviousPage` and `hasNextPage`, but the listing never reads them: "Load More" compares the
 * rows it is holding against `totalCount`, which stays correct however many pages were merged.
 */
export interface PagedResult<TItem> {
  readonly items: readonly TItem[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
}

/**
 * The RFC 9457 body the listing endpoint returns for a rejected query string, keyed by the
 * query-string parameter name (`ToValidationProblem` in `CatalogEndpoints`).
 */
export interface ValidationProblemDetails {
  readonly title?: string;
  readonly detail?: string;
  readonly status?: number;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

// ---------------------------------------------------------------- the product page payload

/** `SpencerDenim.Domain.Catalog.ProductMediaRole`. */
export type ProductMediaRole = 'Gallery' | 'Hover' | 'Video';

/** A breadcrumb step on the product page (`CategoryRefDto`). */
export interface CategoryRefDto {
  readonly id: number;
  readonly name: string;
  readonly slug: string;
}

/**
 * PDP-05's attribute block. Every field is optional in the database, so a product may carry none
 * of them and the details list simply has fewer rows.
 */
export interface ProductAttributesDto {
  readonly material: string | null;
  readonly materialSlug: string | null;
  readonly fit: string | null;
  readonly fitSlug: string | null;
  readonly occasion: string | null;
  readonly occasionSlug: string | null;
  readonly ageGroup: string | null;
  readonly ageGroupSlug: string | null;
}

/**
 * One gallery item (PDP-01). `colourId` ties a photo to one colourway, which is what lets the
 * gallery follow the colour the shopper picked. A `Video` carries `posterUrl`.
 */
export interface ProductImageDto {
  readonly id: number;
  readonly url: string;
  readonly altText: string | null;
  readonly role: ProductMediaRole;
  readonly colourId: number | null;
  readonly width: number | null;
  readonly height: number | null;
  readonly posterUrl: string | null;
  readonly sortOrder: number;
}

/**
 * One size the product is made in. `isInStock` is the aggregate across every colour, so the size
 * list can be drawn before a colour is chosen; per-colour stock comes from the variants.
 */
export interface ProductSizeDto {
  readonly id: number;
  readonly label: string;
  readonly scaleName: string;
  readonly sortOrder: number;
  readonly isInStock: boolean;
}

/**
 * One buyable colour × size combination (PDP-03). It carries its own `price`, because
 * `catalog.ProductVariants.PriceOverride` is real and seeded — a 32" waist and a 38" waist of the
 * same jean are different money — which is why picking a size re-prices the page.
 */
export interface ProductVariantDto {
  readonly id: number;
  readonly sku: string;
  readonly colourId: number;
  readonly colourName: string;
  readonly sizeId: number;
  readonly sizeLabel: string;
  readonly scaleName: string;
  readonly price: PriceDto;
  readonly stockStatus: StockStatus;
  readonly isInStock: boolean;
  readonly availableQuantity: number;
}

/** The measurement table of a size guide: `catalog.SizeGuides.TableJson` (doc 08 §1.4, DB15). */
export interface SizeGuideTableDto {
  readonly columns: readonly string[];
  readonly rows: readonly (readonly string[])[];
}

/**
 * What a size guide would have to carry for the product page to be able to show one.
 *
 * It mirrors `catalog.SizeGuides` column for column, and **the API projects none of it today**
 * (issue #32). The shape is written down rather than guessed at later so that serving it is the
 * only change needed here.
 */
export interface SizeGuideContentDto {
  readonly introHtml: string | null;
  readonly table: SizeGuideTableDto | null;
  readonly measuringTipsHtml: string | null;
  readonly imageUrl: string | null;
}

/**
 * The size guide the product page opens (PDP-04).
 *
 * `id` and `name` are the whole of what the API sends. The measurements exist in the database —
 * `catalog.SizeGuides` has `IntroHtml`, `TableJson`, `MeasuringTipsHtml` and an image, and
 * `CatalogReferenceDataSeeder` fills all four — but `SizeGuideRefDto` does not project them and
 * no endpoint serves them, which is **issue #32**.
 *
 * `content` is therefore optional and, today, always absent. {@link hasSizeGuideContent} is the
 * one condition the trigger and the modal hang on, so the day the API projects it the guide
 * appears with no other change.
 */
export interface SizeGuideRefDto {
  readonly id: number;
  readonly name: string;
  /** TODO(#32): the API does not send this yet, so a guide never has measurements to show. */
  readonly content?: SizeGuideContentDto | null;
}

/**
 * Whether a size guide has anything a shopper could read.
 *
 * A guide with a name and no measurements is not worth a button: opening it would say only that
 * there is nothing to see, which is worse than the control not being there. Until issue #32 lands
 * this is false for every product, and the trigger and the dialog do not render at all.
 */
export function hasSizeGuideContent(
  guide: SizeGuideRefDto | null | undefined,
): guide is SizeGuideRefDto {
  const content = guide?.content;
  if (!content) {
    return false;
  }

  return (
    (content.table?.rows.length ?? 0) > 0 ||
    hasText(content.introHtml) ||
    hasText(content.measuringTipsHtml) ||
    hasText(content.imageUrl)
  );
}

function hasText(value: string | null): boolean {
  return typeof value === 'string' && value.trim().length > 0;
}

/** The admin-written meta tags for one product (GLB-11). `title` already includes the brand. */
export interface SeoDto {
  readonly title: string;
  readonly description: string | null;
}

/**
 * The whole product page in one response (PDP-01..06).
 *
 * `price` is the product-level band — `current` is the cheapest variant, `original` the dearest
 * compare-at price — so it answers "from Rs. X" before a size is chosen. `minPrice`/`maxPrice`
 * bound the band; once a variant is selected its own `price` is the one that matters.
 */
export interface ProductDetailDto {
  readonly id: number;
  readonly productCode: string;
  readonly name: string;
  readonly slug: string;
  readonly department: CategoryRefDto;
  readonly breadcrumb: readonly CategoryRefDto[];
  readonly shortDescription: string | null;
  readonly description: string | null;
  readonly careInstructions: string | null;
  readonly attributes: ProductAttributesDto;
  readonly price: PriceDto;
  readonly minPrice: number;
  readonly maxPrice: number;
  readonly rating: RatingDto;
  readonly badge: ProductBadge | null;
  readonly stockStatus: StockStatus;
  readonly isInStock: boolean;
  readonly seo: SeoDto;
  readonly sizeGuide: SizeGuideRefDto | null;
  readonly newUntilUtc: string | null;
  readonly images: readonly ProductImageDto[];
  readonly colours: readonly ColourSwatchDto[];
  readonly sizes: readonly ProductSizeDto[];
  readonly variants: readonly ProductVariantDto[];
  readonly relatedProducts: readonly ProductCardDto[];
}
