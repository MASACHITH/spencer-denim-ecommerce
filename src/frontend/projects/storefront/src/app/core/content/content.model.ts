/**
 * The payloads of the storefront content API
 * (`src/backend/SpencerDenim.Api/Endpoints/Content/ContentEndpoints.cs`).
 *
 * These mirror `SpencerDenim.Application.Content.Contracts` field for field. The API camel-cases
 * property names and writes enums as their C# member names, which is why {@link HomeSectionType}
 * is the PascalCase member name rather than a slug.
 */

import { ProductCardDto } from '../catalog/catalog.model';

/**
 * `SpencerDenim.Domain.Content.HomeSectionType` — the ten blocks of HOME-01..10.
 *
 * The API only ever sends a section that is switched on and inside its schedule, so the page never
 * has to decide whether a block belongs on screen. It decides only whether the block has anything
 * in it.
 */
export type HomeSectionType =
  | 'HeroVideo'
  | 'ShopByDepartment'
  | 'NewArrivals'
  | 'TrendingItems'
  | 'CampaignBanner'
  | 'ActivewearFeature'
  | 'SaleSection'
  | 'SocialGallery'
  | 'Benefits'
  | 'Newsletter';

/** One tile, benefit, gallery image, banner or hero inside a section. */
export interface HomeSectionItemDto {
  readonly id: number;
  readonly title: string | null;
  readonly subtitle: string | null;
  readonly body: string | null;
  readonly buttonLabel: string | null;
  /** Where the tile goes. A trending tile carries a filtered listing URL (HOME-04). */
  readonly linkUrl: string | null;
  readonly imageUrl: string | null;
  readonly imageAltText: string | null;
  /** Artwork for phones, and the hero's still under reduced motion (HOME-01). */
  readonly mobileImageUrl: string | null;
  readonly mobileImageAltText: string | null;
  readonly videoUrl: string | null;
  readonly videoPosterUrl: string | null;
  /** Names an icon in the storefront's own set (HOME-09), never a file. */
  readonly iconKey: string | null;
  readonly departmentSlug: string | null;
  readonly departmentName: string | null;
  readonly productSlug: string | null;
  readonly sortOrder: number;
}

/** One live block of the homepage. */
export interface HomeSectionDto {
  readonly id: number;
  readonly type: HomeSectionType;
  readonly title: string | null;
  readonly subtitle: string | null;
  readonly sortOrder: number;
  /** The section's own "view all", and the listing a product row was drawn from. */
  readonly linkUrl: string | null;
  readonly linkLabel: string | null;
  /** `dark`, `light` or `none`: how copy sits over artwork. */
  readonly overlay: string | null;
  readonly items: readonly HomeSectionItemDto[];
  /** Cards for a product row (HOME-03, HOME-04 in auto mode, HOME-07). Empty for everything else. */
  readonly products: readonly ProductCardDto[];
}

/** `GET /api/content/home`. */
export interface HomePageDto {
  readonly sections: readonly HomeSectionDto[];
}

/** One line of the announcement strip (GLB-03). */
export interface AnnouncementDto {
  readonly id: number;
  readonly message: string;
  readonly linkUrl: string | null;
}

/**
 * `marketing.NewsletterSubscribers.Source` (doc 08 §3.6): which form the address came from.
 * The API accepts these two and nothing else.
 */
export type NewsletterSource = 'homepage' | 'footer';

/** `POST /api/content/newsletter` (HOME-10, GLB-07). */
export interface NewsletterSignupResponse {
  readonly message: string;
}
