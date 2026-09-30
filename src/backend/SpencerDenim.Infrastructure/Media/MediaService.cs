using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Media;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// The media pipeline of doc 03 §3b and ADM-18: detect → validate → hash → store → render → publish.
/// <para>
/// <b>Nothing about the upload is trusted.</b> The client's content type and the filename's extension are
/// recorded and otherwise ignored; the type comes from the file signature
/// (<see cref="MediaFileSignature"/>). The storage key is built from a GUID this server generated, so no
/// part of a caller's input ever reaches a path. The pixel count is checked from the image header before
/// anything is decoded, which is what stops a 200 KB PNG that declares 40 000 × 40 000 pixels. The
/// original has its metadata stripped and every rendition is re-encoded from decoded pixels, so a payload
/// hidden in an EXIF comment or appended past the end of the image data does not survive the round trip.
/// </para>
/// <para>
/// <b>Failure leaves nothing behind.</b> The row is inserted as <c>Processing</c>; it only becomes
/// <c>Ready</c> once every file is on disk. If any stage throws, the files written so far are deleted and
/// the row is marked <c>Failed</c>, so there is never a file without a row, and never a row claiming
/// <c>Ready</c> over a file that is not there.
/// </para>
/// <para>
/// <b>Two uploads of the same bytes share one file.</b> <c>MediaAssets.Checksum</c> is indexed for this:
/// when the SHA-256 of the upload matches a live, <c>Ready</c> asset of the same visibility and type, the
/// existing asset is returned and nothing is written. It is the right default here — the library's whole
/// point is that one photograph is used by several products (<c>MediaUsages</c> models exactly that), the
/// VPS has one disk, and a fashion catalogue's re-uploads are usually the same export of the same shot.
/// The narrowness matters as much as the rule: the match must be <b>the same visibility</b>, so a
/// customer's bank slip can never be answered by a public file and a public upload can never be answered
/// by a private one; it must be <c>Ready</c>, so a half-finished upload is never handed out; and it must
/// not be soft-deleted, so an asset somebody deliberately removed does not come back. The caller is told
/// which happened — 200 with the existing asset, not 201 — so the admin screen can say "this image is
/// already in the library" rather than silently pointing at somebody else's alt text.
/// </para>
/// </summary>
public sealed class MediaService(
    AppDbContext dbContext,
    IFileStorage storage,
    SkiaImageProcessor imageProcessor,
    IOptions<MediaOptions> mediaOptions,
    IOptions<CatalogOptions> catalogOptions,
    ILogger<MediaService> logger) : IMediaService
{
    /// <summary>SQL Server: a unique index refused the row.</summary>
    private const int DuplicateUniqueIndexError = 2601;

    /// <summary>SQL Server: a unique constraint refused the row.</summary>
    private const int DuplicateKeyError = 2627;

    /// <summary>The index that enforces doc 08 §3.3's deduplication guarantee.</summary>
    private const string ChecksumIndexName = "UX_MediaAssets_Checksum_Ready";

    private readonly MediaOptions _media = mediaOptions.Value;
    private readonly CatalogOptions _catalog = catalogOptions.Value;

    public async Task<MediaUploadOutcome> UploadImageAsync(
        UploadImageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await StoreImageAsync(request, cancellationToken);
        return result.Outcome;
    }

    public async Task<MediaUploadOutcome> UploadVideoAsync(
        UploadVideoRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!storage.IsConfigured)
        {
            return StorageNotConfigured();
        }

        // Doc 08 §3.3: CK_MediaAssets_VideoHasPoster. The row cannot exist without a poster, so the
        // poster is stored first and rolled back if the video is then refused — otherwise a rejected
        // upload would leave an orphan image in the library.
        var posterRequest = new UploadImageRequest(
            request.Poster,
            MediaVisibility.Public,
            request.AltText is null ? null : $"Poster image: {request.AltText}",
            request.Title,
            request.UploadedByUserId);

        var poster = await StoreImageAsync(posterRequest, cancellationToken);

        if (poster.Outcome is MediaUploadOutcome.Rejected rejectedPoster)
        {
            return new MediaUploadOutcome.Rejected(
                rejectedPoster.Reason == MediaUploadRejection.UnsupportedType
                    ? MediaUploadRejection.UnsupportedType
                    : rejectedPoster.Reason,
                "The poster image was refused: " + rejectedPoster.Message);
        }

        var posterAssetId = poster.AssetId
            ?? throw new InvalidOperationException("A successful poster upload has no asset id.");

        VideoStoreResult video;
        try
        {
            video = await StoreVideoAsync(request, posterAssetId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RollBackPosterAsync(poster, CancellationToken.None);
            throw;
        }

        if (video.RollBackPoster)
        {
            await RollBackPosterAsync(poster, cancellationToken);
        }

        return video.Outcome;
    }

    public async Task<MediaAssetDetailDto?> GetAsync(Guid publicId, CancellationToken cancellationToken)
    {
        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .Include(candidate => candidate.PosterAsset)
            .FirstOrDefaultAsync(candidate => candidate.PublicId == publicId, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        var usages = await LoadUsagesAsync(asset.Id, cancellationToken);
        return new MediaAssetDetailDto(ToDto(asset), usages);
    }

    public async Task<MediaDeleteOutcome> DeleteAsync(
        Guid publicId,
        int? deletedByUserId,
        CancellationToken cancellationToken)
    {
        var asset = await dbContext.MediaAssets
            .FirstOrDefaultAsync(candidate => candidate.PublicId == publicId, cancellationToken);

        if (asset is null)
        {
            return new MediaDeleteOutcome.NotFound();
        }

        var usages = await LoadUsagesAsync(asset.Id, cancellationToken);
        if (usages.Count > 0)
        {
            return new MediaDeleteOutcome.InUse(usages);
        }

        // MediaUsages is meant to mirror every structured reference (doc 08 §3.3), but the poster link is
        // a real foreign key on MediaAssets itself, and a NO ACTION foreign key would still refuse the
        // eventual purge. Checking it directly means the guard does not depend on a usage row having been
        // written correctly by whoever created the video.
        var usedAsPoster = await dbContext.MediaAssets
            .AnyAsync(candidate => candidate.PosterAssetId == asset.Id, cancellationToken);

        if (usedAsPoster)
        {
            var videos = await dbContext.MediaAssets
                .AsNoTracking()
                .Where(candidate => candidate.PosterAssetId == asset.Id)
                .Select(candidate => new { candidate.Id, candidate.OriginalFileName, candidate.CreatedAtUtc })
                .ToListAsync(cancellationToken);

            return new MediaDeleteOutcome.InUse(videos
                .Select(video => new MediaUsageDto(
                    MediaUsageOwnerTypes.MediaAsset,
                    video.Id.ToString(CultureInfo.InvariantCulture),
                    MediaUsageRoles.Poster,
                    video.OriginalFileName,
                    video.CreatedAtUtc))
                .ToList());
        }

        // Doc 08 §3.3: "An owner's save rewrites its usage rows in the same transaction." A soft delete
        // is such a save, so the rows this asset *owns* — what it uses, today only a video's poster —
        // go with it, in the same SaveChangesAsync below.
        //
        // Leaving them behind bricks the poster permanently: LoadUsagesAsync would keep finding a row
        // pointing at a video the soft-delete filter hides, so the poster could never be deleted (409
        // for ever) and the "used in…" panel would list it with no label at all, because the label
        // lookup reads through that same filter. The rows recording what *other* owners use of this
        // asset are deliberately untouched — those belong to those owners, and the guard above has
        // already refused the delete if any exist.
        var ownerId = asset.Id.ToString(CultureInfo.InvariantCulture);
        var owned = await dbContext.MediaUsages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.MediaAsset && usage.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        dbContext.MediaUsages.RemoveRange(owned);

        // Doc 08 §3.3: soft delete now, files purged after 30 days (MediaRetention).
        asset.SoftDelete(DateTime.UtcNow, deletedByUserId);
        await dbContext.SaveChangesAsync(cancellationToken);

        MediaLog.AssetSoftDeleted(logger, publicId, MediaRetention.PurgeAfterDays);

        return new MediaDeleteOutcome.Deleted();
    }

    // ------------------------------------------------------------------ images

    private sealed record StoreResult(MediaUploadOutcome Outcome, int? AssetId, bool WasExisting);

    /// <summary>
    /// A video outcome, plus whether the poster stored a moment ago has to be taken back out again —
    /// which it does for every refusal, because a poster with no video is an orphan nobody asked for.
    /// </summary>
    private sealed record VideoStoreResult(MediaUploadOutcome Outcome, bool RollBackPoster);

    private async Task<StoreResult> StoreImageAsync(
        UploadImageRequest request,
        CancellationToken cancellationToken)
    {
        if (!storage.IsConfigured)
        {
            return new StoreResult(StorageNotConfigured(), null, false);
        }

        var maxBytes = _media.MaxBytesFor(request.Visibility, MediaAssetType.Image);
        var length = request.File.Content.Length;

        if (length <= 0)
        {
            return Reject(MediaUploadRejection.Empty, "The uploaded file is empty.");
        }

        if (length > maxBytes)
        {
            return Reject(
                MediaUploadRejection.TooLarge,
                $"The image is {Megabytes(length)} MB. The limit is {Megabytes(maxBytes)} MB.");
        }

        var source = await ReadAllAsync(request.File.Content, cancellationToken);
        var kind = MediaFileSignature.Detect(source);

        if (!MediaFileSignature.IsImage(kind))
        {
            return Reject(
                MediaUploadRejection.UnsupportedType,
                $"The file is {MediaFileSignature.Describe(kind)}. The library takes JPEG, PNG and WebP images. " +
                "The check is on the file's contents, not its name, so renaming it will not help.");
        }

        var checksum = SHA256.HashData(source);

        if (_media.DeduplicateByChecksum
            && await FindDuplicateAsync(checksum, MediaAssetType.Image, request.Visibility, cancellationToken)
                is { } existing)
        {
            return new StoreResult(new MediaUploadOutcome.Duplicate(ToDto(existing)), existing.Id, WasExisting: true);
        }

        var header = imageProcessor.ReadHeader(source);
        if (header is null)
        {
            return Reject(
                MediaUploadRejection.Unprocessable,
                $"The file starts like {MediaFileSignature.Describe(kind)} but could not be opened as one, " +
                $"or it declares more than {_media.MaxImageMegapixels} megapixels.");
        }

        using var upright = imageProcessor.DecodeUpright(source);
        if (upright is null)
        {
            return Reject(
                MediaUploadRejection.Unprocessable,
                $"The file starts like {MediaFileSignature.Describe(kind)} but its image data could not be decoded.");
        }

        byte[] originalBytes;
        if (header.HasNonDefaultOrientation)
        {
            // The EXIF orientation tag is about to be deleted along with the rest of the EXIF, so the
            // rotation has to be baked into the pixels; that cannot be done without re-encoding.
            originalBytes = imageProcessor.ReencodeOriginal(upright, kind);
        }
        else if (!ImageMetadataStripper.TryStrip(source, kind, out originalBytes))
        {
            return Reject(
                MediaUploadRejection.Unprocessable,
                $"The file starts like {MediaFileSignature.Describe(kind)} but its container is malformed.");
        }

        var extension = MediaFileSignature.ExtensionFor(kind)!;
        var contentType = MediaFileSignature.ContentTypeFor(kind)!;

        var asset = new MediaAsset
        {
            PublicId = Guid.NewGuid(),
            MediaType = MediaAssetType.Image,
            Visibility = request.Visibility,
            Status = MediaAssetStatus.Processing,
            OriginalFileName = SanitiseFileName(request.File.FileName, extension),
            ContentType = contentType,
            SizeBytes = originalBytes.LongLength,
            Width = upright.Width,
            Height = upright.Height,
            Checksum = checksum,
            AltText = Trim(request.AltText, 250),
            Title = Trim(request.Title, 150),
            UploadedByUserId = request.UploadedByUserId,
        };

        asset.StorageKey = MediaStorageKey.ForOriginal(request.Visibility, asset.PublicId, extension);

        dbContext.MediaAssets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);

        var written = new List<string>();

        try
        {
            await WriteAsync(asset.StorageKey, originalBytes, cancellationToken);
            written.Add(asset.StorageKey);

            // Renditions are public delivery artefacts. A private asset (a bank slip) gets none: it is
            // never displayed in a gallery, and putting a copy of it on the public volume is precisely
            // what doc 08 §3.3 forbids.
            if (request.Visibility == MediaVisibility.Public)
            {
                foreach (var rendition in imageProcessor.EncodeRenditions(upright))
                {
                    var key = MediaStorageKey.ForRendition(asset.PublicId, rendition.Rendition.Code);
                    await WriteAsync(key, rendition.Bytes, cancellationToken);
                    written.Add(key);
                }

                asset.RenditionSetVersion = MediaRenditions.CurrentSetVersion;
            }

            if (await PublishAsync(asset, written, cancellationToken) is { } winner)
            {
                return new StoreResult(new MediaUploadOutcome.Duplicate(ToDto(winner)), winner.Id, WasExisting: true);
            }

            return new StoreResult(new MediaUploadOutcome.Created(ToDto(asset)), asset.Id, WasExisting: false);
        }
        catch (Exception exception)
        {
            await FailAsync(asset, written, exception, cancellationToken);
            throw;
        }
    }

    // ------------------------------------------------------------------- video

    private async Task<VideoStoreResult> StoreVideoAsync(
        UploadVideoRequest request,
        int posterAssetId,
        CancellationToken cancellationToken)
    {
        var maxBytes = _media.MaxBytesFor(MediaVisibility.Public, MediaAssetType.Video);
        var content = request.File.Content;
        var length = content.Length;

        if (length <= 0)
        {
            return Refuse(MediaUploadRejection.Empty, "The uploaded video is empty.");
        }

        if (length > maxBytes)
        {
            return Refuse(
                MediaUploadRejection.TooLarge,
                $"The video is {Megabytes(length)} MB. The limit is {Megabytes(maxBytes)} MB " +
                "(doc 03 §3b: no server-side conversion, so the file has to be small enough to stream as it is).");
        }

        Span<byte> header = stackalloc byte[MediaFileSignature.RequiredBytes];
        content.Position = 0;
        var read = content.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var kind = MediaFileSignature.Detect(header[..read]);

        if (kind != MediaFileKind.Mp4)
        {
            return Refuse(
                MediaUploadRejection.UnsupportedType,
                $"The file is {MediaFileSignature.Describe(kind)}. Product video has to be MP4 (H.264) — " +
                "doc 03 §3b rules out converting it on the server, so it has to arrive in the format a browser plays.");
        }

        content.Position = 0;
        var video = Mp4Inspector.TryInspect(content);

        if (video is null)
        {
            return Refuse(
                MediaUploadRejection.Unprocessable,
                "The MP4 has no readable video track. It may be audio only, or the file may be truncated.");
        }

        if (!video.IsH264)
        {
            return Refuse(
                MediaUploadRejection.Unprocessable,
                $"The MP4's video track is '{video.Codec}', not H.264 (avc1). Safari and older Android will not " +
                "play it, and nothing on this server converts it. Export it as H.264.");
        }

        content.Position = 0;
        var checksum = await SHA256.HashDataAsync(content, cancellationToken);

        if (_media.DeduplicateByChecksum
            && await FindDuplicateAsync(checksum, MediaAssetType.Video, MediaVisibility.Public, cancellationToken)
                is { } existing)
        {
            return new VideoStoreResult(new MediaUploadOutcome.Duplicate(ToDto(existing)), RollBackPoster: true);
        }

        var asset = new MediaAsset
        {
            PublicId = Guid.NewGuid(),
            MediaType = MediaAssetType.Video,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Processing,
            OriginalFileName = SanitiseFileName(request.File.FileName, ".mp4"),
            ContentType = "video/mp4",
            SizeBytes = length,
            Width = video.Width,
            Height = video.Height,
            DurationSeconds = video.DurationSeconds,
            PosterAssetId = posterAssetId,
            Checksum = checksum,
            AltText = Trim(request.AltText, 250),
            Title = Trim(request.Title, 150),
            UploadedByUserId = request.UploadedByUserId,
        };

        asset.StorageKey = MediaStorageKey.ForOriginal(MediaVisibility.Public, asset.PublicId, ".mp4");

        dbContext.MediaAssets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);

        var written = new List<string>();

        try
        {
            content.Position = 0;
            await storage.WriteAsync(asset.StorageKey, content, cancellationToken);
            written.Add(asset.StorageKey);

            // Doc 08 §3.3: "An owner's save rewrites its usage rows in the same transaction." The video is
            // the poster's owner, so the delete guard can see it.
            dbContext.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = posterAssetId,
                OwnerType = MediaUsageOwnerTypes.MediaAsset,
                OwnerId = asset.Id.ToString(CultureInfo.InvariantCulture),
                UsageRole = MediaUsageRoles.Poster,
            });

            if (await PublishAsync(asset, written, cancellationToken) is { } winner)
            {
                // The poster was created for this upload a moment ago and the winner has its own, so it
                // is rolled back exactly as it is when the checksum lookup answers before any of this.
                return new VideoStoreResult(new MediaUploadOutcome.Duplicate(ToDto(winner)), RollBackPoster: true);
            }

            var stored = await dbContext.MediaAssets
                .AsNoTracking()
                .Include(candidate => candidate.PosterAsset)
                .FirstAsync(candidate => candidate.Id == asset.Id, cancellationToken);

            return new VideoStoreResult(new MediaUploadOutcome.Created(ToDto(stored)), RollBackPoster: false);
        }
        catch (Exception exception)
        {
            await FailAsync(asset, written, exception, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Removes a poster that was created for a video upload that then failed. Only a poster this call
    /// created is removed: when the upload was answered from the checksum index, the asset belongs to
    /// somebody else and deleting it would take their image away.
    /// </summary>
    private async Task RollBackPosterAsync(StoreResult poster, CancellationToken cancellationToken)
    {
        if (poster.WasExisting || poster.AssetId is not { } assetId)
        {
            return;
        }

        try
        {
            var asset = await dbContext.MediaAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == assetId, cancellationToken);

            if (asset is null)
            {
                return;
            }

            await storage.DeleteAsync(asset.StorageKey, cancellationToken);
            await storage.DeletePrefixAsync(MediaStorageKey.RenditionFolderFor(asset.PublicId), cancellationToken);

            // A hard delete, not a soft one: this row was created moments ago by an operation that has
            // been refused, so there is nothing to keep. Doc 08 N12 wants hard deletes to be set based —
            // and ExecuteDelete is also the only way past the interceptor's soft-delete guard (DB5).
            await dbContext.MediaAssets
                .Where(candidate => candidate.Id == assetId)
                .ExecuteDeleteAsync(cancellationToken);

            dbContext.ChangeTracker.Clear();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The video upload has already failed; the caller must hear about that, not about this.
            MediaLog.PosterRollbackFailed(logger, exception, assetId);
        }
    }

    // ----------------------------------------------------------------- helpers

    private async Task<MediaAsset?> FindDuplicateAsync(
        byte[] checksum,
        MediaAssetType mediaType,
        MediaVisibility visibility,
        CancellationToken cancellationToken) =>
        await dbContext.MediaAssets
            .AsNoTracking()
            .Include(candidate => candidate.PosterAsset)
            .Where(candidate => candidate.Checksum == checksum
                && candidate.MediaType == mediaType
                && candidate.Visibility == visibility
                && candidate.Status == MediaAssetStatus.Ready)
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyList<MediaUsageDto>> LoadUsagesAsync(int assetId, CancellationToken cancellationToken)
    {
        var usages = await dbContext.MediaUsages
            .AsNoTracking()
            .Where(usage => usage.MediaAssetId == assetId)
            .OrderBy(usage => usage.OwnerType)
            .ThenBy(usage => usage.OwnerId)
            .ThenBy(usage => usage.UsageRole)
            .Select(usage => new { usage.OwnerType, usage.OwnerId, usage.UsageRole, usage.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        if (usages.Count == 0)
        {
            return [];
        }

        var labels = await LoadOwnerLabelsAsync(usages.Select(usage => (usage.OwnerType, usage.OwnerId)), cancellationToken);

        return usages
            .Select(usage => new MediaUsageDto(
                usage.OwnerType,
                usage.OwnerId,
                usage.UsageRole,
                labels.GetValueOrDefault((usage.OwnerType, usage.OwnerId)),
                usage.CreatedAtUtc))
            .ToList();
    }

    /// <summary>
    /// Resolves a name a person recognises for each owner, so the "used in…" panel reads "Wide-Leg Rinse
    /// Jeans" rather than "Product 41". One query per owner type that is actually present, not one per
    /// row. Owner types whose tables belong to a later milestone simply have no label, and the client
    /// falls back to the type and id.
    /// </summary>
    private async Task<Dictionary<(string OwnerType, string OwnerId), string>> LoadOwnerLabelsAsync(
        IEnumerable<(string OwnerType, string OwnerId)> owners,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<(string, string), string>();
        var byType = owners.GroupBy(owner => owner.OwnerType, StringComparer.Ordinal);

        foreach (var group in byType)
        {
            var ids = group
                .Select(owner => int.TryParse(owner.OwnerId, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
                .OfType<int>()
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                continue;
            }

            switch (group.Key)
            {
                case MediaUsageOwnerTypes.Product:
                    await AddLabelsAsync(
                        labels,
                        group.Key,
                        dbContext.Products.AsNoTracking()
                            .Where(product => ids.Contains(product.Id))
                            .Select(product => new LabelRow(product.Id, product.Name)),
                        cancellationToken);
                    break;

                case MediaUsageOwnerTypes.Category:
                    await AddLabelsAsync(
                        labels,
                        group.Key,
                        dbContext.Categories.AsNoTracking()
                            .Where(category => ids.Contains(category.Id))
                            .Select(category => new LabelRow(category.Id, category.Name)),
                        cancellationToken);
                    break;

                case MediaUsageOwnerTypes.Department:
                    await AddLabelsAsync(
                        labels,
                        group.Key,
                        dbContext.Departments.AsNoTracking()
                            .Where(department => ids.Contains(department.Id))
                            .Select(department => new LabelRow(department.Id, department.Name)),
                        cancellationToken);
                    break;

                case MediaUsageOwnerTypes.ProductColour:
                    await AddLabelsAsync(
                        labels,
                        group.Key,
                        dbContext.ProductColours.AsNoTracking()
                            .Where(colour => ids.Contains(colour.Id))
                            .Select(colour => new LabelRow(colour.Id, colour.Product!.Name + " — " + colour.Name)),
                        cancellationToken);
                    break;

                case MediaUsageOwnerTypes.MediaAsset:
                    await AddLabelsAsync(
                        labels,
                        group.Key,
                        dbContext.MediaAssets.AsNoTracking()
                            .Where(other => ids.Contains(other.Id))
                            .Select(other => new LabelRow(other.Id, other.OriginalFileName)),
                        cancellationToken);
                    break;

                default:
                    // SizeGuide, HomeSectionItem, Setting, ContentPage … : their tables arrive with later
                    // milestones, and a missing label is a display fallback, not a failure.
                    break;
            }
        }

        return labels;
    }

    private sealed record LabelRow(int Id, string Name);

    private static async Task AddLabelsAsync(
        Dictionary<(string, string), string> labels,
        string ownerType,
        IQueryable<LabelRow> query,
        CancellationToken cancellationToken)
    {
        foreach (var row in await query.ToListAsync(cancellationToken))
        {
            labels[(ownerType, row.Id.ToString(CultureInfo.InvariantCulture))] = row.Name;
        }
    }

    private async Task WriteAsync(string storageKey, byte[] bytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(bytes, writable: false);
        await storage.WriteAsync(storageKey, buffer, cancellationToken);
    }

    /// <summary>
    /// Undoes a failed upload: deletes whatever reached storage and marks the row <c>Failed</c>, so the
    /// invariant "no row ever claims Ready over a file that is not there" holds even for a disk that
    /// filled up half way through the rendition set.
    /// </summary>
    /// <summary>
    /// The last step of an upload: publish the asset as <c>Ready</c>, or discover that another request
    /// published the identical bytes first and stand down in its favour.
    /// <para>
    /// <c>UX_MediaAssets_Checksum_Ready</c> is what makes doc 08 §3.3's deduplication an actual
    /// guarantee rather than a hope. <see cref="FindDuplicateAsync"/> still runs first, because
    /// answering from the index without writing anything is far cheaper than writing a file set and
    /// taking it back out again — but it is a read, and two requests carrying the same bytes can both
    /// pass it. The database settles that race, and this method turns the resulting error into the
    /// same <c>Duplicate</c> answer the early lookup would have given, so a caller cannot tell which
    /// of the two paths served it.
    /// </para>
    /// <para>
    /// The loser's row is removed outright rather than marked <c>Failed</c>: nothing failed, it simply
    /// lost, and a <c>Failed</c> row would sit in the library as a permanent record of a race the
    /// person who uploaded never saw.
    /// </para>
    /// </summary>
    /// <returns>
    /// The winning asset when this upload lost the race and has to answer <c>Duplicate</c>;
    /// <see langword="null"/> when it published its own.
    /// </returns>
    private async Task<MediaAsset?> PublishAsync(
        MediaAsset asset,
        List<string> written,
        CancellationToken cancellationToken)
    {
        asset.Status = MediaAssetStatus.Ready;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (IsDuplicateChecksum(exception))
        {
            await StandDownAsync(asset, written, cancellationToken);

            var winner = await FindDuplicateAsync(
                asset.Checksum,
                asset.MediaType,
                asset.Visibility,
                cancellationToken);

            if (winner is null)
            {
                // The only way here is for the winner to have been deleted between the unique index
                // refusing this publish and this lookup. Nothing sensible is left to answer with, and
                // pretending otherwise would hand the caller a DTO for a row that is gone.
                throw;
            }

            MediaLog.UploadDeduplicatedByIndex(logger, asset.PublicId, winner.PublicId);
            return winner;
        }
    }

    /// <summary>
    /// Takes a lost race back out: the files this request wrote, the rows it was holding, and the
    /// <c>Processing</c> row it inserted before any of it.
    /// </summary>
    private async Task StandDownAsync(
        MediaAsset asset,
        List<string> written,
        CancellationToken cancellationToken)
    {
        // The failed SaveChanges left its entries pending. A video's poster usage row is one of them,
        // and it must not be retried: it names this asset as the owner, and this asset is about to
        // stop existing.
        foreach (var entry in dbContext.ChangeTracker.Entries<MediaUsage>().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
        }

        foreach (var key in written)
        {
            try
            {
                await storage.DeleteAsync(key, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MediaLog.RollbackFileNotRemoved(logger, exception, key);
            }
        }

        // A hard delete, not SoftDelete: this row was never Ready, so nothing ever linked it and there is
        // nothing for the 30-day purge window to protect. AuditAndSoftDeleteInterceptor refuses Remove()
        // on a soft-deletable entity and names ExecuteDeleteAsync as the deliberate exception, which is
        // exactly what this is — so the row goes in one statement, and the tracked entity is detached
        // afterwards so nothing later tries to update a row that is gone.
        await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .Where(candidate => candidate.Id == asset.Id)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.Entry(asset).State = EntityState.Detached;
    }

    /// <summary>
    /// Whether the update failed because <c>UX_MediaAssets_Checksum_Ready</c> refused it.
    /// <para>
    /// The index is named in the check rather than matching any unique violation, because
    /// <c>UX_MediaAssets_PublicId</c> and <c>UX_MediaAssets_StorageKey</c> are also unique on this
    /// table. A collision on either of those is a generated GUID repeating itself, which is not a
    /// duplicate upload and must not be answered as one.
    /// </para>
    /// </summary>
    private static bool IsDuplicateChecksum(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: DuplicateKeyError or DuplicateUniqueIndexError } sql
        && sql.Message.Contains(ChecksumIndexName, StringComparison.Ordinal);

    private async Task FailAsync(
        MediaAsset asset,
        List<string> written,
        Exception cause,
        CancellationToken cancellationToken)
    {
        MediaLog.UploadFailed(logger, cause, asset.PublicId, written.Count);

        foreach (var key in written)
        {
            try
            {
                await storage.DeleteAsync(key, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MediaLog.RollbackFileNotRemoved(logger, exception, key);
            }
        }

        try
        {
            asset.Status = MediaAssetStatus.Failed;
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MediaLog.CouldNotMarkFailed(logger, exception, asset.PublicId);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        content.Position = 0;

        // The length is already known and already under the configured ceiling, so this allocates exactly
        // once and nothing here can be made to grow without bound.
        var buffer = new byte[content.Length];
        await content.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }

    /// <summary>
    /// Keeps a recognisable name for the library UI without keeping anything dangerous. The filename is
    /// display data only — it never builds a key, a path or a content type — but it is echoed back in
    /// JSON and in a <c>Content-Disposition</c> header, so directory separators, control characters and
    /// quotes come out, and the extension is replaced with the one the signature actually calls for.
    /// </summary>
    internal static string SanitiseFileName(string? fileName, string extension)
    {
        var name = Path.GetFileName(fileName?.Replace('\\', '/') ?? string.Empty);
        var stem = Path.GetFileNameWithoutExtension(name);

        var cleaned = new string(stem
            .Where(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.' or '(' or ')')
            .ToArray())
            .Trim(' ', '.');

        if (cleaned.Length == 0)
        {
            cleaned = "upload";
        }

        if (cleaned.Length > 255 - extension.Length)
        {
            cleaned = cleaned[..(255 - extension.Length)];
        }

        return cleaned + extension;
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture);

    private static StoreResult Reject(MediaUploadRejection reason, string message) =>
        new(new MediaUploadOutcome.Rejected(reason, message), null, false);

    /// <summary>Refuses a video, and takes the poster that was stored for it back out again.</summary>
    private static VideoStoreResult Refuse(MediaUploadRejection reason, string message) =>
        new(new MediaUploadOutcome.Rejected(reason, message), RollBackPoster: true);

    private static MediaUploadOutcome.Rejected StorageNotConfigured() =>
        new(
            MediaUploadRejection.StorageNotConfigured,
            "Media storage is not configured on this server, so nothing can be uploaded. " +
            "Set Media__PublicRoot and Media__PrivateRoot.");

    private MediaAssetDto ToDto(MediaAsset asset)
    {
        var isPublic = asset.Visibility == MediaVisibility.Public;

        var renditions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (isPublic && asset.MediaType == MediaAssetType.Image && asset.RenditionSetVersion is not null)
        {
            // The set this asset was rendered with, trimmed to the files that were actually written:
            // a source no wider than `large` produces no `zoom`, and listing a URL for it here would
            // put a 404 in the admin library's payload.
            var produced = MediaRenditions.ProducedFor(
                MediaRenditions.ForVersion(asset.RenditionSetVersion),
                asset.Width);

            foreach (var (rendition, _) in produced)
            {
                var url = MediaUrlBuilder.ToPublicUrl(
                    _catalog.MediaBaseUrl,
                    MediaStorageKey.ForRendition(asset.PublicId, rendition));

                if (url is not null)
                {
                    renditions[rendition.Code] = url;
                }
            }
        }

        return new MediaAssetDto(
            asset.PublicId,
            asset.MediaType,
            asset.Visibility,
            asset.Status,
            asset.OriginalFileName,
            asset.StorageKey,
            isPublic ? MediaUrlBuilder.ToPublicUrl(_catalog.MediaBaseUrl, asset.StorageKey) : null,
            asset.ContentType,
            asset.SizeBytes,
            asset.Width,
            asset.Height,
            asset.DurationSeconds,
            asset.PosterAsset?.PublicId,
            asset.PosterAsset is not null
                ? MediaUrlBuilder.ToPublicUrl(_catalog.MediaBaseUrl, asset.PosterAsset.StorageKey)
                : null,
            asset.RenditionSetVersion,
            Convert.ToHexString(asset.Checksum).ToLowerInvariant(),
            asset.AltText,
            asset.Title,
            renditions,
            asset.CreatedAtUtc);
    }
}
