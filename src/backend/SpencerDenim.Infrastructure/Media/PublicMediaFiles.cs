using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// Resolves a key taken off a <c>/media</c> URL to a file that may be served (ADM-18, doc 08 §3.3).
/// <para>
/// <b>Everything is a 404.</b> A malformed key, a key on the private volume, an unknown asset, a
/// soft-deleted one, one still <c>Processing</c> or <c>Failed</c>, and a row whose file is not on disk all
/// answer exactly the same way. That is deliberate: a 403 on a private key would confirm the key exists,
/// and the difference between "no such asset" and "that asset is not published yet" is information about
/// the client's catalogue that an anonymous request has no business learning.
/// </para>
/// <para>
/// <b>Deliberately not cached.</b> Each request costs one lookup on a unique index, and the alternative —
/// holding descriptors in memory — would keep serving a file for the length of the cache after somebody
/// deleted it or unpublished it, which is the one thing this class exists to prevent. The browser cache
/// does the heavy lifting instead: every key contains the asset's <c>PublicId</c>, so a key's content
/// never changes and the response carries a year's <c>max-age</c> and a strong ETag.
/// </para>
/// </summary>
public sealed class PublicMediaFiles(AppDbContext dbContext, IFileStorage storage) : IPublicMediaFiles
{
    private const string RenditionsPrefix = "public/renditions/";

    /// <summary>
    /// Media types a file may be served as. The stored <c>ContentType</c> was written from a verified
    /// signature, but it is a free-text column, and a row that said <c>text/html</c> would turn the media
    /// volume into a place to host a script. Anything not on this list is not served.
    /// </summary>
    private static readonly HashSet<string> ServableContentTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "video/mp4",
    };

    public async Task<MediaFileDescriptor?> ResolveAsync(string? storageKey, CancellationToken cancellationToken)
    {
        if (!MediaStorageKey.Validate(storageKey, out var container, out _)
            || container != MediaStorageContainer.Public)
        {
            return null;
        }

        var key = storageKey!;

        MediaAsset? asset;
        string? contentType;
        string eTagSuffix;

        if (TryParseRendition(key, out var publicId, out var code))
        {
            asset = await FindByPublicIdAsync(publicId, cancellationToken);
            contentType = MediaRenditions.ContentType;
            eTagSuffix = code;
        }
        else
        {
            asset = await FindByStorageKeyAsync(key, cancellationToken);
            contentType = null;
            eTagSuffix = "o";
        }

        if (asset is null
            || asset.Status != MediaAssetStatus.Ready
            || asset.Visibility != MediaVisibility.Public)
        {
            return null;
        }

        if (contentType is not null)
        {
            // A rendition only exists for an image that has been rendered.
            if (asset.MediaType != MediaAssetType.Image || asset.RenditionSetVersion is null)
            {
                return null;
            }
        }
        else
        {
            if (!ServableContentTypes.Contains(asset.ContentType))
            {
                return null;
            }

            contentType = asset.ContentType;
        }

        var info = await storage.GetInfoAsync(key, cancellationToken);
        if (info is null)
        {
            return null;
        }

        return new MediaFileDescriptor(key, contentType, BuildETag(asset.Checksum, eTagSuffix), info.SizeBytes, info.LastModifiedUtc);
    }

    /// <summary>
    /// A strong ETag from the asset's SHA-256 and which file of it this is.
    /// <para>
    /// Strong rather than weak, because a strong validator is the only kind a conditional <c>Range</c>
    /// request (<c>If-Range</c>) may use, and seeking a video is exactly that. It is derived from content
    /// rather than from a timestamp, so it survives a restore, a redeploy and a second server, none of
    /// which preserve a file's modification time.
    /// </para>
    /// </summary>
    private static string BuildETag(byte[] checksum, string suffix) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"\"{Convert.ToHexString(checksum).ToLowerInvariant()}-{suffix}\"");

    private Task<MediaAsset?> FindByStorageKeyAsync(string key, CancellationToken cancellationToken) =>
        dbContext.MediaAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.StorageKey == key, cancellationToken);

    private Task<MediaAsset?> FindByPublicIdAsync(Guid publicId, CancellationToken cancellationToken) =>
        dbContext.MediaAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.PublicId == publicId, cancellationToken);

    /// <summary>
    /// Splits <c>public/renditions/{PublicId}/{code}.webp</c>. A rendition has no row of its own (doc 08
    /// §3.3, simplification 3), so the key convention is the only index there is.
    /// </summary>
    private static bool TryParseRendition(string key, out Guid publicId, out string code)
    {
        publicId = Guid.Empty;
        code = string.Empty;

        if (!key.StartsWith(RenditionsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = key[RenditionsPrefix.Length..];
        var slash = remainder.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return false;
        }

        if (!Guid.TryParseExact(remainder[..slash], "D", out publicId))
        {
            return false;
        }

        var file = remainder[(slash + 1)..];
        if (!file.EndsWith(".webp", StringComparison.Ordinal))
        {
            return false;
        }

        code = file[..^".webp".Length];
        return MediaRenditions.IsKnownCode(code);
    }
}
