/**
 * The ADM-10 product payloads (W1-03), field for field as `/api/admin/products` sends them.
 *
 * These are **not** the storefront's `ProductCardDto` / `ProductDetailDto`. That reader drops every
 * unpublished product, every product in a deactivated category and every product of a department that is
 * switched off — which is most of what this screen exists to manage, because an unpublished product is the
 * only kind there is before it is finished.
 */

/**
 * **Every enum crosses this API as its name, never as a number.**
 *
 * `Program.cs` registers a global `JsonStringEnumConverter` and says why: "Enums travel as their names …
 * so a client never has to keep a copy of our numeric values in sync." This file used to keep exactly
 * such a copy — `1 | 2 | 3` — and the consequence was that a saved product's payload could not be handed
 * back to the API at all: the form sent `"mediaRole": 1` where the API had sent `"Gallery"`, so reordering
 * or re-captioning a photograph on any product loaded from the server answered 400, and setting a badge
 * answered 500.
 *
 * `AdminProductEnumContractTests` (xUnit) now asserts these unions against `Enum.GetNames` on the real
 * C# enums, so adding a badge without updating this file fails the build rather than the client.
 */

/** Doc 08 §3.2. `LowStock` is computed at read time and never stored, so it is not assignable. */
export type ProductBadge = 'New' | 'Sale' | 'OnlineExclusive' | 'BestSeller';

/** Doc 08 §3.2's `CK_ProductMedia_MediaRole`. */
export type ProductMediaRole = 'Gallery' | 'Hover' | 'Video';

/** `media.MediaAssets.MediaType` (doc 08 §3.3). */
export type MediaAssetType = 'Image' | 'Video' | 'Document';

export const MEDIA_ROLE = { gallery: 'Gallery', hover: 'Hover', video: 'Video' } as const;

export const BADGES: readonly { readonly value: ProductBadge; readonly label: string }[] = [
  { value: 'New', label: 'New' },
  { value: 'Sale', label: 'Sale' },
  { value: 'OnlineExclusive', label: 'Online Exclusive' },
  { value: 'BestSeller', label: 'Best Seller' },
];

export interface AdminProductListItem {
  readonly id: number;
  readonly productCode: string;
  readonly name: string;
  readonly slug: string;
  readonly departmentId: number;
  readonly departmentName: string;
  readonly primaryCategoryId: number;
  readonly primaryCategoryName: string;
  readonly basePrice: number;
  readonly compareAtPrice: number | null;
  readonly badgeOverride: ProductBadge | null;
  readonly isPublished: boolean;
  readonly firstPublishedAtUtc: string | null;
  readonly updatedAtUtc: string | null;
  readonly variantCount: number;
  readonly colourCount: number;
  readonly hasVideo: boolean;
  readonly cardImageUrl: string | null;
  /** Base64 `rowversion`. Sent back with every write; a stale one is refused (doc 08 §1.5). */
  readonly rowVersion: string;
}

