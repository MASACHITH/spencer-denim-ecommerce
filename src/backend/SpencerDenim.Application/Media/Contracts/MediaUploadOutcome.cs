using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Media.Contracts;

/// <summary>
/// One file handed to <c>IMediaService</c>.
/// <para>
/// <see cref="Content"/> is a <b>seekable</b> stream the caller owns and disposes. The HTTP layer has
/// already streamed the request body to a temporary file under its own byte cap, so nothing here can be
/// asked to buffer an unbounded body, and the pipeline can read the header, hash the bytes and decode
/// the image without three copies in memory. The service checks the length against the configured
/// ceiling again anyway: the cap belongs to the transport, the rule belongs here.
/// </para>
/// </summary>
/// <param name="Content">The uploaded bytes, positioned anywhere; the service seeks.</param>
/// <param name="FileName">The client's filename. Recorded, sanitised, and never used to build a key or a type.</param>
public sealed record MediaUploadFile(Stream Content, string FileName);

/// <summary>An image upload (ADM-18).</summary>
/// <param name="Visibility">
/// <see cref="MediaVisibility.Public"/> for library media. <see cref="MediaVisibility.Private"/> is the
/// bank-slip path, which this milestone leaves unused (doc 08 N10, W3).
/// </param>
/// <param name="UploadedByUserId">The signed-in staff user, or null for a guest upload.</param>
public sealed record UploadImageRequest(
    MediaUploadFile File,
    MediaVisibility Visibility,
    string? AltText,
    string? Title,
    int? UploadedByUserId);

/// <summary>
/// A video upload. The poster travels with it because doc 08 §3.3's check constraint
/// (<c>MediaType = 2 ⇒ PosterAssetId NOT NULL</c>) means a video row cannot exist without one, and
/// nothing here converts or extracts frames — the VPS has 2 vCPUs (doc 03 §3b).
/// </summary>
public sealed record UploadVideoRequest(
    MediaUploadFile File,
    MediaUploadFile Poster,
    string? AltText,
    string? Title,
    int? UploadedByUserId);

/// <summary>Why an upload was refused. Each value maps to exactly one HTTP status at the endpoint.</summary>
public enum MediaUploadRejection
{
    /// <summary>No bytes, or a filename that is nothing but whitespace. 400.</summary>
    Empty = 1,

    /// <summary>Past the configured ceiling for this visibility and type. 413.</summary>
    TooLarge,

    /// <summary>The file signature is not one this library accepts. 415.</summary>
    UnsupportedType,

    /// <summary>
    /// The right kind of file, but unusable: an image the decoder rejects or that declares more pixels
    /// than <c>Media:MaxImageMegapixels</c>, or an MP4 with no H.264 video track. 422.
    /// </summary>
    Unprocessable,

    /// <summary>A video upload arrived without a poster image. 400.</summary>
    PosterRequired,

    /// <summary>Media storage is not configured on this deployment. 503.</summary>
    StorageNotConfigured,

    /// <summary>Storage accepted nothing: out of disk, permission denied, a volume that is not mounted. 500.</summary>
    StorageFailure,
}

/// <summary>The outcome of an upload.</summary>
public abstract record MediaUploadOutcome
{
    private MediaUploadOutcome()
    {
    }

    /// <summary>A new asset was created and is <c>Ready</c>. 201.</summary>
    public sealed record Created(MediaAssetDto Asset) : MediaUploadOutcome;

    /// <summary>
    /// These exact bytes were already in the library, so the existing asset is returned and nothing was
    /// written. 200, not 201 — the caller did not create anything. See <c>IMediaService</c> for why.
    /// </summary>
    public sealed record Duplicate(MediaAssetDto Asset) : MediaUploadOutcome;

    /// <summary>Refused. <paramref name="Message"/> is safe to show a staff member.</summary>
    public sealed record Rejected(MediaUploadRejection Reason, string Message) : MediaUploadOutcome;
}

/// <summary>The outcome of a delete (doc 08 §3.3: soft delete, allowed only with no usages).</summary>
public abstract record MediaDeleteOutcome
{
    private MediaDeleteOutcome()
    {
    }

    /// <summary>Soft-deleted. The files are purged after 30 days (<c>MediaRetention</c>). 204.</summary>
    public sealed record Deleted : MediaDeleteOutcome;

    /// <summary>No such asset, or it was already soft-deleted. 404.</summary>
    public sealed record NotFound : MediaDeleteOutcome;

    /// <summary>Something still uses it. 409, with the list so the panel can say what. </summary>
    public sealed record InUse(IReadOnlyList<MediaUsageDto> Usages) : MediaDeleteOutcome;
}
