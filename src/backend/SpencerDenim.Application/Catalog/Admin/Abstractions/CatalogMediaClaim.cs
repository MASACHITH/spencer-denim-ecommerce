using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Catalog.Admin.Abstractions;

/// <summary>
/// One library asset a catalogue save is about to point at: which asset, in which role (a
/// <see cref="MediaUsageRoles"/> value), and the request field that carried it, so a refusal can name it.
/// </summary>
public sealed record CatalogMediaSlot(int MediaAssetId, string UsageRole, string Field)
{
    /// <summary>A product's video slot holds a video; every other catalogue slot holds an image.</summary>
    public MediaAssetType ExpectedType =>
        string.Equals(UsageRole, MediaUsageRoles.Video, StringComparison.Ordinal)
            ? MediaAssetType.Video
            : MediaAssetType.Image;
}

/// <summary>
/// The pictures a product, category or department save will point at, handed to the store so it can
/// lock and re-check them <b>inside</b> the save's transaction (#223, doc 08 §5.3).
/// <para>
/// The handler's own media check is an RCSI read, so a media-library delete can commit between that check
/// and the save, leaving the owner on a soft-deleted picture. <c>MediaService.DeleteAsync</c> takes
/// <c>UPDLOCK, READCOMMITTEDLOCK, ROWLOCK</c> on the asset row before its own checks; the store takes the
/// same lock on every slot's asset (ascending id) before it writes anything, and re-checks each one: not
/// deleted, ready, public and of the slot's kind. Exactly one of the two wins. A save that locks first
/// commits its usage rows and the delete then refuses (409); a delete that locks first commits, the
/// re-check finds the asset deleted, nothing is written, and the store answers
/// <see cref="CatalogSaveOutcome.MediaUnavailable"/> with the failing slots in <see cref="Unavailable"/>.
/// </para>
/// </summary>
public sealed class CatalogMediaClaim
{
    public CatalogMediaClaim(IEnumerable<CatalogMediaSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        Slots = slots.ToList();
    }

    /// <summary>Every asset the saved row will point at, possibly with one asset in several slots.</summary>
    public IReadOnlyList<CatalogMediaSlot> Slots { get; }

    /// <summary>
    /// A <c>catalog.Departments</c> row to lock (<c>UPDLOCK</c>) <b>before</b> the assets: doc 08 §5.3's
    /// "department row, then asset rows". Set by the department save, which writes that row and so must
    /// take it in the same order a mega-menu card's save does, or the two could deadlock.
    /// </summary>
    public int? LockDepartmentId { get; init; }

    /// <summary>
    /// When set (a <see cref="MediaUsageOwnerTypes"/> value), the store also rewrites that owner's
    /// <c>media.MediaUsages</c> rows to exactly <see cref="Slots"/>, in the same transaction — which is what makes
    /// the media library refuse to delete the picture. A category and a department use it; a product writes its
    /// usage rows through <c>IProductWriteStore.SyncProductMediaUsagesAsync</c> and leaves it unset.
    /// </summary>
    public string? UsageOwnerType { get; init; }

    /// <summary>Set by the store when the save answers <see cref="CatalogSaveOutcome.MediaUnavailable"/>.</summary>
    public IReadOnlyList<CatalogMediaSlot> Unavailable { get; set; } = [];
}
