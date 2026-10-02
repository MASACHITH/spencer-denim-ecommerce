namespace SpencerDenim.Domain.Settings;

/// <summary>
/// One row of <c>ops.Settings</c> (doc 08 §3.8; ADM-22, GLB-02, PDP-05, PAY-03) <c>[RV]</c>.
/// <para>
/// <b>A row per key, created by the seeder, never by a client.</b> <see cref="GroupKey"/>,
/// <see cref="ValueType"/>, <see cref="IsSecret"/> and above all
/// <see cref="RequiredPermission"/> describe the key rather than the value, and a write path that
/// could create a row would be a write path that could choose its own
/// <see cref="RequiredPermission"/> — a caller inventing <c>Bank.AccountNumber</c> with
/// <c>Settings.Edit</c> on it. So every write this slice offers is an <i>update</i> of a seeded row,
/// and the permission is read off the row that is already there.
/// </para>
/// <para>
/// <b><see cref="RowVersion"/> exists because doc 08 marks this table <c>[RV]</c></b>, unlike
/// <c>sales.Carts</c>. Two staff users on one settings screen is the ordinary case, not a race worth
/// ignoring: without it the second save silently overwrites the first, and both audit rows claim to
/// have changed the value from what the screen last showed.
/// </para>
/// </summary>
public sealed class Setting
{
    /// <summary>
    /// The dotted key, e.g. <c>Store.LogoAssetId</c>. <c>varchar(100)</c>, and the primary key — so the
    /// row is addressed by what it means rather than by a surrogate id a screen would have to carry.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The group a settings screen draws together, e.g. <c>Store</c>. <c>varchar(50)</c>, indexed.</summary>
    public string GroupKey { get; set; } = string.Empty;

    public SettingValueType ValueType { get; set; }

    /// <summary>
    /// The value, in invariant culture, or <see langword="null"/> for a key nobody has filled in.
    /// <para>
    /// <b>Null is a first-class state here</b>, and it is the state every key in this slice starts in:
    /// the client has not sent their social profile addresses and has not uploaded a logo. A storefront
    /// that renders a null as a link or an <c>&lt;img&gt;</c> is the defect this column's nullability is
    /// warning about.
    /// </para>
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// <c>CK_Settings_IsSecret</c>: 1 if and only if <see cref="ValueType"/> is
    /// <see cref="SettingValueType.Secret"/>. Kept as its own column rather than derived, because every
    /// read model and every audit row has to branch on it and a computed column cannot be indexed into
    /// a CHECK the way the document specifies it.
    /// </summary>
    public bool IsSecret { get; set; }

    /// <summary>
    /// The permission a caller must hold to read or write this key: <c>Settings.Edit</c>,
    /// <c>Settings.Payments</c> for every <c>Bank.*</c> and <c>Payments.*</c> key (doc 08 DB2), or
    /// <c>Settings.Security</c> — GrantsAll-only — for <c>Email.Smtp.*</c>, <c>Security.*</c> and
    /// <c>Privacy.RetainInvoiceBillTo</c> (N2, R12). <c>varchar(64)</c>.
    /// </summary>
    public string RequiredPermission { get; set; } = string.Empty;

    /// <summary>What a staff user is told the key is for. <c>nvarchar(300)</c>.</summary>
    public string? Description { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>Doc 08 §1.5 <c>[RV]</c>: the concurrency token, exchanged with a client as an ETag.</summary>
    public byte[]? RowVersion { get; set; }
}
