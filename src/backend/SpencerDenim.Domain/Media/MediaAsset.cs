using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Media;

/// <summary>
/// <c>media.MediaAssets</c> (doc 08 §3.3) [A] [SD]. The database stores metadata and a relative
/// storage key only; public URLs are built from configuration.
/// </summary>
public sealed class MediaAsset : IAuditable, ISoftDeletable
{
    private readonly SoftDeleteState _softDelete = new();

    public int Id { get; set; }

    /// <summary>Unguessable public token. Also the folder name in the rendition key convention.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public MediaAssetType MediaType { get; set; } = MediaAssetType.Image;

    public MediaVisibility Visibility { get; set; } = MediaVisibility.Public;

    public MediaAssetStatus Status { get; set; } = MediaAssetStatus.Ready;

    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>Relative key, e.g. <c>public/originals/…</c> or <c>private/…</c>.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public decimal? DurationSeconds { get; set; }

    /// <summary>Poster image for a video. Required when <see cref="MediaType"/> is Video (CK).</summary>
    public int? PosterAssetId { get; set; }

    public MediaAsset? PosterAsset { get; set; }

    /// <summary>Which rendition set was generated. Images only.</summary>
    public byte? RenditionSetVersion { get; set; }

    /// <summary>SHA-256 of the original file.</summary>
    public byte[] Checksum { get; set; } = [];

    public string? AltText { get; set; }

    public string? Title { get; set; }

    /// <summary>NULL for guest bank-slip uploads.</summary>
    public int? UploadedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public bool IsDeleted => _softDelete.IsDeleted;

    public DateTime? DeletedAtUtc => _softDelete.DeletedAtUtc;

    public int? DeletedByUserId => _softDelete.DeletedByUserId;

    public void SoftDelete(DateTime utcNow, int? userId) => _softDelete.Delete(utcNow, userId);

    public void Restore() => _softDelete.Restore();
}
