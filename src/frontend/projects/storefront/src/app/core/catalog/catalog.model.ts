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
