namespace SpencerDenim.Domain.Settings;

/// <summary>
/// The <c>ops.Settings</c> keys this milestone populates (ADM-22, GLB-02). Doc 08 §3.8's "Groups" table
/// is the authority for the names; these are the subset with a screen behind them.
/// <para>
/// <b>What is deliberately absent, and why that is not an oversight.</b> Doc 08 lists sixteen groups.
/// <c>Bank.*</c> is now here (W3-03, PAY-01): it is the one group doc 11 P7's preconditions make a
/// prerequisite of taking money at all. <c>Payments.PayPal.*</c> still waits for W4-01 — its keys are
/// credentials and an FX rate with no provider to use them; <c>Email.Smtp.*</c>, <c>Security.*</c> and
/// <c>Privacy.RetainInvoiceBillTo</c> need
/// <c>Settings.Security</c>, the sensitive-change alert and <c>ops.AdminAlerts</c>, none of which exists;
/// <c>Delivery</c>, the couriers and the delivery methods need <c>sales.DeliveryMethods</c> and friends,
/// which are tables #53 creates. A key seeded here would be a key a screen offers and nothing honours —
/// the shape of defect this project has twice decided against (the bag's promo code, "popular searches").
/// The table, its constraints and the permission machinery are all built, so those keys drop in as rows.
/// </para>
/// <para>
/// <b><c>Store.Name</c> is absent on purpose, and this is the one place it could plausibly belong.</b>
/// Doc 08 §3.8 lists <c>Name</c> in the <c>Store</c> group, doc 11 P13 step 13 (line 1419) says store name is
/// edited in Settings, and P14's permission table (line 1494) lists it under <c>Settings.Edit</c> — all of
/// which predate the owner's decision of 2026-10-02 (docs/02 §I2, W1-15),
/// which put the shop's name in exactly one constant per application
/// (<c>SpencerDenim.Domain.Branding.StoreBrand</c> and <c>BRAND</c> in <c>@spencer-denim/shared</c>) with
/// a convention test that fails if it is written out anywhere else. Seeding it here would give the name
/// two sources of truth, one of which the server renders the storefront from before any database is
/// touched. The logo is an image a client uploads; the name is part of the build. See the journal entry
/// for 2026-10-02.
/// </para>
/// </summary>
public static class SettingKeys
{
    /// <summary>Group <c>Store</c> (doc 08 §3.8): the shop's own identity as a shopper sees it (GLB-02).</summary>
    public static class Store
    {
        public const string Group = "Store";

        /// <summary>
        /// The header wordmark's artwork: a <see cref="SettingValueType.MediaAssetRef"/> pointing at a
        /// ready, public image in the media library. Null until the client uploads one, and the header
        /// falls back to the text wordmark for exactly as long as it stays null.
        /// </summary>
        public const string LogoAssetId = "Store.LogoAssetId";
    }

    /// <summary>Group <c>Contact</c> (doc 08 §3.8): how a shopper reaches the shop (GLB-07, GLB-08).</summary>
    public static class Contact
    {
        public const string Group = "Contact";

        /// <summary>
        /// The WhatsApp number in international form, digits only, as <c>wa.me</c> wants it (GLB-08).
        /// </summary>
        public const string WhatsApp = "Contact.WhatsApp";
    }

    /// <summary>
    /// Group <c>Social</c> (doc 08 §3.8): the shop's own profile addresses (GLB-07).
    /// <para>
    /// <b>Three of doc 08's four.</b> <c>YouTubeUrl</c> is not seeded: the icon set
    /// (<c>@spencer-denim/shared</c>'s <c>IconName</c>) has no YouTube glyph, so a filled-in value would
    /// have nowhere to render, and the footer's three placeholder icons — the ones this task replaces —
    /// are Facebook, Instagram and TikTok. It is a row and an icon away whenever the client asks.
    /// </para>
    /// </summary>
    public static class Social
    {
        public const string Group = "Social";

        public const string FacebookUrl = "Social.FacebookUrl";

        public const string InstagramUrl = "Social.InstagramUrl";

        public const string TikTokUrl = "Social.TikTokUrl";
    }

    /// <summary>
    /// Group <c>Bank</c> (doc 08 §3.8): where a customer sends a bank transfer, and permission
    /// <c>Settings.Payments</c> on every key (DB2) — the first group in this build that is not
    /// <c>Settings.Edit</c>.
    /// <para>
    /// <b>Every one of these is empty until the client fills it in, and that is the point.</b> The client
    /// has not given us their bank details, so inventing an account number would be worse than showing
    /// nothing: a shopper would transfer real money to a number nobody owns. All five are seeded
    /// <see langword="null"/> (the <c>Store.LogoAssetId</c> precedent), and
    /// <c>BankTransferProvider</c> treats an incomplete set as "instructions not available yet" rather
    /// than printing a half-filled form. Doc 11 P7's preconditions say the same thing from the other side:
    /// "bank details set by a Super Admin" is a precondition, not an assumption.
    /// </para>
    /// </summary>
    public static class Bank
    {
        public const string Group = "Bank";

        /// <summary>The bank's name, e.g. the one printed on the shopper's online-banking payee list.</summary>
        public const string BankName = "Bank.BankName";

        public const string Branch = "Bank.Branch";

        /// <summary>The name the account is held in, which a branch deposit slip asks for.</summary>
        public const string AccountName = "Bank.AccountName";

        /// <summary>
        /// The account number, stored as the client types it.
        /// <para>
        /// <b>Deliberately not validated beyond a length and a character allow-list.</b> Sri Lankan bank
        /// account numbers have no single national format — length and grouping differ by bank — so a
        /// pattern rule here would refuse a correct number and there would be no way round it from the
        /// screen. The one thing worth refusing is a value that is not a number at all.
        /// </para>
        /// </summary>
        public const string AccountNumber = "Bank.AccountNumber";

        /// <summary>
        /// Optional free text shown under the details, e.g. "CDM deposits take one working day to appear".
        /// <para>
        /// <b><see cref="SettingValueType.String"/> and not <see cref="SettingValueType.TextHtml"/>.</b>
        /// Doc 08 §3.8 does not state a type for this key. HTML would need the content-pages sanitiser
        /// (N10), which belongs to ADM-17 and does not exist, and an unsanitised HTML value rendered on a
        /// customer-facing order page is stored XSS. It is plain text, rendered with its line breaks
        /// preserved, and it reaches the page as text and never as markup.
        /// </para>
        /// </summary>
        public const string Instructions = "Bank.Instructions";
    }

    /// <summary><c>ops.Settings.Key</c> is <c>varchar(100)</c>.</summary>
    public const int KeyMaxLength = 100;

    /// <summary><c>ops.Settings.GroupKey</c> is <c>varchar(50)</c>.</summary>
    public const int GroupKeyMaxLength = 50;

    /// <summary><c>ops.Settings.RequiredPermission</c> is <c>varchar(64)</c>.</summary>
    public const int RequiredPermissionMaxLength = 64;

    /// <summary><c>ops.Settings.Description</c> is <c>nvarchar(300)</c>.</summary>
    public const int DescriptionMaxLength = 300;
}
