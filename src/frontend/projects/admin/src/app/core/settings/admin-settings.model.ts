/**
 * `GET /api/admin/settings` (ADM-22).
 *
 * These interfaces are a hand-written copy of
 * `SpencerDenim.Application.Settings.Contracts.AdminSettingsDto`. That copy is a hazard rather than a
 * convenience — W1-12's blocker was a form model that had never listed three columns, so every save
 * cleared them — so the backend pins the pairing with `AdminSettingsPayloadShapeTests` and
 * `AdminSettingsWriteShapeTests` rather than leaving it to review.
 */

/** Doc 08 §3.8's `ValueType`, by name: the API serialises enums as their names. */
export type SettingValueType =
  | 'String'
  | 'TextHtml'
  | 'Int'
  | 'Decimal'
  | 'Bool'
  | 'Json'
  | 'Secret'
  | 'MediaAssetRef';

/** One settings key as the screen sees it. */
export interface AdminSetting {
  readonly key: string;
  readonly valueType: SettingValueType;
  /**
   * The value, or null for a key nobody has filled in — **and always null for a secret**, whoever is
   * asking. The server never returns one; `hasValue` is all a screen can learn about it.
   */
  readonly value: string | null;
  readonly isSecret: boolean;
  readonly hasValue: boolean;
  readonly requiredPermission: string;
  readonly description: string | null;
  readonly updatedAtUtc: string | null;
}

/** One group of settings, as a form. */
export interface AdminSettingGroup {
  readonly groupKey: string;
  /** The group's concurrency token, sent straight back with a save and never interpreted here. */
  readonly rowVersion: string;
  readonly settings: readonly AdminSetting[];
}

/** The current logo, for the preview and the Remove button. */
export interface AdminLogo {
  readonly publicId: string;
  readonly url: string | null;
  readonly width: number | null;
  readonly height: number | null;
  readonly altText: string | null;
  readonly originalFileName: string;
}

export interface AdminSettings {
  /** Only the groups this user may read. A group they may not is absent, not empty. */
  readonly groups: readonly AdminSettingGroup[];
  readonly logo: AdminLogo | null;
}

/** What `POST /api/admin/media/images` returns, of which this screen needs one field. */
export interface UploadedMediaAsset {
  readonly publicId: string;
  readonly url: string | null;
  readonly width: number | null;
  readonly height: number | null;
  readonly altText: string | null;
  readonly originalFileName: string;
}

/**
 * The contact and social form's values.
 *
 * **Every field, every save.** `PUT` replaces, so a field this object omits is a field the API is told to
 * clear — which is exactly how W1-12's blocker worked. Keeping the keys here identical to
 * `UpdateContactAndSocialRequest`'s is what `AdminSettingsWriteShapeTests` asserts.
 */
export interface ContactAndSocialValues {
  readonly whatsApp: string | null;
  readonly facebookUrl: string | null;
  readonly instagramUrl: string | null;
  readonly tikTokUrl: string | null;
}

/** The settings keys this screen edits, so the template and the mapping cannot disagree about a name. */
export const SETTING_KEYS = {
  logoAssetId: 'Store.LogoAssetId',
  whatsApp: 'Contact.WhatsApp',
  facebookUrl: 'Social.FacebookUrl',
  instagramUrl: 'Social.InstagramUrl',
  tikTokUrl: 'Social.TikTokUrl',
} as const;

/** Doc 08 §3.8's group names. */
export const SETTING_GROUPS = {
  store: 'Store',
  contact: 'Contact',
  social: 'Social',
} as const;
