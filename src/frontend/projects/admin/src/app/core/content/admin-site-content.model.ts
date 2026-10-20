/**
 * `/api/admin/content/home`, `/api/admin/content/announcements` and `/api/admin/content/size-guides`
 * (ADM-17, GLB-03, PDP-04; issue #192).
 *
 * A hand-written copy of the API's contracts. Every request type lists **every** key the server reads,
 * with no optional members: the endpoints refuse a body that leaves one out (`Send <field>.`), so a
 * missing key here would be a compile error rather than a 400 in production.
 *
 * Schedules are the shop's own wall-clock time, `yyyy-MM-ddTHH:mm` with no offset — what an
 * `<input type="datetime-local">` produces — the sale-campaign convention.
 */

// ---- media ----------------------------------------------------------------------------------------

export interface AdminMediaRef {
  readonly publicId: string;
  readonly url: string | null;
  readonly altText: string | null;
}

export interface AdminVideoRef {
  readonly publicId: string;
  readonly url: string | null;
  readonly posterUrl: string | null;
}

/** What `POST /api/admin/media/images` and `/videos` answer with; only these fields are read here. */
export interface UploadedMedia {
  readonly publicId: string;
  readonly url: string | null;
  readonly posterUrl?: string | null;
  readonly altText?: string | null;
}

// ---- homepage blocks ------------------------------------------------------------------------------

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

export type HomeSectionMode = 'Auto' | 'Manual';

export type HomeOverlay = 'dark' | 'light' | 'none';

export type HomeSectionField =
  | 'title'
  | 'subtitle'
  | 'mode'
  | 'count'
  | 'listingUrl'
  | 'linkLabel'
  | 'overlay';

export type HomeItemField =
  | 'title'
  | 'subtitle'
  | 'body'
  | 'buttonLabel'
  | 'linkUrl'
  | 'image'
  | 'mobileImage'
  | 'video'
  | 'department'
  | 'product'
  | 'iconKey';

export interface AdminHomeSectionRules {
  readonly sectionFields: readonly HomeSectionField[];
  readonly itemFields: readonly HomeItemField[];
  readonly requiredItemFields: readonly string[];
  /** 0 = this block has no items. */
  readonly maxItems: number;
  /** True only for the social gallery, whose links may go to another site. */
  readonly linkMayLeaveSite: boolean;
  /** Hero and banners: an item can be switched on only with a picture (the storefront draws nothing without one). */
  readonly needsArtwork: boolean;
}

export interface AdminHomeSectionItem {
  readonly id: number;
  readonly title: string | null;
  readonly subtitle: string | null;
  readonly body: string | null;
  readonly buttonLabel: string | null;
  readonly linkUrl: string | null;
  readonly image: AdminMediaRef | null;
  readonly mobileImage: AdminMediaRef | null;
  readonly video: AdminVideoRef | null;
  readonly departmentId: number | null;
  readonly productSlug: string | null;
  readonly iconKey: string | null;
  readonly sortOrder: number;
  readonly isEnabled: boolean;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
  readonly isLiveNow: boolean;
}

export interface AdminHomeSection {
  readonly id: number;
  readonly type: HomeSectionType;
  readonly title: string | null;
  readonly subtitle: string | null;
  readonly isEnabled: boolean;
  readonly sortOrder: number;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
  readonly mode: HomeSectionMode | null;
  readonly count: number | null;
  readonly listingUrl: string | null;
  readonly linkLabel: string | null;
  readonly overlay: HomeOverlay | null;
  readonly isLiveNow: boolean;
  readonly rules: AdminHomeSectionRules;
  readonly items: readonly AdminHomeSectionItem[];
  /** The section is the aggregate: item writes send this too. */
  readonly rowVersion: string;
}

export interface AdminHomeDepartment {
  readonly id: number;
  readonly name: string;
  readonly slug: string;
  readonly isActive: boolean;
}

export interface AdminHomeContent {
  readonly sections: readonly AdminHomeSection[];
  readonly departments: readonly AdminHomeDepartment[];
  readonly benefitIcons: readonly string[];
}

/** `PUT /api/admin/content/home/sections/{id}`. Every key, always; a field the block does not use is null. */
export interface HomeSectionRequest {
  readonly title: string | null;
  readonly subtitle: string | null;
  readonly isEnabled: boolean;
  readonly sortOrder: number | null;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
  readonly mode: HomeSectionMode | null;
  readonly count: number | null;
  readonly listingUrl: string | null;
  readonly linkLabel: string | null;
  readonly overlay: HomeOverlay | null;
  readonly rowVersion: string;
}

