using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Contracts;

/// <summary>
/// The settings screen's read model (ADM-22).
/// <para>
/// <b>Only the groups the caller may see.</b> <see cref="Groups"/> is filtered by the caller's
/// permissions against each row's <c>RequiredPermission</c> (doc 08 §3.8 DB2), so a Store Manager gets no
/// <c>Bank</c> group at all rather than an empty one — a group the API refuses to return is also a group
/// whose existence it does not confirm.
/// </para>
/// </summary>
/// <param name="Groups">The groups this caller may read, in catalogue order.</param>
/// <param name="Logo">
/// The shop's logo as the screen needs to show it, resolved from <c>Store.LogoAssetId</c>, or
/// <see langword="null"/> when no logo is set or the asset behind it is no longer usable.
/// <para>
/// A typed property rather than extra fields on the <c>Store.LogoAssetId</c> row, because a
/// <see cref="SettingValueType.MediaAssetRef"/> is the one value type that is useless as text: the
/// screen needs a URL and a size to draw it, and the stored number means nothing to a person.
/// </para>
/// </param>
public sealed record AdminSettingsDto(
    IReadOnlyList<AdminSettingGroupDto> Groups,
    AdminLogoDto? Logo);

/// <summary>One group of settings, as a form.</summary>
/// <param name="GroupKey">Doc 08 §3.8's group name, e.g. <c>Social</c>.</param>
/// <param name="RowVersion">
/// The group's concurrency token (<see cref="SettingsGroupVersion"/>), sent straight back with a save.
/// </param>
/// <param name="Settings">The group's keys, in catalogue order.</param>
public sealed record AdminSettingGroupDto(
    string GroupKey,
    string RowVersion,
    IReadOnlyList<AdminSettingDto> Settings);

/// <summary>
/// One settings key as the screen sees it.
/// </summary>
/// <param name="Key">The dotted key.</param>
/// <param name="ValueType">Which control the screen draws.</param>
/// <param name="Value">
/// The value, or <see langword="null"/> for a key nobody has filled in — <b>and always
/// <see langword="null"/> when <paramref name="IsSecret"/> is true.</b>
/// <para>
/// <b>A secret's value never leaves the server, for any caller, through this contract.</b> That is
/// stronger than doc 08 requires and deliberately so. The weaker rule — "return it only to a caller with
/// the permission" — needs the permission check to be right on every future read path, and gets one of
/// them wrong eventually. The stronger rule needs nothing to be right: the projection that builds this
/// record writes <see langword="null"/> whenever <see cref="Setting.IsSecret"/> is set, so there is no
/// path to get it wrong on. Nothing is lost, either: ciphertext is unreadable to a person, and plaintext
/// on a settings screen is a credential in a browser's memory, in its cache and in anybody's screenshot.
/// A screen that needs to say "a password is set" reads <paramref name="HasValue"/>; a screen that needs
/// to change one sends a new one.
/// </para>
/// </param>
/// <param name="IsSecret">Doc 08 §3.8's <c>IsSecret</c>, true if and only if the type is <c>Secret</c>.</param>
/// <param name="HasValue">
/// Whether the row holds a value at all. The only thing a screen can learn about a secret, and what
/// lets it show "Set — enter a new one to replace it" rather than an empty box that looks unconfigured.
/// </param>
/// <param name="RequiredPermission">
/// Which permission guards the key. Returned so the screen can disable a control rather than let a save
/// fail — convenience only; the API is the boundary (doc 03 §3a).
/// </param>
/// <param name="Description">What the key is for, in plain language.</param>
/// <param name="UpdatedAtUtc">When it last changed, or <see langword="null"/> if never.</param>
public sealed record AdminSettingDto(
    string Key,
    SettingValueType ValueType,
    string? Value,
    bool IsSecret,
    bool HasValue,
    string RequiredPermission,
    string? Description,
    DateTime? UpdatedAtUtc)
{
    /// <summary>
    /// The one way to build one of these from a row, so the secret rule is applied in a single place and a
    /// new read path cannot opt out of it by constructing the record itself.
    /// </summary>
    public static AdminSettingDto From(Setting row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AdminSettingDto(
            row.Key,
            row.ValueType,
            row.IsSecret ? null : row.Value,
            row.IsSecret,
            !string.IsNullOrEmpty(row.Value),
            row.RequiredPermission,
            row.Description,
            row.UpdatedAtUtc);
    }
}

/// <summary>
/// The current logo, for the admin screen's preview and its Remove button (GLB-02, ADM-22).
/// </summary>
/// <param name="PublicId">
/// The asset's public token, which is the identifier the media API speaks. The internal
/// <c>MediaAssets.Id</c> that <c>Settings.Value</c> actually stores is never returned.
/// </param>
/// <param name="Url">The image to draw, a rendition where one exists.</param>
/// <param name="Width">The original's pixel width, so the preview can show what was uploaded.</param>
/// <param name="Height">The original's pixel height.</param>
/// <param name="AltText">
/// The alt text recorded with the asset. Carried through so the screen can show whether one is set — a
/// logo with no alt text is a header with an unlabelled image in it (GLB-12).
/// </param>
/// <param name="OriginalFileName">What the client called the file, so they can recognise it.</param>
public sealed record AdminLogoDto(
    Guid PublicId,
    string? Url,
    int? Width,
    int? Height,
    string? AltText,
    string OriginalFileName);
