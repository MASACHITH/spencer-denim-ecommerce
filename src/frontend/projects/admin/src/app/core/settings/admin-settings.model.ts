/**
 * `GET /api/admin/settings` (ADM-22).
 *
 * These interfaces are a hand-written copy of
 * `SpencerDenim.Application.Settings.Contracts.AdminSettingsDto`. That copy is a hazard rather than a
 * convenience — W1-12's blocker was a form model that had never listed three columns, so every save
 * cleared them — so the backend pins the pairing with `AdminSettingsPayloadShapeTests` and
 * `AdminSettingsShapeTests` rather than leaving it to review.
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
 * **Every field the server sent, on every save — and nothing it did not.** `PUT` replaces, so a field the body
 * carries as `null` is a field the API is told to *clear*, while a field the body omits is one it leaves alone.
 * Those are two different instructions and this type has to be able to express both:
 *
 * - a key the server sent, holding `null` → the client is clearing it (what an empty box means);
 * - a key the server did **not** send → absent here, so `JSON.stringify` drops it and the API leaves it alone.
 *
 * The second case is why these are optional. The first version made them required and `toForm` filled the gaps
 * with `null`, so a caller who could not read `Social.*` would have cleared all three on their first save of
 * the WhatsApp number — W1-12's Blocker, one feature over. No such caller exists until #53 adds one.
 *
 * The W1-12 guarantee itself is unchanged for the ordinary caller: the screen sends every field it was given,
 * and `AdminSettingsShapeTests` pins these names against the request record and the handler's key list.
 */
export interface ContactAndSocialValues {
  readonly whatsApp?: string | null;
  /** The telephone number as shoppers read it (GLB-10, W2-11). */
  readonly phone?: string | null;
  readonly email?: string | null;
  /** The postal address, one line per line. Plain text, never HTML. */
  readonly address?: string | null;
  /** When the shop is open, one line per line. Plain text, never HTML. */
  readonly openingHours?: string | null;
  /** Only a Google Maps or OpenStreetMap embed address is accepted; the storefront frames it. */
  readonly mapEmbedUrl?: string | null;
  readonly facebookUrl?: string | null;
  readonly instagramUrl?: string | null;
  readonly tikTokUrl?: string | null;
}

/**
 * The bank account customers transfer to (PAY-01, doc 11 P7).
 *
 * **Optional for the same reason {@link ContactAndSocialValues}' fields are, and here it matters more.**
 * These five keys need `Settings.Payments`, which no default role holds, so a Store Manager's settings
 * screen genuinely does not receive them. A field this object omitted is one the API leaves alone; a field
 * it carries as `null` is one the API clears. Making them required and filling the gaps with `null` would
 * mean the first save by any caller who cannot read the group wipes the shop's bank account number — and
 * every order placed afterwards would show a customer no account to pay into.
 */
export interface BankDetailValues {
  readonly bankName?: string | null;
  readonly branch?: string | null;
  readonly accountName?: string | null;
  readonly accountNumber?: string | null;
  readonly instructions?: string | null;
}

/**
 * The company's details an invoice prints, and the auto-deliver window (ADM-09, doc 08 §3.8 groups `Store`
 * and `Orders`).
 *
 * **Optional for the same reason {@link BankDetailValues}' fields are.** A field this object omits is one
 * the API leaves alone; a field it carries as `null` is one the API clears. Reading absent as null is the
 * shape that wiped every category image behind 1,571 passing tests.
 *
 * **`legalName` is the company's, not the shop's.** A shopper reads `BRAND.name`; an invoice names the
 * registered company that issues it (docs/02 §I2). They are different facts about the same business.
 */
export interface InvoiceAndFulfilmentValues {
  readonly legalName?: string | null;

  /**
   * Named `registeredAddress`, not `address`: `Contact.Address` already owns that name on the same
   * screen, and two boxes called "address" on one page collided on their DOM id and their error field.
   */
  readonly registeredAddress?: string | null;
  readonly brNumber?: string | null;
  readonly vatNumber?: string | null;

  /** Whole days as digits, 1–90. Empty stops the job rather than defaulting it. */
  readonly autoDeliverAfterDays?: string | null;
}

/** The settings keys this screen edits, so the template and the mapping cannot disagree about a name. */
export const SETTING_KEYS = {
  logoAssetId: 'Store.LogoAssetId',
  whatsApp: 'Contact.WhatsApp',
  phone: 'Contact.Phone',
  email: 'Contact.Email',
  address: 'Contact.Address',
  openingHours: 'Contact.OpeningHours',
  mapEmbedUrl: 'Contact.MapEmbedUrl',
  facebookUrl: 'Social.FacebookUrl',
  instagramUrl: 'Social.InstagramUrl',
  tikTokUrl: 'Social.TikTokUrl',
  bankName: 'Bank.BankName',
  branch: 'Bank.Branch',
  accountName: 'Bank.AccountName',
  accountNumber: 'Bank.AccountNumber',
  instructions: 'Bank.Instructions',
  legalName: 'Store.LegalName',
  storeAddress: 'Store.Address',
  brNumber: 'Store.BRNumber',
  vatNumber: 'Store.VatNumber',
  autoDeliverAfterDays: 'Orders.AutoDeliverAfterDays',
} as const;

/** Doc 08 §3.8's group names. */
export const SETTING_GROUPS = {
  store: 'Store',
  contact: 'Contact',
  social: 'Social',
  bank: 'Bank',

  /**
   * ADM-09's own group. It shares a **save unit** with `Store` (`SettingCatalog.SaveUnits`), so the token
   * the invoice form sends is the one the logo form sends — which is deliberate: `KeysInSaveUnitFor` is
   * what makes the token a form is handed identical to the one its save compares, and publishing two
   * tokens for one unit is the mismatch that made every valid ADM-22 save a 409.
   */
  orders: 'Orders',
} as const;
