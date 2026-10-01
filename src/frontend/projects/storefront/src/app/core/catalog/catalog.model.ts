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
  /** The **thumb** rendition (160 w): a swatch is never drawn larger, so it carries no srcset. */
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
  /**
   * The **card** rendition (600 w WebP), or the original for an asset that has no rendition set.
   *
   * The `*Url` names are unchanged from when they held the originals, so nothing that only reads
   * a URL had to move; the `*SrcSet` fields are the optimisation on top of them — the candidate
   * list thumb → card → large, and `null` when the asset has no renditions. A listing that
   * fetched the originals weighed about 1,780 KB a page against about 330 KB for the renditions,
   * and the saving only lands if the browser is told how wide a card really is, which is why
   * every consumer pairs the srcset with a `sizes` describing its own grid.
   */
  readonly imageUrl: string | null;
  readonly imageSrcSet: string | null;
  readonly hoverImageUrl: string | null;
  readonly hoverImageSrcSet: string | null;
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
 * The body of `GET /api/catalog/search` (`ProductSearchResponse` in `CatalogEndpoints`).
 *
 * `term` and `tokens` are what the API actually searched for, not what was asked: the term is
 * trimmed and the tokens are lower-cased, de-duplicated and capped at five (doc 08 §7.6). The page
 * shows the shopper the term **it** normalised rather than this one — the two agree, because
 * `normaliseTerm` runs before the request — but `tokens` is the honest answer to "what did you
 * actually match on", which is what makes a five-word search explainable.
 */
export interface ProductSearchResponse {
  readonly term: string;
  readonly tokens: readonly string[];
  readonly results: PagedResult<ProductCardDto>;
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
 *
 * The gallery draws the same photograph at three very different sizes — a 56 px thumbnail, a
 * stage about 600 px wide and a full-screen view magnified to 3× — so it is the one place where
 * one URL for all three is measurably wrong. Each size therefore names its own rendition, and
 * each falls back to {@link url} so a product photographed before the renditions existed still
 * shows everywhere.
 */
export interface ProductImageDto {
  readonly id: number;
  /** The **large** rendition (1200 w), or the original when the asset has no rendition set. */
  readonly url: string;
  /** Candidates thumb → zoom for {@link url}; `null` when the asset has no renditions. */
  readonly srcSet: string | null;
  /** The **thumb** rendition (160 w) for the rail; `null` means fall back to {@link url}. */
  readonly thumbnailUrl: string | null;
  /** The **zoom** rendition (2000 w) for the full-screen view; `null` falls back to {@link url}. */
  readonly zoomUrl: string | null;
  readonly altText: string | null;
  readonly role: ProductMediaRole;
  readonly colourId: number | null;
  readonly width: number | null;
  readonly height: number | null;
  /** A video's poster frame. Videos have no renditions, so this is always the original. */
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
 * What a size guide carries for the product page to show one.
 *
 * It mirrors `catalog.SizeGuides` column for column. This shape was written down here *before* the
 * API served any of it, and issue #32 then matched it rather than inventing a second one — which is
 * the whole reason the content is embedded on the product payload instead of coming from a route of
 * its own: {@link hasSizeGuideContent} has to answer before the trigger exists, so there is nothing
 * to click that could trigger a fetch.
 */
export interface SizeGuideContentDto {
  /**
   * Rendered as markup through `[innerHTML]`, which is the only thing that sanitises it: there is
   * no HTML sanitiser in the backend, and `catalog.SizeGuides.IntroHtml` is not documented as
   * sanitised in doc 08 §3.2 either. Safe today only because the seeder is its sole writer; see
   * issue #70, owed before ADM-11.
   */
  readonly introHtml: string | null;
  readonly table: SizeGuideTableDto | null;
  /** Markup, on exactly the same terms as {@link introHtml}. */
  readonly measuringTipsHtml: string | null;
  readonly imageUrl: string | null;
  /**
   * The diagram's alt text, from the media asset. Null when the asset carries none, in which case
   * the dialog names the guide rather than leaving an informative image unlabelled or claiming to
   * describe a picture it cannot see.
   */
  readonly imageAltText: string | null;
}

/**
 * The size guide the product page opens (PDP-04).
 *
 * The API sends `id`, `name` and the measurements, projected from `catalog.SizeGuides` in the same
 * query as the product (issue #32, closed).
 *
 * `content` stays optional on the type rather than required, because it is genuinely absent for a
 * guide an admin has created and not filled in: `TableJson`'s own default is `{}`, and the API
 * answers `"content": null` rather than four empty fields. {@link hasSizeGuideContent} is the one
 * condition the trigger and the modal hang on.
 */
export interface SizeGuideRefDto {
  readonly id: number;
  readonly name: string;
  /** Absent, or null, when the guide holds nothing a shopper could read. */
  readonly content?: SizeGuideContentDto | null;
}

/**
 * Whether a size guide has anything a shopper could read.
 *
 * A guide with a name and no measurements is not worth a button: opening it would say only that
 * there is nothing to see, which is worse than the control not being there. When this is false the
 * trigger and the dialog do not render at all.
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
