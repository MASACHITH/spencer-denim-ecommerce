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
  /** The shop's YouTube channel (issue #193). Empty hides the footer icon, as for the other three. */
  readonly youTubeUrl?: string | null;
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

/**
 * PayPal's credentials, exchange rate and timings (PAY-02, doc 08 §3.8 `Payments.PayPal`).
 *
 * **Optional for the same reason {@link BankDetailValues}' fields are, and with one difference.** These keys
 * need `Settings.Payments`, so a screen whose caller cannot read them never receives them, and a field the
 * object omits is one the API leaves alone. `expiryMinutes` is doc 08's `Orders.PayPalExpiryMinutes`, which is
 * `Settings.Edit`: a caller with `Settings.Payments` alone does not receive it, so it is absent here and the
 * box is not drawn.
 *
 * **The client secret is not a field of this form.** It is write-only: the server never returns it, so there
 * is nothing to load into a box, and the screen holds whatever the admin types in a separate signal and sends
 * it only when it is not blank. A blank box means "keep what is saved", not "clear it".
 *
 * Every value is text, as the API reads it: numbers are typed digits, and the server validates and
 * normalises (`3` becomes `3.00`).
 */
export interface PayPalValues {
  /** `sandbox` or `live`. */
  readonly mode?: string | null;
  readonly clientId?: string | null;
  readonly webhookId?: string | null;

  /** Rupees per US dollar. Changing it stamps the rate's age on the server. */
  readonly lkrPerUsd?: string | null;
  readonly fxBufferPercent?: string | null;
  readonly rateMaxAgeDays?: string | null;
  readonly captureLockHours?: string | null;
  readonly captureRecoveryMaxHours?: string | null;
  readonly pendingCaptureMaxDays?: string | null;

  /** Doc 08's `Orders.PayPalExpiryMinutes` — `Settings.Edit`, so often absent. */
  readonly expiryMinutes?: string | null;
}

/**
 * How long the nightly purge keeps raw page views (issue #193, doc 08 §3.8 group `Traffic`, `Settings.Edit`).
 *
 * **Optional for the same reason {@link BankDetailValues}' fields are**: a field this object omits is one the API
 * leaves alone. Unlike the text groups, a field it carries may not be empty — the API refuses an empty period
 * rather than clearing it, because an unset period would quietly hand the purge back to server configuration.
 *
 * Whole days as typed digits, as the API reads them: raw 30–365, bots 7–90 and never more than raw.
 */
export interface TrafficValues {
  readonly rawRetentionDays?: string | null;
  readonly botRetentionDays?: string | null;
}

/** Issue #193's bounds, the same figures as `SettingKeys.Traffic` on the server, for the hints and a pre-check. */
export const TRAFFIC_BOUNDS = {
  rawMin: 30,
  rawMax: 365,
  botMin: 7,
  botMax: 90,
} as const;

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
  youTubeUrl: 'Social.YouTubeUrl',
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
  payPalMode: 'Payments.PayPal.Mode',
  payPalClientId: 'Payments.PayPal.ClientId',
  payPalClientSecret: 'Payments.PayPal.ClientSecret',
  payPalWebhookId: 'Payments.PayPal.WebhookId',
  payPalLkrPerUsd: 'Payments.PayPal.LkrPerUsd',
  payPalFxBufferPercent: 'Payments.PayPal.FxBufferPercent',
  payPalRateMaxAgeDays: 'Payments.PayPal.RateMaxAgeDays',
  payPalRateUpdatedAtUtc: 'Payments.PayPal.RateUpdatedAtUtc',
  payPalCaptureLockHours: 'Payments.PayPal.CaptureLockHours',
  payPalCaptureRecoveryMaxHours: 'Payments.PayPal.CaptureRecoveryMaxHours',
  payPalPendingCaptureMaxDays: 'Payments.PayPal.PendingCaptureMaxDays',
  payPalExpiryMinutes: 'Orders.PayPalExpiryMinutes',
  trafficRawRetentionDays: 'Traffic.RawRetentionDays',
  trafficBotRetentionDays: 'Traffic.BotRetentionDays',
  reviewsRequireApproval: 'Reviews.RequireApproval',
  currencyUsd: 'Currency.Display.UsdLkrPerUnit',
  currencyGbp: 'Currency.Display.GbpLkrPerUnit',
  currencyEur: 'Currency.Display.EurLkrPerUnit',
  currencyAud: 'Currency.Display.AudLkrPerUnit',
  currencyAed: 'Currency.Display.AedLkrPerUnit',
  smtpHost: 'Email.Smtp.Host',
  smtpPort: 'Email.Smtp.Port',
  smtpUsername: 'Email.Smtp.Username',
  smtpPassword: 'Email.Smtp.Password',
  smtpFromAddress: 'Email.Smtp.FromAddress',
  smtpFromName: 'Email.Smtp.FromName',
  smtpUseStartTls: 'Email.Smtp.UseStartTls',
  pdpDeliveryInfoHtml: 'Content.PdpDeliveryInfoHtml',
  pdpReturnsInfoHtml: 'Content.PdpReturnsInfoHtml',
} as const;

