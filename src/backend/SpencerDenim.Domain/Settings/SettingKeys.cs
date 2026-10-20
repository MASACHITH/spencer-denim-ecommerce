namespace SpencerDenim.Domain.Settings;

/// <summary>
/// The <c>ops.Settings</c> keys this milestone populates (ADM-22, GLB-02). Doc 08 §3.8's "Groups" table
/// is the authority for the names; these are the subset with a screen behind them.
/// <para>
/// <b>What is deliberately absent, and why that is not an oversight.</b> Doc 08 §3.8's "Groups" table
/// has fifteen rows — counted from the table; "sixteen" is §3.1's module list, which is a different
/// thing — and names 59 keys, of which this build seeds 36 (issue #193 added <c>Social.YouTubeUrl</c> and the
/// two <c>Traffic.*</c> retention periods; issue #189 <c>Reviews.RequireApproval</c>). <see cref="DeferredSettingKeys"/>
/// is the per-key record of the other 23 and is machine-checked against the document (<c>SettingKeySpecificationTests</c>); the paragraphs below
/// are the summary.
/// <c>Bank.*</c> is now here (W3-03, PAY-01): it is the one group doc 11 P7's preconditions make a
/// prerequisite of taking money at all. <c>Payments.PayPal.*</c> is here since W4-01 (see
/// <c>SettingKeys.PayPal</c>); <c>Social.YouTubeUrl</c> and <c>Traffic.*</c> since issue #193.
/// <c>Email.Smtp.*</c>, <c>Security.*</c> and <c>Privacy.RetainInvoiceBillTo</c> are <c>Settings.Security</c>
/// and deferred for their own reasons: SMTP needs doc 08 §3.8 N2's <i>dual</i> send (the only part of N2 missing —
/// the single-send <c>SensitiveSettingsChangedAlert</c> exists), the lockout figures are read once at start-up
/// rather than per attempt, and no anonymisation path exists to honour the privacy key;
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
/// another name: the shop a customer sees is named by <c>StoreBrand.Name</c>, and the party that issues an
/// invoice is the registered company that commissioned it. A legal document names the second, so that one
/// is a row the client fills in — and it stays unset, and prints as nothing, until they do.
/// </para>
/// </summary>
public static partial class SettingKeys
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
        /// shop.</b> Everything a shopper reads carries <c>StoreBrand.Name</c> (docs/02 §I2), which is
        /// why the shop's name is a build-time constant and not a settings row; an invoice is a legal
        /// document and the party issuing it is the registered company. The two are different facts about
        /// the same business and this key holds the second one.
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
    /// <b>All four of doc 08's.</b> <see cref="YouTubeUrl"/> waited for a glyph in the shared icon set
    /// (<c>@spencer-denim/shared</c>'s <c>IconName</c>) and arrived with it in issue #193; until then a value typed
    /// into it would have had nowhere to render.
    /// </para>
    /// </summary>
    public static class Social
    {
        public const string Group = "Social";

        public const string FacebookUrl = "Social.FacebookUrl";

        public const string InstagramUrl = "Social.InstagramUrl";

        public const string TikTokUrl = "Social.TikTokUrl";

        /// <summary>The shop's YouTube channel (issue #193). Empty hides the footer icon, as the other three do.</summary>
        public const string YouTubeUrl = "Social.YouTubeUrl";
    }

    /// <summary>
    /// Group <c>Traffic</c> (doc 08 §3.8, §8.4): how long raw page views are kept before the nightly purge
    /// deletes them (ADM-20, issue #193). <c>Settings.Edit</c>, as doc 08 §3.8's table states.
    /// <para>
    /// <b>The database row wins; configuration is the fallback.</b> The purge reads these rows every night.
    /// <c>Traffic__RawRetentionDays</c> and <c>Traffic__BotRetentionDays</c> are used only while a row is unset or
    /// holds something outside the bounds below (scripted in by hand, say) — so a deployment that has never been
    /// seeded behaves exactly as it did before the form existed, and the seeded values are doc 08 §3.8's own 90
    /// and 7, which are also the configuration defaults. See <c>TrafficRetentionPeriods</c>.
    /// </para>
    /// <para>
    /// <b>The bounds are narrower than configuration's</b> (4 to 3,650), on purpose: these are what a staff user
    /// may type into a form, and a purge is irreversible. The daily tables are kept indefinitely whatever these
    /// say, so the Traffic page's history never depends on them; what they control is how long visit-level rows
    /// (one per page view, with a random session id) exist.
    /// </para>
    /// </summary>
    public static class Traffic
    {
        public const string Group = "Traffic";

        /// <summary>Days of page views kept, today included. Doc 08 §8.4: 90.</summary>
        public const string RawRetentionDays = "Traffic.RawRetentionDays";

        /// <summary>Days of bot page views kept, today included. Doc 08 §8.4: 7.</summary>
        public const string BotRetentionDays = "Traffic.BotRetentionDays";

        /// <summary>Doc 08 §3.8's seed for <see cref="RawRetentionDays"/>.</summary>
        public const int RawRetentionDaysDefault = 90;

        /// <summary>Doc 08 §3.8's seed for <see cref="BotRetentionDays"/>.</summary>
        public const int BotRetentionDaysDefault = 7;

        /// <summary>
        /// Thirty days: a month of visit-level rows to look back at when a figure on the Traffic page needs
        /// explaining. (Losing history to a short period is not the reason — the purge never deletes a day the rollup
        /// has not built, and the rollup's catch-up finds such days however old they are.)
        /// </summary>
        public const int RawRetentionDaysMin = 30;

        /// <summary>
        /// One year. The daily tables already keep every day's totals indefinitely, so raw rows older than this
        /// serve no report; they only keep visit-level data for longer.
        /// </summary>
        public const int RawRetentionDaysMax = 365;

        /// <summary>
        /// Seven days, doc 08's own figure: bot views are counted into the daily tables by the same rollup, so the
        /// same outage argument applies, and a week leaves four days past the three-day rebuild window.
        /// </summary>
        public const int BotRetentionDaysMin = 7;

        /// <summary>
        /// Ninety days, and never more than <see cref="RawRetentionDays"/>: bot rows are kept out of every visitor
        /// figure, so keeping them longer than people's views has no use.
        /// </summary>
        public const int BotRetentionDaysMax = 90;
    }

    /// <summary>
    /// Group <c>Reviews</c> (doc 08 §3.8, doc 11 P12; issue #189): whether a customer's review waits for a
    /// moderator before it is shown.
    /// </summary>
    public static class Reviews
    {
        public const string Group = "Reviews";

        /// <summary>
        /// <c>true</c>: a new or edited review is Pending until staff approve it. <c>false</c>: it is shown at
        /// once. Seeded <b>true</b>, doc 08 §3.8's default, and read per submission; a missing or unreadable value
        /// also means <b>true</b>, because the safe failure is a review that waits rather than one that publishes.
        /// </summary>
        public const string RequireApproval = "Reviews.RequireApproval";

        /// <summary>Doc 08 §3.8's seed for <see cref="RequireApproval"/>.</summary>
        public const bool RequireApprovalDefault = true;
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

    /// <summary>
    /// Group <c>Seed</c>: the seeder's own bookkeeping, and <b>not a group any screen shows</b>.
    /// <para>
    /// <b>Deliberately absent from <see cref="SettingCatalog"/>, which is what keeps it off the settings
    /// screen.</b> Every read model and every write handler starts from the catalogue — <c>SettingsStore</c>
    /// only ever queries <c>WHERE Key IN (@catalogue keys)</c> — so a row whose key is not in the catalogue
    /// is invisible to the admin API and unwritable through it. <c>SettingsSeeder</c> leaves such rows
    /// alone rather than deleting them, which is the behaviour this key needs. A unit test asserts it is
    /// not in the catalogue, because putting it there would offer an operator a box in which to break the
    /// upgrade step.
    /// </para>
    /// </summary>
    public static class Seed
    {
        public const string Group = "Seed";

        /// <summary>
        /// Doc 08 §9's <c>Seed.Version</c>: the highest <see cref="Identity.DefaultRoleSeedVersions"/>
        /// version whose permission increments have been offered to the default roles in this database
        /// (issue #122). An <see cref="SettingValueType.Int"/> row, written by the seeder and read by
        /// nothing else.
        /// </summary>
        public const string Version = "Seed.Version";

        /// <summary>
        /// The highest <c>HomeContentData</c> seed version applied to this database (#63, #192). The same
        /// versioned-increment rule as <see cref="Version"/>: content introduced in a version above the one
        /// recorded here cannot have been edited or removed by this database's owner, because it did not
        /// exist for them to touch — so it is offered once, and never again. An
        /// <see cref="SettingValueType.Int"/> row, written by the seeder and read by nothing else.
        /// </summary>
        public const string HomeContentVersion = "Seed.HomeContentVersion";
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
