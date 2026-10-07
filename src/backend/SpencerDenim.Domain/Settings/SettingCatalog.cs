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
public sealed record SettingDefinition(
    string Key,
    string GroupKey,
    SettingValueType ValueType,
    string RequiredPermission,
    string Label,
    string? Description)
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
/// than from a caller. <see cref="SettingKeys"/> explains which of doc 08 §3.8's sixteen groups are
/// missing and what each is waiting for.
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
            "The bank a customer transfers to, exactly as it appears in online banking - for example "
            + "Commercial Bank of Ceylon PLC. Customers cannot pay by bank transfer until the bank name, "
            + "account name and account number are all filled in."),

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
        // The logo on its own: changing it changes every page instantly, so it is a deliberate act with its
        // own audit row rather than a field that rides along with a form.
        [SettingKeys.Store.Group],

        [SettingKeys.Contact.Group, SettingKeys.Social.Group],

        // The bank details on their own. They are one form filled in from one message from the client, and
        // they are guarded by a different permission from every other group — a save unit that spanned both
        // would hand a Settings.Edit holder a token covering rows they may not write.
        [SettingKeys.Bank.Group],
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
