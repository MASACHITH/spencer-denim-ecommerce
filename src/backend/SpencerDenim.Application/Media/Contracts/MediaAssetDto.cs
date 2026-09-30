using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Media.Contracts;

/// <summary>
/// One row of <c>media.MediaAssets</c> as the admin API returns it (ADM-18).
/// <para>
/// <see cref="Url"/> and <see cref="RenditionUrls"/> are built from <c>Catalog:MediaBaseUrl</c> the same
/// way the storefront's catalogue payloads are, so a client never has to know the key convention. A
/// private asset carries no URL at all: nothing on the private volume is reachable through
/// <c>/media</c> (doc 08 §3.3).
/// </para>
/// </summary>
/// <param name="Checksum">The SHA-256 of the uploaded bytes, lowercase hex.</param>
/// <param name="RenditionUrls">Rendition code to URL, for an image that has renditions; empty otherwise.</param>
public sealed record MediaAssetDto(
    Guid PublicId,
    MediaAssetType MediaType,
    MediaVisibility Visibility,
    MediaAssetStatus Status,
    string OriginalFileName,
    string StorageKey,
    string? Url,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    decimal? DurationSeconds,
    Guid? PosterPublicId,
    string? PosterUrl,
    byte? RenditionSetVersion,
    string Checksum,
    string? AltText,
    string? Title,
    IReadOnlyDictionary<string, string> RenditionUrls,
    DateTime CreatedAtUtc);

/// <summary>
/// One row of <c>media.MediaUsages</c>: what an asset is used by. Doc 08 §3.3 — this is what the "used
/// in…" panel shows and what the delete guard counts.
/// </summary>
/// <param name="OwnerType">
/// <c>Product</c>, <c>Category</c>, <c>Department</c>, <c>MediaAsset</c> (a video's poster), … .
/// </param>
/// <param name="OwnerId">The owning entity's ID, or the setting key.</param>
/// <param name="UsageRole">Which slot it fills: <c>Card</c>, <c>Gallery</c>, <c>Hero</c>, <c>Poster</c>, … .</param>
/// <param name="OwnerLabel">
/// A name a person recognises ("Wide-Leg Rinse Jeans"), resolved per owner type where this milestone has
/// a table to read it from. Null when the owner type has no name to show, and the client then falls back
/// to the type and ID.
/// </param>
public sealed record MediaUsageDto(
    string OwnerType,
    string OwnerId,
    string UsageRole,
    string? OwnerLabel,
    DateTime CreatedAtUtc);

/// <summary>An asset with its usage list, for the library's detail panel.</summary>
public sealed record MediaAssetDetailDto(MediaAssetDto Asset, IReadOnlyList<MediaUsageDto> Usages);
