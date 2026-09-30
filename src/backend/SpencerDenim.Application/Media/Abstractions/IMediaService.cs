using SpencerDenim.Application.Media.Contracts;

namespace SpencerDenim.Application.Media.Abstractions;

/// <summary>
/// The media pipeline of doc 03 §3b: "Uploads go through <c>IMediaService</c> → <c>IFileStorage</c>".
/// <para>
/// This is the use-case port for ADM-18. It is a service rather than the one-handler-per-action shape the
/// rest of the Application layer uses, because doc 03 §3b names it and because the four operations share
/// one pipeline (detect → validate → hash → store → render → publish) rather than one line of
/// orchestration each; splitting them would move that pipeline into a helper the handlers all call, which
/// is this interface with an extra layer in front.
/// </para>
/// <para>
/// <b>Two guarantees hold for every path through it.</b> First, an upload that fails at any stage leaves
/// neither a file nor a row claiming <c>Ready</c>: the row is inserted as <c>Processing</c>, only becomes
/// <c>Ready</c> once every byte is on disk, and a failure rolls the files back and marks the row
/// <c>Failed</c>. Second, nothing about the upload is trusted — not the filename, not the client's
/// content type, not the extension; the type comes from the file signature and the storage key is built
/// from a GUID this server generated.
/// </para>
/// </summary>
public interface IMediaService
{
    /// <summary>
    /// Stores an image: verifies the signature, enforces the size and pixel ceilings, computes the
    /// SHA-256, writes the metadata-stripped original, and renders the WebP set of
    /// <c>MediaRenditions.Current</c>.
    /// </summary>
    Task<MediaUploadOutcome> UploadImageAsync(UploadImageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an MP4 (H.264) and its poster image. No conversion happens: the bytes are stored as they
    /// arrived and the poster is a separate image asset, linked by <c>PosterAssetId</c> and by a
    /// <c>MediaUsages</c> row, so the poster cannot be deleted while the video needs it.
    /// </summary>
    Task<MediaUploadOutcome> UploadVideoAsync(UploadVideoRequest request, CancellationToken cancellationToken);

    /// <summary>One asset with its "used in…" list, or null when there is no such asset.</summary>
    Task<MediaAssetDetailDto?> GetAsync(Guid publicId, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes an asset, but only when nothing uses it (doc 08 §3.3). The files stay until the
    /// purge job of <c>MediaRetention</c> removes them.
    /// </summary>
    Task<MediaDeleteOutcome> DeleteAsync(Guid publicId, int? deletedByUserId, CancellationToken cancellationToken);
}

/// <summary>What the serving endpoint needs to answer one request for a file under <c>/media</c>.</summary>
/// <param name="StorageKey">The validated key to open.</param>
/// <param name="ContentType">From a fixed allow-list, never from a free-text database column.</param>
/// <param name="ETag">A strong ETag, quoted, derived from the asset checksum and the rendition code.</param>
/// <param name="SizeBytes">Length, for <c>Content-Length</c> and range arithmetic.</param>
/// <param name="LastModifiedUtc">For <c>Last-Modified</c>.</param>
public sealed record MediaFileDescriptor(
    string StorageKey,
    string ContentType,
    string ETag,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

/// <summary>
/// Resolves a key taken off a <c>/media</c> URL to a file that may be served, or to nothing.
/// <para>
/// Separate from <see cref="IMediaService"/> on purpose: serving is anonymous, read-only and on the hot
/// path, and it must be impossible for it to reach an upload, a delete or the private volume. It answers
/// <see langword="null"/> — never an exception, never a 403 — for a key that is malformed, private,
/// unknown, soft-deleted, not <c>Ready</c>, or whose file is missing, so nothing about the library leaks
/// through the difference between two failures.
/// </para>
/// </summary>
public interface IPublicMediaFiles
{
    Task<MediaFileDescriptor?> ResolveAsync(string? storageKey, CancellationToken cancellationToken);
}
