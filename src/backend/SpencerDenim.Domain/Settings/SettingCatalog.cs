using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Domain.Settings;

/// <summary>
/// One <c>ops.Settings</c> key as the seeder will create it. The shape of the key, never its value.
/// </summary>
/// <param name="Key">The dotted key.</param>
/// <param name="GroupKey">The group a screen draws it under.</param>
/// <param name="ValueType">Doc 08 §3.8's <c>ValueType</c>.</param>
/// <param name="RequiredPermission">
/// What a caller must hold to read or write it. Taken from here and never from a request, which is the
/// whole reason the catalogue exists in the Domain rather than in a request DTO.
/// </param>
/// <param name="Label">
/// A short name a person recognises, for a list that has room for a name and not a sentence — the media
/// library's "used in…" panel (doc 08 §3.3 calls that field "a name a person recognises").
/// <para>
/// Separate from <paramref name="Description"/> because they are different jobs and the first version used
/// the description for both: a 185-character paragraph arrived as a <c>MediaUsageDto.OwnerLabel</c> beside
/// "Wide-Leg Rinse Jeans". <see cref="LabelMaxLength"/> is asserted by a test.
/// </para>
/// </param>
/// <param name="Description">What a staff user is told the key is for, in full, on the settings screen.</param>
/// <param name="SeedValue">
/// What the seeder writes into a <b>missing</b> row, in invariant culture, or <see langword="null"/> for the
/// overwhelming majority of keys — which hold information only the client has.
/// <para>
/// <b>It is applied on insert and never on realign</b>, which is <c>SettingsSeeder</c>'s whole asymmetry:
/// the metadata is this codebase's and is brought back into line every run, the value is the client's and
/// is never touched again. A seed value that was re-applied would be W1-12's defect with a longer fuse — a
/// deployment silently resetting a number somebody deliberately changed.
/// </para>
/// <para>
/// <b>Only use it for a value this project's own documents state</b>, such as doc 08 §3.8's
/// <c>AutoDeliverAfterDays (7)</c>. A client fact with a plausible-looking default is the defect this
/// project has shipped twice: a courier name, a bank, an address. Those stay <see langword="null"/> and
/// render as nothing.
/// </para>
/// </param>
public sealed record SettingDefinition(
    string Key,
    string GroupKey,
    SettingValueType ValueType,
    string RequiredPermission,
    string Label,
    string? Description,
    string? SeedValue = null)
{
    /// <summary>How long a <see cref="Label"/> may be to still read as a name rather than a sentence.</summary>
    public const int LabelMaxLength = 60;

    /// <summary>
    /// <c>CK_Settings_IsSecret</c>, as a property rather than a second field anybody could get wrong:
    /// a key is secret if and only if its type is <see cref="SettingValueType.Secret"/>.
    /// </summary>
    public bool IsSecret => ValueType == SettingValueType.Secret;
}