/** `POST .../sections/{id}/items` and `PUT .../items/{id}`. `rowVersion` is the SECTION's. */
export interface HomeItemRequest {
  readonly title: string | null;
  readonly subtitle: string | null;
  readonly body: string | null;
  readonly buttonLabel: string | null;
  readonly linkUrl: string | null;
  readonly imagePublicId: string | null;
  readonly mobileImagePublicId: string | null;
  readonly videoPublicId: string | null;
  readonly departmentId: number | null;
  readonly productSlug: string | null;
  readonly iconKey: string | null;
  readonly sortOrder: number | null;
  readonly isEnabled: boolean;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
  readonly rowVersion: string;
}

/** What a section or item write answers: the id and the section's NEW row version. */
export interface HomeSaved {
  readonly id: number;
  readonly rowVersion: string;
}

/** The ten blocks' names as the screen shows them, in the proposal's order (doc 11 P13 step 5). */
export const HOME_SECTION_LABELS: Readonly<Record<HomeSectionType, string>> = {
  HeroVideo: 'Hero video',
  ShopByDepartment: 'Shop by department',
  NewArrivals: 'New arrivals',
  TrendingItems: 'Trending items',
  CampaignBanner: 'Campaign banner',
  ActivewearFeature: 'Activewear feature',
  SaleSection: 'Sale section',
  SocialGallery: 'Social gallery',
  Benefits: 'Benefits',
  Newsletter: 'Newsletter',
};

/** "On the shop now" / "Off" / "Scheduled" — the three things a block, item or message can be. */
export function liveLabel(row: { readonly isEnabled: boolean; readonly isLiveNow: boolean }): string {
  if (row.isLiveNow) {
    return 'On the shop now';
  }

  return row.isEnabled ? 'Scheduled' : 'Off';
}

// ---- announcement bar -----------------------------------------------------------------------------

export interface AdminAnnouncement {
  readonly id: number;
  readonly message: string;
  readonly linkUrl: string | null;
  readonly sortOrder: number;
  readonly isActive: boolean;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
  readonly isLiveNow: boolean;
  readonly version: string;
}

/** POST omits `version`; PUT carries it. */
export interface AnnouncementRequest {
  readonly message: string;
  readonly linkUrl: string | null;
  readonly sortOrder: number | null;
  readonly isActive: boolean;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
}

export interface AnnouncementSaved {
  readonly id: number;
  readonly version: string;
}

export const ANNOUNCEMENT_MAX_LENGTH = 200;

/** Beyond this, a message runs to three or more lines on a 320 px phone; the editor hints, never refuses. */
export const ANNOUNCEMENT_LONG_ON_PHONES = 60;

// ---- size guides ----------------------------------------------------------------------------------

export interface AdminSizeGuideListItem {
  readonly id: number;
  readonly name: string;
  readonly categoryCount: number;
  readonly productCount: number;
  readonly hasImage: boolean;
  readonly updatedAtUtc: string | null;
  readonly rowVersion: string;
}

export interface SizeGuideTable {
  readonly columns: readonly string[];
  readonly rows: readonly (readonly string[])[];
}

export interface SizeGuideLimits {
  readonly maxColumns: number;
  readonly maxRows: number;
  readonly maxCells: number;
  readonly maxCellLength: number;
  readonly maxHtmlLength: number;
}

/** The server's constants, used before a guide has been read (a new guide has no `limits` to read). */
export const DEFAULT_SIZE_GUIDE_LIMITS: SizeGuideLimits = {
  maxColumns: 20,
  maxRows: 100,
  maxCells: 1000,
  maxCellLength: 60,
  maxHtmlLength: 20000,
};

export interface AdminSizeGuide {
  readonly id: number;
  readonly name: string;
  readonly introHtml: string | null;
  /** Null when the stored table is unreadable or empty. */
  readonly table: SizeGuideTable | null;
  readonly measuringTipsHtml: string | null;
  readonly image: AdminMediaRef | null;
  readonly categoryCount: number;
  readonly productCount: number;
  readonly rowVersion: string;
  readonly limits: SizeGuideLimits;
}

/** POST omits `rowVersion`; PUT carries it. `tableJson` is a STRING holding the table. */
export interface SizeGuideRequest {
  readonly name: string;
  readonly introHtml: string | null;
  readonly tableJson: string;
  readonly measuringTipsHtml: string | null;
  readonly imagePublicId: string | null;
}

export interface SizeGuideSaved {
  readonly id: number;
  readonly rowVersion: string;
  readonly removedFromIntro: readonly string[];
  readonly removedFromTips: readonly string[];
}
