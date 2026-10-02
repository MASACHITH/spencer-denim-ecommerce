using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Abstractions;

/// <summary>
/// Write port for <c>ops.Settings</c> (ADM-22), in the shape <c>ICatalogStructureStore</c> established:
/// it hands back tracked entities and takes one <see cref="SaveAsync"/> for the whole unit of work, so a
/// group's four rows, their <c>MediaUsages</c> rows and the audit row commit together or not at all.
/// <para>
/// <b>There is no <c>Add</c> and no <c>Delete</c>, and that is the security property.</b> Rows are created
/// by the seeder from <see cref="SettingCatalog"/>; a use case can only update one that is already there.
/// So <see cref="Setting.RequiredPermission"/> — the column that decides who may write the key — is always
/// a value this codebase chose, never one a request supplied.
/// </para>
/// <para>
/// Nothing here validates. Every rule lives in the handlers, where it is unit-testable and where a failure
/// is a named field in a 400. The port's only judgement is the one the database alone can make: a row
/// somebody else changed first.
/// </para>
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// The rows for <paramref name="keys"/>, tracked, in key order. A key with no row is simply absent from
    /// the result — the caller decides whether that is a fault (it is: it means the seeder has not run).
    /// </summary>
    Task<IReadOnlyList<Setting>> ListAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken);

    /// <summary>
    /// A media asset by its public token, but only one a setting may point at: an <b>image</b>, <b>public</b>,
    /// <b>Ready</b> and not soft-deleted. Anything else comes back <see langword="null"/>, so the handler
    /// answers with a named field error instead of storing a reference that renders as a broken image.
    /// </summary>
    Task<SettingImageAsset?> FindUsableImageAsync(Guid publicId, CancellationToken cancellationToken);

    /// <summary>
    /// The same projection by internal id — what a <see cref="SettingValueType.MediaAssetRef"/> actually
    /// stores — for resolving a logo that is already set. <see langword="null"/> when the row is gone, soft
    /// deleted, or no longer usable, which is why both read models treat a dangling reference as "no logo"
    /// rather than as an error.
    /// </summary>
    Task<SettingImageAsset?> FindUsableImageByIdAsync(int assetId, CancellationToken cancellationToken);

    /// <summary>
    /// Points <c>media.MediaUsages</c> at <paramref name="newAssetId"/> for one setting key, releasing
    /// whatever it pointed at before — enlisted in this unit of work, not committed.
    /// <para>
    /// <b>This is the half of a logo change that is easy to forget and expensive to get wrong.</b> Doc 08
    /// §3.3: "an owner's save rewrites its usage rows in the same transaction". If the old row is left
    /// behind, the media library goes on refusing to delete the previous logo for ever (409, with a "used
    /// in…" entry naming a setting that no longer points at it) — and the owner's stated reason for asking
    /// for this screen was to be able to remove the old logo and add a new one. If the new row is missing,
    /// the opposite and worse: a staff user deletes the live logo and the header shows a broken image.
    /// </para>
    /// <para>
    /// <paramref name="newAssetId"/> <see langword="null"/> releases without claiming, which is Remove.
    /// </para>
    /// </summary>
    Task ReplaceMediaUsageAsync(
        string settingKey,
        string usageRole,
        int? newAssetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits everything the handler changed, stamping <paramref name="actingUserId"/> into
    /// <c>UpdatedByUserId</c>.
    /// </summary>
    Task<SettingsSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken);
}

/// <summary>
/// A media asset a setting is allowed to point at, projected for both read models.
/// </summary>
/// <param name="Id">
/// <c>media.MediaAssets.Id</c> — the value a <see cref="SettingValueType.MediaAssetRef"/> stores and the
/// value a <c>MediaUsages</c> row needs. Never returned to a client.
/// </param>
/// <param name="PublicId">The token clients use.</param>
/// <param name="Url">
/// A public URL for the image, built from <c>Catalog:MediaBaseUrl</c> by the adapter — so no handler and no
/// endpoint has to know the storage-key convention.
/// </param>
/// <param name="Width">The original's pixel width, if it was recorded.</param>
/// <param name="Height">The original's pixel height, if it was recorded.</param>
/// <param name="AltText">The alt text a staff user gave the asset, if any.</param>
/// <param name="OriginalFileName">What the uploaded file was called.</param>
public sealed record SettingImageAsset(
    int Id,
    Guid PublicId,
    string? Url,
    int? Width,
    int? Height,
    string? AltText,
    string OriginalFileName);

/// <summary>What the database made of a settings write.</summary>
public enum SettingsSaveOutcome
{
    /// <summary>Committed.</summary>
    Saved,

    /// <summary>
    /// Nothing was different, so no statement was issued: no row version moves and no audit row is
    /// written. A save of an untouched form is not an event.
    /// </summary>
    NothingToSave,

    /// <summary>
    /// Somebody else changed one of these rows between the read and the save (<c>RowVersion</c>, doc 08
    /// §1.5 <c>[RV]</c>). Nothing was written; the caller reloads and re-applies.
    /// </summary>
    ConcurrencyConflict,
}