/** Doc 08 §3.8's group names. */
export const SETTING_GROUPS = {
  store: 'Store',
  contact: 'Contact',
  social: 'Social',

  /** The product page's delivery and returns texts (issue #33): their own panel and save unit, `Settings.Edit`. */
  content: 'Content',

  bank: 'Bank',

  /**
   * ADM-09's own group. It shares a **save unit** with `Store` (`SettingCatalog.SaveUnits`), so the token
   * the invoice form sends is the one the logo form sends — which is deliberate: `KeysInSaveUnitFor` is
   * what makes the token a form is handed identical to the one its save compares, and publishing two
   * tokens for one unit is the mismatch that made every valid ADM-22 save a 409.
   */
  orders: 'Orders',

  /** PayPal's own panel and save unit; `Settings.Payments`, so a Store Manager never receives it. */
  payPal: 'Payments.PayPal',

  /** The raw-traffic retention periods (issue #193): their own panel and save unit, `Settings.Edit`. */
  traffic: 'Traffic',

  /** The review approval switch (issue #189): its own panel and save unit, `Settings.Edit`. */
  reviews: 'Reviews',

  /** The five display-currency rates (issue #248): their own panel and save unit, `Settings.Edit`. */
  currencyDisplay: 'Currency.Display',

  /**
   * The mail account (ADM-22, 2026-10-21): its own panel and save unit, `Settings.Security` — Super Admin only,
   * so no other role ever receives this group.
   */
  smtp: 'Email.Smtp',
} as const;

/**
 * One currency a shopper may choose to see prices in (issue #248). **The same five, in the same order, as
 * `DisplayCurrencies.All` on the server**, which is also the order of the public feed and of this panel's rows.
 *
 * `field` is the lower-case code, the name of the body field `PUT /api/admin/settings/currency-rates` reads; `key` is
 * the `ops.Settings` row holding the rate (rupees for one unit). A backend test pins the names against the handler's
 * own list, and the panel's spec pins this table against the panel.
 */
export const DISPLAY_CURRENCIES = [
  { code: 'USD', name: 'US dollar', field: 'usd', key: 'Currency.Display.UsdLkrPerUnit' },
  { code: 'GBP', name: 'Pound sterling', field: 'gbp', key: 'Currency.Display.GbpLkrPerUnit' },
  { code: 'EUR', name: 'Euro', field: 'eur', key: 'Currency.Display.EurLkrPerUnit' },
  { code: 'AUD', name: 'Australian dollar', field: 'aud', key: 'Currency.Display.AudLkrPerUnit' },
  { code: 'AED', name: 'UAE dirham', field: 'aed', key: 'Currency.Display.AedLkrPerUnit' },
] as const;

export type CurrencyField = (typeof DISPLAY_CURRENCIES)[number]['field'];

/**
 * The rates to save (issue #248, group `Currency.Display`, `Settings.Edit`).
 *
 * **Only the fields that changed**, the bank group's rule: a field this object omits is one the API leaves alone, a
 * field it carries as an empty string is one the API **clears** (the currency is then not offered to shoppers).
 * Those are two different instructions, which is why every key is optional and nothing is filled in with `null`.
 */
export type CurrencyRateValues = Readonly<Partial<Record<CurrencyField, string>>>;

/** A rate that has not changed for longer than this many days is flagged on the screen (issue #248's decision). */
export const CURRENCY_RATE_STALE_AFTER_DAYS = 30;

/**
 * The email account form's text fields, as `PUT /api/admin/settings/email-smtp` names them
 * (`UpdateSmtpSettingsCommandHandler.TextFields`, less the write-only password). Port is digits as text, like every
 * other numeric box on this screen. The switch is sent separately as a boolean.
 */
export const SMTP_TEXT_FIELDS = ['host', 'port', 'username', 'fromAddress', 'fromName'] as const;

export type SmtpTextValues = Readonly<Record<(typeof SMTP_TEXT_FIELDS)[number], string | null>>;

/** `POST /api/admin/settings/email-smtp/test`'s answer: where the test went (the signed-in admin's own address). */
export interface SmtpTestResponse {
  readonly sentTo: string;
}

/**
 * The product page's two texts (issue #33), as HTML. Holding **only the keys the server sent**, the bank group's
 * rule, so a caller who did not receive the group sends nothing for it.
 */
export interface ProductPageInfoValues {
  readonly deliveryInfoHtml?: string | null;
  readonly returnsInfoHtml?: string | null;
}

/** What a product-page text save answers: what the sanitiser removed, empty when nothing was. */
export interface ProductPageInfoSaveResponse {
  readonly removed: readonly string[];
}

/** The most cleaned markup either text may hold: `SettingKeys.Content.PdpInfoMaxLength` on the server. */
export const PRODUCT_PAGE_INFO_MAX_LENGTH = 4_000;
