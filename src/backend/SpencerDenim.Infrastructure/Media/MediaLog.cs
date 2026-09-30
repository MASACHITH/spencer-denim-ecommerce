using Microsoft.Extensions.Logging;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// Source-generated log messages for the media library. The rest of the solution logs this way too
/// (<c>CatalogSeedRunner</c>), and the analyser insists on it: an interpolated log line allocates and
/// formats even when the level is switched off.
/// </summary>
internal static partial class MediaLog
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "Could not delete the temporary media file {TemporaryPath}.")]
    public static partial void TemporaryFileNotDeleted(ILogger logger, Exception exception, string temporaryPath);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Media asset {PublicId} soft-deleted; its files are purged after {PurgeAfterDays} days.")]
    public static partial void AssetSoftDeleted(ILogger logger, Guid publicId, int purgeAfterDays);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Error,
        Message = "Media upload {PublicId} failed after writing {WrittenCount} file(s); rolling back.")]
    public static partial void UploadFailed(ILogger logger, Exception exception, Guid publicId, int writtenCount);

    [LoggerMessage(
        EventId = 1106,
        Level = LogLevel.Information,
        Message = "Media upload {PublicId} lost a deduplication race and was withdrawn in favour of {WinnerPublicId}.")]
    public static partial void UploadDeduplicatedByIndex(ILogger logger, Guid publicId, Guid winnerPublicId);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Could not remove {StorageKey} while rolling back a failed upload.")]
    public static partial void RollbackFileNotRemoved(ILogger logger, Exception exception, string storageKey);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Error,
        Message = "Could not mark media asset {PublicId} as Failed.")]
    public static partial void CouldNotMarkFailed(ILogger logger, Exception exception, Guid publicId);

    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Error,
        Message = "Could not roll back the poster asset {AssetId} after a failed video upload. " +
                  "It stays in the library as an unused image.")]
    public static partial void PosterRollbackFailed(ILogger logger, Exception exception, int assetId);

    [LoggerMessage(
        EventId = 1110,
        Level = LogLevel.Information,
        Message = "Demo media placeholders skipped: no media storage is configured " +
                  "(set Media__PublicRoot and Media__PrivateRoot). The demo catalogue's images will 404.")]
    public static partial void DemoPlaceholdersSkipped(ILogger logger);

    [LoggerMessage(
        EventId = 1115,
        Level = LogLevel.Warning,
        Message = "Media:PlaceholderFontPath points at '{Path}', which does not exist. " +
                  "The demo placeholders will carry no labels; they stay unique and unmistakable without them.")]
    public static partial void PlaceholderFontFileMissing(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1116,
        Level = LogLevel.Warning,
        Message = "Media:PlaceholderFontPath points at '{Path}', which Skia could not open as a typeface. " +
                  "It has to be a TrueType or OpenType file — .woff and .woff2 are not readable. " +
                  "The demo placeholders will carry no labels.")]
    public static partial void PlaceholderFontFileUnreadable(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1117,
        Level = LogLevel.Information,
        Message = "Demo placeholder labels come from {Source}.")]
    public static partial void PlaceholderFontResolved(ILogger logger, string source);

    [LoggerMessage(
        EventId = 1111,
        Level = LogLevel.Warning,
        Message = "No usable font was found, so the demo placeholders carry no words — only the tinted ground, " +
                  "the diagonals, the colourway swatch and the identity block, which is what makes each one " +
                  "distinct. Set Media__PlaceholderFontPath to a .ttf or .otf file to get the labels back.")]
    public static partial void DemoPlaceholdersHaveNoFont(ILogger logger);

    [LoggerMessage(
        EventId = 1112,
        Level = LogLevel.Information,
        Message = "Generated placeholder images for {Generated} demo media asset(s); {AlreadyPresent} were already there.")]
    public static partial void DemoPlaceholdersGenerated(ILogger logger, int generated, int alreadyPresent);

    [LoggerMessage(
        EventId = 1113,
        Level = LogLevel.Warning,
        Message = "Media storage is not configured (Media__PublicRoot / Media__PrivateRoot are empty). " +
                  "Uploads answer 503 and /media answers 404 until they are set.")]
    public static partial void StorageNotConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 1114,
        Level = LogLevel.Information,
        Message = "Media volumes: public '{PublicRoot}', private '{PrivateRoot}' (separate, and only the first is served at /media).")]
    public static partial void StorageConfigured(ILogger logger, string publicRoot, string privateRoot);
}