export interface PagedProducts {
  readonly items: readonly AdminProductListItem[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly totalPages: number;
  readonly hasPreviousPage: boolean;
  readonly hasNextPage: boolean;
}

export interface AdminProductColour {
  /** The existing row's id, or null for one the form has just added. */
  readonly id: number | null;
  readonly name: string;
  readonly colourFamilyId: number;
  readonly colourFamilyName: string | null;
  readonly swatchHex: string | null;
  readonly swatchAssetPublicId: string | null;
  readonly swatchAssetUrl: string | null;
  readonly sortOrder: number;
}

export interface AdminProductVariant {
  readonly id: number | null;
  /** Which colourway, **by name** — a colour added in the same request has no id yet. */
  readonly colourName: string;
  readonly sizeId: number;
  readonly sizeLabel: string | null;
  readonly sizeScaleName: string | null;
  readonly sku: string;
  readonly barcode: string | null;
  readonly priceOverride: number | null;
  readonly compareAtPriceOverride: number | null;
  readonly isActive: boolean;
  readonly sortOrder: number;
  /** An order line names it, so it can be switched off but never deleted (doc 08 §3.2). */
  readonly hasOrders: boolean;
  /** Read-only here: stock is ADM-12's, through the inventory ledger. */
  readonly onHand: number;
}

export interface AdminProductMedia {
  readonly id: number | null;
  readonly assetPublicId: string;
  /** 1 Image, 2 Video, 3 Document (`media.MediaAssets.MediaType`). */
  readonly mediaType: MediaAssetType;
  readonly url: string | null;
  readonly posterUrl: string | null;
  readonly colourName: string | null;
  readonly mediaRole: ProductMediaRole;
  readonly sortOrder: number;
  readonly altTextOverride: string | null;
  /** The library asset's own alt text, which the publish checklist accepts as a fallback. */
  readonly assetAltText: string | null;
}

export interface AdminRelatedProduct {
  readonly relatedProductId: number;
  readonly name: string;
  readonly productCode: string;
  readonly isPublished: boolean;
  readonly sortOrder: number;
}

export interface AdminProductDetail {
  readonly id: number;
  readonly productCode: string;
  readonly name: string;
  readonly slug: string;
  readonly departmentId: number;
  readonly primaryCategoryId: number;
  readonly extraCategoryIds: readonly number[];
  readonly ageGroupId: number | null;
  readonly shortDescription: string | null;
  readonly description: string | null;
  readonly careInstructions: string | null;
  readonly materialId: number | null;
  readonly fitId: number | null;
  readonly occasionId: number | null;
  readonly basePrice: number;
  readonly compareAtPrice: number | null;
  readonly badgeOverride: ProductBadge | null;
  readonly newUntilUtc: string | null;
  readonly recommendedRank: number | null;
  readonly sizeGuideId: number | null;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
  readonly isPublished: boolean;
  readonly firstPublishedAtUtc: string | null;
  readonly cardImageUrl: string | null;
  readonly hoverImageUrl: string | null;
  readonly colours: readonly AdminProductColour[];
  readonly variants: readonly AdminProductVariant[];
  readonly media: readonly AdminProductMedia[];
  readonly relatedProducts: readonly AdminRelatedProduct[];
  /** Doc 11 §7 G-01, read from the same function the publish route refuses with. */
  readonly canBePublished: boolean;
  readonly publishBlockers: readonly string[];
  readonly rowVersion: string;
}

export interface CatalogOption {
  readonly id: number;
  readonly name: string;
}

export interface ProductCategoryOption {
  readonly id: number;
  readonly departmentId: number;
  readonly parentCategoryId: number | null;
  readonly depth: number;
  readonly name: string;
  /** "Clothing › Dresses › Midi" — what makes a flat `<select>` readable at 320 px. */
  readonly displayName: string;
  readonly isActive: boolean;
}

export interface ProductSizeOption {
  readonly id: number;
  readonly scaleName: string;
  readonly label: string;
  readonly sortOrder: number;
}

export interface ColourFamilyOption {
  readonly id: number;
  readonly name: string;
  readonly hexCode: string;
}

export interface ProductFormOptions {
  readonly departments: readonly CatalogOption[];
  readonly categories: readonly ProductCategoryOption[];
  readonly ageGroups: readonly CatalogOption[];
  readonly materials: readonly CatalogOption[];
  readonly fits: readonly CatalogOption[];
  readonly occasions: readonly CatalogOption[];
  readonly colourFamilies: readonly ColourFamilyOption[];
  readonly sizes: readonly ProductSizeOption[];
  readonly sizeGuides: readonly CatalogOption[];
}

/**
 * What the form sends back.
 *
 * `media` is `undefined` when the form is not editing the gallery and an array when it is — and those are
 * **not** the same thing to the API: a body with no `media` field leaves the photographs alone, an empty
 * array removes them. W1-12 conflated the two and every save silently cleared the images.
 */
export interface ProductFormValues {
  readonly productCode: string;
  readonly name: string;
  readonly slug: string;
  readonly departmentId: number | null;
  readonly primaryCategoryId: number | null;
  readonly extraCategoryIds: readonly number[];
  readonly ageGroupId: number | null;
  readonly shortDescription: string | null;
  readonly description: string | null;
  readonly careInstructions: string | null;
  readonly materialId: number | null;
  readonly fitId: number | null;
  readonly occasionId: number | null;
  readonly basePrice: number | null;
  readonly compareAtPrice: number | null;
  readonly badgeOverride: ProductBadge | null;
  readonly recommendedRank: number | null;
  readonly sizeGuideId: number | null;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
  readonly colours: readonly ColourValues[];
  readonly variants: readonly VariantValues[];
  readonly media?: readonly MediaValues[];
  readonly relatedProducts: readonly { readonly relatedProductId: number; readonly sortOrder: number }[];
  readonly confirmSlugChange?: boolean;
}

export interface ColourValues {
  readonly id: number | null;
  readonly name: string;
  readonly colourFamilyId: number | null;
  readonly swatchHex: string | null;
  readonly swatchAssetPublicId: string | null;
  readonly sortOrder: number;
}

export interface VariantValues {
  readonly id: number | null;
  readonly colourName: string;
  readonly sizeId: number;
  readonly sku: string;
  readonly barcode: string | null;
  readonly priceOverride: number | null;
  readonly compareAtPriceOverride: number | null;
  readonly isActive: boolean;
  readonly sortOrder: number;
}

export interface MediaValues {
  readonly assetPublicId: string;
  readonly colourName: string | null;
  readonly mediaRole: ProductMediaRole;
  readonly sortOrder: number;
  readonly altTextOverride: string | null;
}

/** One asset as `/api/admin/media` returns it after an upload. */
export interface UploadedAsset {
  readonly publicId: string;
  readonly mediaType: MediaAssetType;
  readonly url: string | null;
  readonly posterUrl: string | null;
  readonly altText: string | null;
}

/** RFC 9457 validation problem, as the API sends it. */
export interface ValidationProblemDetails {
  readonly title?: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;
}
