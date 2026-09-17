using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.SizeGuides</c> (doc 08 §3.2) [A] [SD] [RV]: the PDP-04 modal, managed per category
/// with a per-product override.
/// </summary>
public sealed class SizeGuide : IAuditable, ISoftDeletable
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? IntroHtml { get; set; }

    /// <summary>Measurement table. <c>nvarchar(max)</c> with an <c>ISJSON</c> check (doc 08 §1.4, DB15).</summary>
    public string TableJson { get; set; } = "{}";

    public string? MeasuringTipsHtml { get; set; }

    public int? ImageAssetId { get; set; }

    public MediaAsset? ImageAsset { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    /// <summary>Doc 08 §1.5 [SD]. Set only by <see cref="SoftDelete"/>; a filtered unique index and the
    /// global query filter both rely on it.</summary>
    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public int? DeletedByUserId { get; private set; }

    public void SoftDelete(DateTime utcNow, int? userId)
    {
        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        DeletedAtUtc = utcNow;
        DeletedByUserId = userId;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        DeletedByUserId = null;
    }
}