/// <summary>
/// The settings keys this build knows about (ADM-22), in the order a screen shows them.
/// <para>
/// <b>This is the allow-list, in both directions.</b> The seeder creates exactly these rows, and a key
/// that is not here has no row — so a write path cannot reach one, a read model cannot project one, and
/// the question "which permission guards this key" always has an answer that came from source rather
/// than from a caller. <see cref="DeferredSettingKeys"/> records every key of doc 08 §3.8's fifteen
/// groups that this build does not seed, and why, and a test diffs both lists against the document
/// itself.
/// </para>
/// </summary>
public static class SettingCatalog
{
    /// <summary>Every key this build seeds, in display order.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(
            SettingKeys.Store.LogoAssetId,
            SettingKeys.Store.Group,
            SettingValueType.MediaAssetRef,
            Permissions.Settings.Edit,
            "The shop's logo",
            "The shop's logo, shown in the header instead of the text wordmark. Leave it empty to show "
            + "the wordmark. Replacing it releases the previous image so the media library will let you "
            + "delete it."),

        new(
            SettingKeys.Contact.WhatsApp,
            SettingKeys.Contact.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "WhatsApp number",
            "The WhatsApp number the floating button opens, in international form — for example "
            + "94771234567. No plus sign, no spaces."),

        new(
            SettingKeys.Contact.Phone,
            SettingKeys.Contact.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Telephone number",
            "The shop's telephone number, as you want shoppers to read it — for example "
            + "+94 11 234 5678. It is shown on the Contact page and in the footer, and tapping it dials."),

        new(
            SettingKeys.Contact.Email,
            SettingKeys.Contact.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Contact email address",
            "The address shoppers should write to. Leave it empty and no email address is shown. "
            + "Nothing is sent from this address by the shop."),

        new(
            SettingKeys.Contact.Address,
            SettingKeys.Contact.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Postal address",
            "The shop's address, one line per line. Plain text, not HTML."),

        new(
            SettingKeys.Contact.OpeningHours,
            SettingKeys.Contact.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Opening hours",
            "When the shop is open, one line per line — for example “Monday to Friday, 9am – 6pm”. "
            + "Plain text, not HTML."),

        new(
            SettingKeys.Contact.MapEmbedUrl,
            SettingKeys.Contact.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Map embed address",
            "The “Embed a map” address from Google Maps (Share → Embed a map → copy the src) or "
            + "from OpenStreetMap. Only those two are accepted. Leave it empty and the Contact page "
            + "shows no map."),

        new(
            SettingKeys.Social.FacebookUrl,
            SettingKeys.Social.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Facebook page",
            "The full address of the shop's Facebook page. Leave it empty and the footer shows no "
            + "Facebook icon."),

        new(
            SettingKeys.Social.InstagramUrl,
            SettingKeys.Social.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Instagram profile",
            "The full address of the shop's Instagram profile. Leave it empty and the footer shows no "
            + "Instagram icon."),

        new(
            SettingKeys.Social.TikTokUrl,
            SettingKeys.Social.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "TikTok profile",
            "The full address of the shop's TikTok profile. Leave it empty and the footer shows no "
            + "TikTok icon."),

        // Issue #193: the fourth of doc 08 §3.8's Social keys, which waited for a glyph in the shared icon set.
        new(
            SettingKeys.Social.YouTubeUrl,
            SettingKeys.Social.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "YouTube channel",
            "The full address of the shop's YouTube channel. Leave it empty and the footer shows no "
            + "YouTube icon."),

        // Group Bank (doc 08 §3.8, doc 11 P7): the account a customer transfers to. Settings.Payments on
        // every row, which is the first time this catalogue carries a permission other than Settings.Edit —
        // and the reason GetAdminSettingsQueryHandler's per-row filter was built before there was anything
        // for it to filter.
        new(
            SettingKeys.Bank.BankName,
            SettingKeys.Bank.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "Bank name",
            // No example bank here. A named real bank in help text that renders beside the box a Super
            // Admin types the real one into is a value mistakable for data, which is the MAJOR 4 criterion.
            "The bank a customer transfers to, exactly as it appears in online banking. Customers cannot "
            + "pay by bank transfer until the bank name, account name and account number are all filled "
            + "in."),

        new(
            SettingKeys.Bank.Branch,
            SettingKeys.Bank.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "Branch",
            "The branch the account is held at. Optional: a branch deposit slip asks for it, online "
            + "banking usually does not."),

        new(
            SettingKeys.Bank.AccountName,
            SettingKeys.Bank.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "Account name",
            "The name the account is held in. A customer's bank may check it against the account number, "
            + "so it must match the bank's records exactly."),

        new(
            SettingKeys.Bank.AccountNumber,
            SettingKeys.Bank.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "Account number",
            "The account number customers transfer to. Digits, spaces and dashes only. Check it twice: "
            + "it is shown to every customer who chooses bank transfer."),

        new(
            SettingKeys.Bank.Instructions,
            SettingKeys.Bank.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "Extra instructions",
            "Optional notes shown under the bank details, for example how long a cash-machine deposit "
            + "takes to appear. Plain text; line breaks are kept."),

        // Group Store, part two (W3-04, ADM-09): the seller's details an invoice has to carry (doc 08
        // §3.5 InvoiceSellerSnapshotJson, §4.1). Every one of them is **null until the client fills it
        // in**, and the invoice prints nothing at all for an unset value — see InvoiceSellerDetails. A
        // default here would be an invented registered address on a legal document, which is strictly
        // worse than a gap somebody can see and ask about.
        new(
            SettingKeys.Store.LegalName,
            SettingKeys.Store.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Registered company name",
            // No example company. The shop's own client name as help text beside the box the real one is
            // typed into is a value mistakable for data, which is what W3-03 was marked Major for.
            "The registered name of the company that issues invoices. This is the only place the company "
            + "name is used — everything a shopper reads is the shop's name. Invoices print nothing here "
            + "until it is filled in."),

        new(
            SettingKeys.Store.Address,
            SettingKeys.Store.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Registered address",
            "The company's registered address as it should appear on an invoice, one line per line. This "
            + "is not the contact address shoppers write to, which is under Contact. Invoices print "
            + "nothing here until it is filled in."),

        new(
            SettingKeys.Store.BRNumber,
            SettingKeys.Store.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "Business registration number",
            "The company's business registration number, for the invoice header. Invoices print nothing "
            + "here until it is filled in."),

        new(
            SettingKeys.Store.VatNumber,
            SettingKeys.Store.Group,
            SettingValueType.String,
            Permissions.Settings.Edit,
            "VAT registration number",
            "The company's VAT registration number, for the invoice header. Prices already include VAT, "
            + "so an invoice shows no VAT line — this is the registration number only. Invoices print "
            + "nothing here until it is filled in."),

        // Group Orders (doc 08 §3.8, doc 11 P9 step 8). The one key with a seeded value in this build,
        // because 7 is a figure the documents state rather than one only the client knows.
        new(
            SettingKeys.Orders.AutoDeliverAfterDays,
            SettingKeys.Orders.Group,
            SettingValueType.Int,
            Permissions.Settings.Edit,
            "Mark delivered after",
            "How many days after dispatch a shipped order is marked Delivered automatically, if nobody "
            + "does it by hand. Store pickup orders are never marked automatically: an order nobody has "
            + "collected has not been delivered. Between 1 and 90 days.",
            SeedValue: SettingKeys.Orders.AutoDeliverAfterDaysDefault.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),

        // Group Payments.PayPal (doc 08 §3.8, W4-01), and Orders.PayPalExpiryMinutes. Their own file, so the
        // payment providers that follow add theirs beside these.
        .. PayPalSettingDefinitions.All,

        // Group Traffic (doc 08 §3.8, §8.4; issue #193): how long the nightly purge keeps raw page views. Seeded
        // with doc 08's own 90 and 7, which are also TrafficOptions' configuration defaults, so seeding an
        // existing database changes nothing the purge does. The row wins over Traffic__* once it holds a value
        // inside the bounds; see TrafficRetentionPeriods.
        new(
            SettingKeys.Traffic.RawRetentionDays,
            SettingKeys.Traffic.Group,
            SettingValueType.Int,
            Permissions.Settings.Edit,
            "Keep page views for",
            "How many days of individual page views are kept, today included, before the nightly purge deletes "
            + "them. The daily totals on the Traffic page are kept for ever whatever this says. Between 30 and "
            + "365 days.",
            SeedValue: SettingKeys.Traffic.RawRetentionDaysDefault.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),

        new(
            SettingKeys.Traffic.BotRetentionDays,
            SettingKeys.Traffic.Group,
            SettingValueType.Int,
            Permissions.Settings.Edit,
            "Keep bot page views for",
            "How many days of page views from search engines and link previews are kept. They are never counted "
            + "as visitors. Between 7 and 90 days, and no longer than page views are kept.",
            SeedValue: SettingKeys.Traffic.BotRetentionDaysDefault.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),

        // Group Reviews (doc 08 §3.8, doc 11 P12; issue #189). Seeded "true", doc 08's own "(true)", so a new
        // database starts with every review waiting for a moderator. The seeder never overwrites a saved value.
        new(
            SettingKeys.Reviews.RequireApproval,
            SettingKeys.Reviews.Group,
            SettingValueType.Bool,
            Permissions.Settings.Edit,
            "Approve reviews before they appear",
            "When this is on, a customer's new or edited review waits on the Reviews screen until somebody "
            + "approves it. When it is off, reviews appear on the product page as soon as they are written. A "
            + "review staff have hidden stays hidden either way.",
            SeedValue: SettingKeys.Reviews.RequireApprovalDefault ? "true" : "false"),
    ];

    /// <summary>
    /// Keyed <b>case-insensitively</b>, to match the database.
    /// <para>
    /// The collation is <c>Latin1_General_100_CI_AS_SC</c>, so SQL Server treats <c>social.facebookurl</c> and
    /// <c>Social.FacebookUrl</c> as one row — and an <c>Ordinal</c> dictionary here would not. The
    /// consequence was specific and permanent: a row scripted in with different casing is found by SQL and
    /// "realigned" by the seeder, which reports success, and then <i>missed</i> by every ordinal lookup — so
    /// the box disappears from the screen and every save answers <c>NotSeeded</c>, which is a 500, for ever,
    /// while the advertised repair path reports nothing wrong.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The definition of <paramref name="key"/>, or <see langword="null"/> when this build has none.</summary>
    public static SettingDefinition? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>Every group this build populates, in display order, without repeats.</summary>
    public static IReadOnlyList<string> Groups { get; } =
        All.Select(definition => definition.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Which groups are saved <b>in one request</b> — the forms a staff user sees, each with one Save button.
    /// <para>
    /// <b>This exists because getting it wrong cost ADM-22 a working screen.</b> The read model published a
    /// concurrency token per <i>group</i>, and the contact-and-social save compared it against the rows of
    /// <i>both</i> groups, because one form saves both; every valid save answered 409. The two sides lived in
    /// two files and nothing made them agree. They now both read this list, so the token a screen is handed is
    /// by construction the token its save will compare.
    /// </para>
    /// <para>
    /// Doc 08 §3.8 keeps <c>Contact</c> and <c>Social</c> as separate groups and that is respected — a group is
    /// a unit of <i>permission and display</i>. A save unit is a unit of <i>concurrency</i>, and it is a
    /// coarser thing: the WhatsApp number and the three social addresses are one strip of the footer, filled in
    /// from one email from the client, and splitting the save would mean a partial success nobody could
    /// describe.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> SaveUnits { get; } =
    [
        // Store and Orders together (W3-04). Two forms on the settings screen share this token — the logo,
        // which has its own command because changing it changes every page instantly, and the invoice and
        // fulfilment form below it. They are one unit because the screen re-reads after every save, so a
        // wider token costs a staff user nothing, and because `KeysInSaveUnitFor` is what keeps the token
        // a form is handed identical to the one its save compares. Splitting them would mean declaring the
        // same relationship twice, which is the shape of the defect this list was created to end.
        [SettingKeys.Store.Group, SettingKeys.Orders.Group],

        [SettingKeys.Contact.Group, SettingKeys.Social.Group],

        // The bank details on their own. They are one form filled in from one message from the client, and
        // they are guarded by a different permission from every other group — a save unit that spanned both
        // would hand a Settings.Edit holder a token covering rows they may not write.
        [SettingKeys.Bank.Group],

        // PayPal's credentials, rate and timings: one form, one permission (Settings.Payments).
        // Orders.PayPalExpiryMinutes is written by this form too but lives in the Orders group (doc 08 §3.8's
        // name and permission for it, Settings.Edit), so it is covered by the Store-and-Orders token above
        // and not by this one - a unit may not span two permissions.
        [SettingKeys.PayPal.Group],

        // The two retention periods: one form, one permission (Settings.Edit), and a cross-field rule (bots never
        // longer than people) that only holds if both are compared under one token.
        [SettingKeys.Traffic.Group],

        // The review approval switch (issue #189): one form, one permission (Settings.Edit).
        [SettingKeys.Reviews.Group],
    ];

    /// <summary>
    /// Every key saved in the same request as <paramref name="groupKey"/>, which is the set a concurrency
    /// token covers.
    /// <para>
    /// A group nobody has declared a save unit for answers with its own keys — so a key added to the catalogue
    /// in a new group is readable and writable rather than silently unsaveable.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> KeysInSaveUnitFor(string groupKey)
    {
        var unit = SaveUnits.FirstOrDefault(candidate => candidate.Contains(groupKey, StringComparer.OrdinalIgnoreCase))
            ?? [groupKey];

        return All
            .Where(definition => unit.Contains(definition.GroupKey, StringComparer.OrdinalIgnoreCase))
            .Select(definition => definition.Key)
            .ToList();
    }

    /// <summary>
    /// The keys of one group, in display order. Used by the write handlers to say exactly which rows a
    /// command may touch, so a handler cannot be pointed at a key from another group.
    /// </summary>
    public static IReadOnlyList<string> KeysIn(string groupKey) =>
        All.Where(definition => string.Equals(definition.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
            .Select(definition => definition.Key)
            .ToList();
}
