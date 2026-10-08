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
/// <para>
/// <b><see cref="Store.LegalName"/> is a different fact and is here.</b> It is not the shop's name under
/// another name: the shop a customer sees is Colombo Wardrobe, and the party that issues an invoice is the
/// registered company that commissioned it. A legal document names the second, so that one is a row the
/// client fills in — and it stays unset, and prints as nothing, until they do.
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

        /// <summary>
        /// The client company's registered name, as it must appear on an invoice (W3-04, ADM-09).
        /// <para>
        /// <b>This is the one place in the system where the <i>company</i> is meant rather than the
        /// shop.</b> Everything a shopper reads says Colombo Wardrobe (docs/02 §I2), which is why the
        /// shop's name is a build-time constant and not a settings row; an invoice is a legal document
        /// and the party issuing it is the registered company. The two are different facts about the
        /// same business and this key holds the second one.
        /// </para>
        /// <para>
        /// <b>Seeded null, and the invoice prints nothing for it rather than a placeholder.</b> Doc 08
        /// §4.1 wants the seller's details frozen on to the order at issue; an invented address or
        /// registration number on a legal document is worse than a blank, so an unset key renders as
        /// absent. See <c>InvoiceSellerDetails</c>.
        /// </para>
        /// </summary>
        public const string LegalName = "Store.LegalName";

        /// <summary>The business registration number. Seeded null; printed only when set.</summary>
        public const string BRNumber = "Store.BRNumber";

        /// <summary>
        /// The VAT registration number. Seeded null; printed only when set.
        /// <para>
        /// An invoice shows <b>no VAT line</b> (doc 08 D2, doc 11 P9): prices are VAT-inclusive, so this
        /// is the seller's registration number in the header and never a tax calculation.
        /// </para>
        /// </summary>
        public const string VatNumber = "Store.VatNumber";

        /// <summary>
        /// The company's registered address, one line per line, as plain text.
        /// <para>
        /// <b>Not <see cref="Contact.Address"/>.</b> That one is where a shopper writes to and is shown
        /// on the Contact page; this one is the registered address on an invoice. They are usually the
        /// same string and are not the same fact, and reusing the contact address on an invoice would
        /// make a shop that moves its counter reissue its legal identity.
        /// </para>
        /// </summary>
        public const string Address = "Store.Address";
    }

    /// <summary>
    /// Group <c>Orders</c> (doc 08 §3.8): how long an order waits and how long a dispatch waits.
    /// <para>
    /// <b>One of doc 08's three keys.</b> <c>BankTransferExpiryHours</c> is still read from configuration
    /// (<c>Orders__BankTransferExpiryHours</c>) and moving it is owed to ADM-22 — W3-03's journal lists it
    /// — and <c>PayPalExpiryMinutes</c> has no provider to expire. <see cref="AutoDeliverAfterDays"/> is
    /// here because doc 11 P9 step 8's job has nothing else to read.
    /// </para>
    /// </summary>
    public static class Orders
    {
        public const string Group = "Orders";

        /// <summary>
        /// How many days after dispatch a Shipped order is marked Delivered if nobody does it by hand
        /// (doc 11 P9 step 8, doc 08 §4.1 D4). Seeded <b>7</b>, which is doc 08 §3.8's own default.
        /// <para>
        /// <b>Never applied to a Store pickup order</b> (doc 08 §4.1 G-18): for a pickup, Shipped means
        /// "ready for collection", so an uncollected parcel would otherwise be recorded as delivered and
        /// earn a "Verified buyer" badge for goods still on the shelf. The exclusion is in the job's
        /// statement, not in this value.
        /// </para>
        /// <para>
        /// <b>Seeded rather than left null, unlike every other key in this build.</b> The others hold
        /// information only the client has; this one holds a number doc 11 P9 states, and a null would
        /// stop the job rather than ask a question. <see cref="AutoDeliverAfterDaysMin"/> and
        /// <see cref="AutoDeliverAfterDaysMax"/> bound what a staff user may type; <b>0 is not
        /// accepted</b>, because "mark it delivered the moment it ships" is the badge defect G-18 is
        /// about, chosen deliberately.
        /// </para>
        /// </summary>
        public const string AutoDeliverAfterDays = "Orders.AutoDeliverAfterDays";

        /// <summary>Doc 08 §3.8's seeded default for <see cref="AutoDeliverAfterDays"/>.</summary>
        public const int AutoDeliverAfterDaysDefault = 7;

        /// <summary>One day. Lower than this is "deliver it as it ships", which G-18 refuses.</summary>
        public const int AutoDeliverAfterDaysMin = 1;

        /// <summary>
        /// 90 days. Above this the job is no longer a safety net and the figure is almost certainly a typo
        /// — and an order that sits Shipped for three months is a question for staff, not for a job.
        /// </summary>
        public const int AutoDeliverAfterDaysMax = 90;
    }

    /// <summary>Group <c>Contact</c> (doc 08 §3.8): how a shopper reaches the shop (GLB-07, GLB-08).</summary>
    public static class Contact
    {
        public const string Group = "Contact";

        /// <summary>
        /// The WhatsApp number in international form, digits only, as <c>wa.me</c> wants it (GLB-08).
        /// </summary>
        public const string WhatsApp = "Contact.WhatsApp";

        /// <summary>
        /// The shop's telephone number, for display and for a <c>tel:</c> link (GLB-07, GLB-10).
        /// Added by W2-11, because the Contact page's hours-and-address panel is doc 11 P13 step 13's
        /// "contact details … edited in <b>Settings</b>" and the storefront had a placeholder constant
        /// there instead (see the TODO on <c>DEFAULT_SITE_CONFIG</c>).
        /// </summary>
        public const string Phone = "Contact.Phone";

        /// <summary>The address a shopper writes to. Shown, never written to by the system.</summary>
        public const string Email = "Contact.Email";

        /// <summary>
        /// The shop's postal address, as plain text with one line per line. <b>Not HTML</b>: the storefront
        /// splits it and interpolates each line, so there is no markup surface here at all — which is why
        /// this is a <c>String</c> and not doc 08 §3.8's <c>Text/HTML</c>.
        /// </summary>
        public const string Address = "Contact.Address";

        /// <summary>Opening hours, as plain text with one line per line. As <see cref="Address"/>.</summary>
        public const string OpeningHours = "Contact.OpeningHours";

        /// <summary>
        /// The map embed address for the Contact page (GLB-10: "Contact (form, hours, map, social)").
        /// <para>
        /// <b>Checked against a short host allow-list, not merely against being a URL.</b> The storefront
        /// puts this in an <c>&lt;iframe src&gt;</c>, so an arbitrary address would let whoever can edit
        /// settings frame anything inside the shop's own page. Only Google Maps' and OpenStreetMap's embed
        /// hosts are accepted — see <c>SettingFieldRules.TryNormaliseMapEmbedUrl</c>.
        /// </para>
        /// </summary>
        public const string MapEmbedUrl = "Contact.MapEmbedUrl";
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
