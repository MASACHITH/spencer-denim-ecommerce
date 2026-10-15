namespace SpencerDenim.Domain.Settings;

/// <summary>Why one of doc 08 §3.8's keys has no row.</summary>
public enum SettingKeyDeferralKind
{
    /// <summary>
    /// The key will be seeded by the task that builds the feature reading it. Until then a row would be a
    /// box on the settings screen that nothing honours.
    /// </summary>
    AwaitingItsFeature = 1,

    /// <summary>
    /// The key will never be seeded: a decision outside doc 08 §3.8 overrides it, and the decision is
    /// named in the reason.
    /// </summary>
    Withdrawn = 2,
}

/// <summary>One of doc 08 §3.8's keys that this build does not seed, and why.</summary>
/// <param name="Key">The dotted key, exactly as doc 08 §3.8's "Groups" table spells it.</param>
/// <param name="Kind">Whether it is waiting for a feature or has been withdrawn.</param>
/// <param name="Reason">
/// What a reader needs in order to agree or disagree: what reads the value today, and what has to exist
/// before a row would mean anything.
/// </param>
public sealed record DeferredSettingKey(string Key, SettingKeyDeferralKind Kind, string Reason);

/// <summary>
/// Every key doc 08 §3.8 lists that <see cref="SettingCatalog"/> does not seed, with the decision for each
/// (issue #113).
/// <para>
/// <b>This list exists so that "which of §9's keys are missing" has an answer in source rather than in a
/// task report.</b> <c>SettingKeySpecificationTests</c> parses doc 08 §3.8's "Groups" table and asserts
/// that every key it finds is either in <see cref="SettingCatalog"/> or here, that nothing is in both, and
/// that nothing here is absent from the document. So a key added to doc 08 fails the build until somebody
/// decides about it, and a key left here after its feature ships fails the build too.
/// </para>
/// <para>
/// <b>Why an absent key is not an empty key.</b> The admin screen can only edit what the catalogue knows
/// about (<c>SettingsStore.ListAsync</c> queries the catalogue's keys and nothing else), so an absent key
/// is not editable at all and the client cannot set it; anything reading it falls back to the code's
/// default. That makes "absent" the wrong state for a key a built feature reads — and the right state for
/// a key no built feature reads, because a seeded row would be a promise the shop does not keep. Each
/// reason below therefore names what reads the value <i>today</i>.
/// </para>
/// <para>
/// <b>Checked, for every key here whose value has a code-side fallback: the fallback is §3.8's own
/// default.</b> <c>CatalogOptions</c> ships 5 / 30 / 10 for <c>Inventory.DefaultLowStockThreshold</c>,
/// <c>Catalog.NewArrivalDays</c> and <c>Catalog.BestSellerTopN</c>; <c>BankTransferOptions</c> ships 48 for
/// <c>Orders.BankTransferExpiryHours</c>; <c>AdminSecurityOptions</c> ships 5 and 15 for
/// <c>Security.StaffLockoutMaxFailures</c> and <c>StaffLockoutMinutes</c>; <c>TrafficOptions</c> ships 90 and 7 for
/// <c>Traffic.RawRetentionDays</c> and <c>BotRetentionDays</c> (W4-03). So no key below is silently
/// running on a number that differs from the document, which is the half of #113 that would have been a
/// defect rather than an omission. <c>SettingKeySpecificationTests</c> pins all eight, against the
/// document's own cells rather than against a second copy of the number.
/// </para>
/// </summary>
public static class DeferredSettingKeys
{
    /// <summary>Every deferred key, grouped the way doc 08 §3.8 groups them.</summary>
    public static IReadOnlyList<DeferredSettingKey> All { get; } =
    [
        // ---- Store -------------------------------------------------------------------------------------
        new(
            "Store.Name",
            SettingKeyDeferralKind.Withdrawn,
            "The shop's name is a build-time constant in exactly one place per application "
            + "(StoreBrand.Name and BRAND in @spencer-denim/shared), by the owner's decision of "
            + "2026-10-02 (docs/02 §I2, W1-15), and BrandNameConventionTests fails the build if it is "
            + "written anywhere else. A settings row would give it a second source of truth, one of which "
            + "the server renders the storefront from before any database is read. Store.LegalName is the "
            + "company that issues an invoice and is seeded; this key is the shop, and is not."),

        new(
            "Store.Email",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "Nothing reads it. The address shoppers write to is Contact.Email, which is seeded; the "
            + "invoice's seller block is IOrderSettingsReader.ReadInvoiceSellerAsync, which reads "
            + "LegalName, Address, BRNumber and VatNumber and prints no email. A row here would be a box "
            + "with no consumer."),

        new(
            "Store.FaviconAssetId",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "GLB-02's second and third image slots. Nothing reads either: the storefront's favicon is a "
            + "static asset (index.html's link rel=icon) and PageMetadata takes og:image from the page's "
            + "own image, removing the tag when there is none. They arrive with the task that makes the "
            + "shop's icon and default share image client-supplied, which also needs the media-usage "
            + "wiring Store.LogoAssetId already has."),

        new(
            "Store.DefaultOgImageAssetId",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "See Store.FaviconAssetId: nothing reads it, and PageMetadata takes og:image from the page's "
            + "own media."),

        // ---- Social ------------------------------------------------------------------------------------
        new(
            "Social.YouTubeUrl",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "The footer renders one icon per configured social address and @spencer-denim/shared's "
            + "IconName has no YouTube glyph, so a value typed here would have nowhere to render. It is a "
            + "row and an icon away; the other three of doc 08's four are seeded."),

        // ---- Content -----------------------------------------------------------------------------------
        new(
            "Content.PdpReturnsInfoHtml",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "PDP-05's returns and delivery panels do not exist: nothing in the storefront or the API "
            + "reads either key. The allow-list HTML sanitiser these two would need (doc 08 N10) does now "
            + "exist — W2-11 built IHtmlSanitiser — so the blocker is the panel, not the sanitiser."),

        new(
            "Content.PdpDeliveryInfoHtml",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "See Content.PdpReturnsInfoHtml: the PDP has no panel reading it."),

        // ---- Orders ------------------------------------------------------------------------------------
        new(
            "Orders.BankTransferExpiryHours",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "Read from configuration as Orders__BankTransferExpiryHours (BankTransferOptions), whose "
            + "default is doc 08 §3.8's 48. Seeding the key without moving the reader would give the "
            + "payment deadline two sources that could disagree, on the path that decides when an unpaid "
            + "order is cancelled. Moving it is owed to ADM-22 and is listed in W3-03's journal entry."),

        // ---- Inventory and Catalog ---------------------------------------------------------------------
        new(
            "Inventory.DefaultLowStockThreshold",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "Read from configuration as Catalog__DefaultLowStockThreshold (CatalogOptions), whose default "
            + "is doc 08 §3.8's 5. The reader is on the catalogue's hot path and moving it to ops.Settings "
            + "needs the cached options wiring SiteSettingsCache has; until then a seeded row would be an "
            + "editable box the shop does not read."),

        new(
            "Catalog.NewArrivalDays",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "As Inventory.DefaultLowStockThreshold: CatalogOptions, default 30, doc 08 §3.8's own figure."),

        new(
            "Catalog.BestSellerTopN",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "As Inventory.DefaultLowStockThreshold: CatalogOptions, default 10, doc 08 §3.8's own figure."),

        // ---- Reviews -----------------------------------------------------------------------------------
        new(
            "Reviews.RequireApproval",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "There is no review submission or moderation path yet, so nothing reads it in any form — not "
            + "even a configuration fallback. It belongs to the task that lets a customer write a review."),

        // ---- Delivery ----------------------------------------------------------------------------------
        new(
            "Delivery.DefaultZone",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "Checkout prices per district and asks for one before it quotes (GetCheckoutQuery), so there "
            + "is no path that needs a zone with no district. The key is for the signed-in-customer "
            + "default of R7, which waits on customer accounts (W3-01, ACC-01)."),

        // ---- Privacy -----------------------------------------------------------------------------------
        new(
            "Privacy.RetainInvoiceBillTo",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "Settings.Security (R12), and nothing reads it: the PDPA anonymisation path that would honour "
            + "it is not built. It also needs the doc 08 §3.8 N2 sensitive-change alert, because every "
            + "Settings.Security key is in N2's trigger list."),

        // ---- Traffic -----------------------------------------------------------------------------------
        new(
            "Traffic.RawRetentionDays",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "analytics.PageViews and the raw-traffic purge exist (W4-03), and the purge keeps doc 08 §8.4's 90 "
            + "days — from configuration (Traffic__RawRetentionDays, TrafficOptions), not from this table. The "
            + "settings screen has one form per group and no Traffic form, so a seeded row would be a value staff "
            + "can see and cannot change. Seed it with the form; until then the code-side default is §3.8's own."),

        new(
            "Traffic.BotRetentionDays",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "See Traffic.RawRetentionDays: the purge keeps bot page views doc 08 §8.4's 7 days, from "
            + "configuration (Traffic__BotRetentionDays), until the settings screen has a Traffic form."),

        // ---- Email.Smtp ------------------------------------------------------------------------------
        // Seven keys, one reason. ops.EmailOutbox now exists (#133) and so does ops.AdminAlerts (#138),
        // so the tables are no longer the blocker — #113 guessed they were, and that guess is out of date.
        // Two things still are. The transport is handed the bound EmailOptions singleton, i.e.
        // Email__Smtp__* read once at startup, so a
        // seeded row would not configure anything; and doc 08 §3.8 N2 requires an SMTP change to send its
        // alert twice, once through the new configuration and once through the previous one carried as
        // Data Protection ciphertext in EmailOutbox.TransportOverrideProtected. That dual send does not
        // exist, and "a hijacked SMTP change cannot silence its own alert" is the property it provides, so
        // making these keys editable before it exists would remove a control rather than add a setting.
        new("Email.Smtp.Host", SettingKeyDeferralKind.AwaitingItsFeature, SmtpReason),
        new("Email.Smtp.Port", SettingKeyDeferralKind.AwaitingItsFeature, SmtpReason),
        new("Email.Smtp.Username", SettingKeyDeferralKind.AwaitingItsFeature, SmtpReason),
        new("Email.Smtp.Password", SettingKeyDeferralKind.AwaitingItsFeature, SmtpReason),
        new("Email.Smtp.FromAddress", SettingKeyDeferralKind.AwaitingItsFeature, SmtpReason),
        new("Email.Smtp.FromName", SettingKeyDeferralKind.AwaitingItsFeature, SmtpReason),
        new("Email.Smtp.UseStartTls", SettingKeyDeferralKind.AwaitingItsFeature, SmtpReason),

        // ---- Security --------------------------------------------------------------------------------
        new(
            "Security.StaffLockoutMaxFailures",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "Applied to IdentityOptions.Lockout at startup from AdminSecurityOptions, whose default is "
            + "doc 08 §3.8's 5. The options object is built once and cached by the options system, so a "
            + "live-editable row needs the value read per sign-in attempt instead, as well as N2's "
            + "sensitive-change alert — this group is on N2's trigger list."),

        new(
            "Security.StaffLockoutMinutes",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "As Security.StaffLockoutMaxFailures: AdminSecurityOptions, default 15, doc 08 §3.8's own "
            + "figure."),

        new(
            "Security.CustomerLockoutMaxFailures",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "There are no customer accounts to lock out (W3-01, ACC-01), so nothing reads it in any form."),

        new(
            "Security.CustomerLockoutMinutes",
            SettingKeyDeferralKind.AwaitingItsFeature,
            "See Security.CustomerLockoutMaxFailures: no customer sign-in exists."),
    ];

    private const string SmtpReason =
        "The transport is handed the bound EmailOptions singleton — Email__Smtp__*, bound once at "
        + "startup — so a seeded row would configure nothing. ops.EmailOutbox (#133) and ops.AdminAlerts (#138) now exist, so the tables are no "
        + "longer the blocker; doc 08 §3.8 N2's dual send is, which carries the previous SMTP "
        + "configuration as ciphertext in EmailOutbox.TransportOverrideProtected so that a hijacked SMTP "
        + "change cannot silence its own alert. Making the keys editable first would remove that control.";

    /// <summary>The deferred keys as a set, for a membership test.</summary>
    public static IReadOnlySet<string> Keys { get; } =
        All.Select(deferred => deferred.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
