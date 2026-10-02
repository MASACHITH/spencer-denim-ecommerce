namespace SpencerDenim.Domain.Settings;

/// <summary>
/// The <c>ops.Settings</c> keys this milestone populates (ADM-22, GLB-02). Doc 08 §3.8's "Groups" table
/// is the authority for the names; these are the subset with a screen behind them.
/// <para>
/// <b>What is deliberately absent, and why that is not an oversight.</b> Doc 08 lists sixteen groups.
/// <c>Bank.*</c> and <c>Payments.PayPal.*</c> need <c>Settings.Payments</c> and a payments screen
/// (issue #53, W3); <c>Email.Smtp.*</c>, <c>Security.*</c> and <c>Privacy.RetainInvoiceBillTo</c> need
/// <c>Settings.Security</c>, the sensitive-change alert and <c>ops.AdminAlerts</c>, none of which exists;
/// <c>Delivery</c>, the couriers and the delivery methods need <c>sales.DeliveryMethods</c> and friends,
/// which are tables #53 creates. A key seeded here would be a key a screen offers and nothing honours —
/// the shape of defect this project has twice decided against (the bag's promo code, "popular searches").
/// The table, its constraints and the permission machinery are all built, so those keys drop in as rows.
/// </para>
/// <para>
/// <b><c>Store.Name</c> is absent on purpose, and this is the one place it could plausibly belong.</b>
/// Doc 08 §3.8 lists <c>Name</c> in the <c>Store</c> group and doc 11 P14 step 11 lists "Store name"
/// under <c>Settings.Edit</c> — both predate the owner's decision of 2026-10-02 (docs/02 §I2, W1-15),
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

    /// <summary><c>ops.Settings.Key</c> is <c>varchar(100)</c>.</summary>
    public const int KeyMaxLength = 100;

    /// <summary><c>ops.Settings.GroupKey</c> is <c>varchar(50)</c>.</summary>
    public const int GroupKeyMaxLength = 50;

    /// <summary><c>ops.Settings.RequiredPermission</c> is <c>varchar(64)</c>.</summary>
    public const int RequiredPermissionMaxLength = 64;

    /// <summary><c>ops.Settings.Description</c> is <c>nvarchar(300)</c>.</summary>
    public const int DescriptionMaxLength = 300;
}
